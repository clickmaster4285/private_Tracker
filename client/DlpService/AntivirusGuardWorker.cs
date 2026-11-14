using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// 24/7 antivirus guard loop (Windows LocalSystem). Runs whenever the tracker is installed
/// (<c>ALPHA_AV_ENFORCE</c>, default true — independent of the web policy so no admin or
/// employee action can switch it off from the dashboard):
/// <list type="number">
/// <item>Every <c>ALPHA_AV_GUARD_SEC</c> (30 s): <see cref="DefenderGuard.Enforce"/> reverts any
/// weakening of Defender and raises an <c>av_tamper</c> alert.</item>
/// <item>Every pass: new Defender Operational events → <c>malware</c> / <c>av_tamper</c> alerts
/// (synced by <see cref="AlertSyncWorker"/> into the existing DLP Alerts inbox).</item>
/// <item>Every <c>ALPHA_AV_STATUS_SEC</c> (300 s) or on change: engine status →
/// <c>POST /api/v1/antivirus/status/sync</c> (fleet view at <c>/antivirus</c>).</item>
/// <item>Signatures older than a day → <c>Update-MpSignature</c> (≥ 4 h apart); no quick scan
/// in <c>ALPHA_AV_QUICK_SCAN_HOURS</c> → background quick scan.</item>
/// </list>
/// A registered third-party AV that owns protection is reported, never fought.
/// </summary>
public sealed class AntivirusGuardWorker : BackgroundService
{
    private const string KeyEventBookmark = "av_event_bookmark";
    private static readonly TimeSpan TamperAlertEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DriftAlertEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SignatureRetryEvery = TimeSpan.FromHours(4);
    private static readonly TimeSpan ScanRetryEvery = TimeSpan.FromHours(6);

    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AntivirusGuardWorker> _logger;

    private DefenderGuard.AvStatus? _status;
    private DateTimeOffset _lastStatusRead = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStatusSent = DateTimeOffset.MinValue;
    private string _lastSentFingerprint = "";
    private DateTimeOffset _lastTamperAlert = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDriftAlert = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastTamperAt;
    private DateTimeOffset? _lastEnforceAt;
    private int _tamperCount24h;
    private DateTimeOffset _tamperWindowStart = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastSignatureAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastScanAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFullScanAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastExploitProtection = DateTimeOffset.MinValue;
    private int _backgroundJob; // 0 idle, 1 signature/scan running
    private readonly Dictionary<string, DateTimeOffset> _recentThreats = new(StringComparer.OrdinalIgnoreCase);

    public AntivirusGuardWorker(
        DlpConfig cfg, PolicyStore store, IHttpClientFactory httpFactory, ILogger<AntivirusGuardWorker> logger)
    {
        _cfg = cfg;
        _store = store;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!_cfg.AvEnforce)
        {
            _logger.LogWarning("AntivirusGuardWorker parked (ALPHA_AV_ENFORCE=false)");
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        var interval = TimeSpan.FromSeconds(Math.Max(10, _cfg.AvGuardSec));
        _logger.LogInformation(
            "AntivirusGuardWorker running (guard every {Guard}s, status every {Status}s, quick scan every {Scan}h)",
            interval.TotalSeconds, _cfg.AvStatusSec, _cfg.AvQuickScanHours);

        // Let the rest of the service settle (policy apply, pipe) before the first PowerShell burst.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AntivirusGuardWorker tick failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _tamperWindowStart > TimeSpan.FromHours(24))
        {
            _tamperWindowStart = now;
            _tamperCount24h = 0;
        }

        // 1. Status (cheap enough at the guard cadence when stale; always before first enforce).
        var statusDue = _status is null ||
                        now - _lastStatusRead >= TimeSpan.FromSeconds(Math.Max(30, _cfg.AvStatusSec / 2));
        if (statusDue)
        {
            _status = await Task.Run(DefenderGuard.ReadStatus, ct);
            _lastStatusRead = now;
        }

        // 2. Enforce — unless a registered third-party AV legitimately owns protection.
        if (_status is { } st && !DefenderGuard.ThirdPartyOwnsProtection(st))
        {
            var hardening = new DefenderGuard.HardeningOptions(
                Enabled: _cfg.AvHardening,
                AsrAudit: _cfg.AvAsrAudit,
                ControlledFolderAccess: _cfg.AvControlledFolderAccess,
                CfaAudit: _cfg.AvCfaAudit,
                ScanCpuLimit: _cfg.AvScanCpuLimit,
                QuarantinePurgeDays: _cfg.AvQuarantinePurgeDays);
            var res = await Task.Run(() => DefenderGuard.Enforce(_cfg.AvAllowedExclusions, hardening), ct);
            _lastEnforceAt = now;
            await HandleEnforceResultAsync(res, ct);
            if (res.Changed.Count > 0)
            {
                // Re-read so the server sees the healed state, not the tampered one.
                _status = await Task.Run(DefenderGuard.ReadStatus, ct);
                _lastStatusRead = DateTimeOffset.UtcNow;
            }
        }

        // 3. Defender detections / protection-off events → alerts.
        await DrainEventsAsync(ct);

        // 4. Keep signatures fresh + make sure a periodic quick scan happens.
        MaybeStartBackgroundJobs(now);

        // 5. Fleet status to the server.
        await MaybeSendStatusAsync(ct);

        // 6. Admin-side remote fix: pull + run any queued commands (scan / remediate).
        await DrainCommandsAsync(ct);
    }

    // ── Remote commands (admin-side fix) ──────────────────────────────────────

    private sealed record CommandItem(long Id, string Type, string Arg);

    private async Task DrainCommandsAsync(CancellationToken ct)
    {
        // Only pull when no heavy job (scheduled scan / previous command) is running, so a
        // command the server marks 'running' on dispatch is actually executed, not shelved.
        if (Volatile.Read(ref _backgroundJob) != 0) return;
        _cfg.RefreshCredentials();
        if (string.IsNullOrWhiteSpace(_cfg.ServerUrl) || string.IsNullOrWhiteSpace(_cfg.DeviceToken))
            return;

        List<CommandItem> cmds;
        try { cmds = await PullCommandsAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Antivirus command pull failed");
            return;
        }
        if (cmds.Count == 0) return;
        if (Interlocked.CompareExchange(ref _backgroundJob, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var cmd in cmds)
                {
                    _logger.LogInformation("Antivirus remote command {Type} (id {Id})", cmd.Type, cmd.Id);
                    (bool ok, string detail) result;
                    try { result = ExecuteCommand(cmd); }
                    catch (Exception ex) { result = (false, ex.GetType().Name + ": " + ex.Message); }
                    await _store.AuditAsync("av_command",
                        $"id={cmd.Id} type={cmd.Type} ok={result.ok} {result.detail}", CancellationToken.None);
                    await ReportResultAsync(cmd.Id, result.ok ? "done" : "failed", result.detail, CancellationToken.None);
                }
                _lastStatusRead = DateTimeOffset.MinValue; // refresh status after scans/remediation
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Antivirus command execution failed");
            }
            finally
            {
                Volatile.Write(ref _backgroundJob, 0);
            }
        }, CancellationToken.None);
    }

    private static (bool Ok, string Detail) ExecuteCommand(CommandItem cmd) => cmd.Type switch
    {
        "scan_quick" => DefenderGuard.QuickScan(),
        "scan_full" => DefenderGuard.FullScan(),
        "scan_custom" => DefenderGuard.CustomScan(cmd.Arg),
        "update_signatures" => DefenderGuard.UpdateSignatures(),
        "remove_threats" => DefenderGuard.RemoveThreats(),
        "restore_threat" => DefenderGuard.RestoreThreat(cmd.Arg),
        _ => (false, "unknown_command_type"),
    };

    private async Task<List<CommandItem>> PullCommandsAsync(CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("dlp");
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_cfg.ServerUrl}/api/v1/antivirus/commands/pending");
        req.Headers.Authorization = new AuthenticationHeaderValue("Device", _cfg.DeviceToken);
        req.Headers.TryAddWithoutValidation("X-Client-Platform", "windows");

        var list = new List<CommandItem>();
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return list;
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("commands", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in arr.EnumerateArray())
        {
            var id = e.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var i) ? i : 0;
            var type = e.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "" : "";
            var arg = e.TryGetProperty("arg", out var aEl) ? aEl.GetString() ?? "" : "";
            if (id > 0 && type.Length > 0) list.Add(new CommandItem(id, type, arg));
        }
        return list;
    }

    private async Task ReportResultAsync(long id, string status, string detail, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("dlp");
            var payload = new Dictionary<string, object?> { ["id"] = id, ["status"] = status, ["result"] = detail };
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{_cfg.ServerUrl}/api/v1/antivirus/commands/result")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Device", _cfg.DeviceToken);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                _logger.LogDebug("Antivirus command result HTTP {Status}", (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Antivirus command result report failed");
        }
    }

    private async Task HandleEnforceResultAsync(DefenderGuard.EnforceResult res, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Hardening applied (ASR / CFA / scan tuning) is NOT tamper — the guard configures
        // these the first time it sees an unhardened PC. Audit only; never a tamper alert.
        if (res.Hardened.Count > 0)
        {
            var applied = string.Join(", ", res.Hardened.Take(30));
            _logger.LogInformation("Antivirus guard applied hardening: {Applied}", applied);
            await _store.AuditAsync("av_hardening_applied", applied, ct);
        }

        if (res.Changed.Count > 0)
        {
            _lastTamperAt = now;
            _tamperCount24h++;
            var detail = string.Join(", ", res.Changed.Take(20));
            _logger.LogWarning("Antivirus guard reverted Defender tampering: {Changed}", detail);
            await _store.AuditAsync("av_guard_revert", detail, ct);

            if (now - _lastTamperAlert >= TamperAlertEvery)
            {
                _lastTamperAlert = now;
                await EnqueueAsync(Guid.NewGuid().ToString(), "av_tamper", "high", "blocked", now,
                    new Dictionary<string, string>
                    {
                        ["detail"] = "Antivirus protection was weakened on this PC and restored automatically",
                        ["changes"] = detail,
                    }, ct);
            }
        }

        if (res.RemainingDrift.Count > 0 && now - _lastDriftAlert >= DriftAlertEvery)
        {
            _lastDriftAlert = now;
            var drift = string.Join(", ", res.RemainingDrift);
            _logger.LogWarning("Antivirus guard could not restore: {Drift}", drift);
            await _store.AuditAsync("av_guard_drift", drift, ct);
            await EnqueueAsync(Guid.NewGuid().ToString(), "av_tamper", "critical", "blocked_failed", now,
                new Dictionary<string, string>
                {
                    ["detail"] = "Antivirus protection is weakened and Windows refused the automatic fix",
                    ["settings"] = drift,
                    ["errors"] = string.Join(" | ", res.Errors.Take(5)),
                }, ct);
        }
        else if (res.Errors.Count > 0)
        {
            _logger.LogDebug("Antivirus guard errors: {Errors}", string.Join(" | ", res.Errors.Take(5)));
        }
    }

    // ── Events ──────────────────────────────────────────────────────────────

    private async Task DrainEventsAsync(CancellationToken ct)
    {
        var bookmarkRaw = await _store.GetKvAsync(KeyEventBookmark, ct);
        if (!long.TryParse(bookmarkRaw, out var bookmark))
        {
            // First run: start from "now" so an install does not replay years of history.
            bookmark = await Task.Run(DefenderGuard.LatestRecordId, ct);
            await _store.SetKvAsync(KeyEventBookmark, bookmark.ToString(), ct);
            _logger.LogInformation("Antivirus guard event bookmark initialised at record {Id}", bookmark);
            return;
        }

        List<DefenderGuard.DefenderEvent> events;
        try
        {
            events = await Task.Run(() => DefenderGuard.ReadEvents(bookmark), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Defender event log read failed");
            return;
        }
        if (events.Count == 0) return;

        foreach (var ev in events)
        {
            await HandleEventAsync(ev, ct);
            bookmark = Math.Max(bookmark, ev.RecordId);
        }
        await _store.SetKvAsync(KeyEventBookmark, bookmark.ToString(), ct);
    }

    private async Task HandleEventAsync(DefenderGuard.DefenderEvent ev, CancellationToken ct)
    {
        // Deterministic id per log record: a crash before the bookmark is saved re-reads the
        // same record and INSERT OR IGNORE keeps a single alert.
        var id = StableGuid("defender:" + ev.RecordId + ":" + ev.EventId).ToString();
        string D(string k) => ev.Data.TryGetValue(k, out var v) ? v : "";

        switch (ev.EventId)
        {
            case 1006:
            case 1116:
            {
                var threat = D("Threat Name");
                var path = D("Path");
                var key = threat + "|" + path;
                if (_recentThreats.TryGetValue(key, out var seen) && ev.At - seen < TimeSpan.FromMinutes(5))
                    return;
                _recentThreats[key] = ev.At;
                if (_recentThreats.Count > 500)
                {
                    foreach (var old in _recentThreats.Where(kv => ev.At - kv.Value > TimeSpan.FromHours(1))
                                 .Select(kv => kv.Key).ToList())
                        _recentThreats.Remove(old);
                }

                _logger.LogWarning("Malware detected: {Threat} at {Path}", threat, path);
                await EnqueueAsync(id, "malware", MapSeverity(D("Severity Name")), "blocked", ev.At,
                    Evidence(ev, "Threat Name", "Severity Name", "Category Name", "Path", "Process Name",
                        "Detection User", "Detection Source Name", "Action Name", "Threat ID", "Origin Name"),
                    ct);
                break;
            }
            case 1008:
            case 1118:
            case 1119:
                await EnqueueAsync(id, "malware", "critical", "blocked_failed", ev.At,
                    Evidence(ev, "Threat Name", "Severity Name", "Path", "Process Name", "Action Name",
                        "Error Description", "Detection User"), ct);
                break;
            case 1007:
            case 1117:
                await _store.AuditAsync("av_threat_action",
                    $"threat={D("Threat Name")} action={D("Action Name")} path={D("Path")}", ct);
                break;
            case 5001:
            case 5010:
            case 5012:
                _lastTamperAt = ev.At;
                await EnqueueAsync(id, "av_tamper", "critical", "alerted", ev.At, new Dictionary<string, string>
                {
                    ["detail"] = ev.EventId switch
                    {
                        5001 => "Real-time protection was turned off",
                        5010 => "Scanning for malware and unwanted software was turned off",
                        _ => "Scanning for viruses was turned off",
                    },
                    ["eventId"] = ev.EventId.ToString(),
                }, ct);
                break;
            case 5013:
                await EnqueueAsync(id, "av_tamper", "medium", "blocked", ev.At,
                    Evidence(ev, "Changed Type", "Value"), ct);
                break;
            default:
                await _store.AuditAsync("av_config_event", $"id={ev.EventId} {D("New Value")}", ct);
                break;
        }
    }

    // ── Signatures / scans ──────────────────────────────────────────────────

    private void MaybeStartBackgroundJobs(DateTimeOffset now)
    {
        if (_status is not { Available: true } st || DefenderGuard.ThirdPartyOwnsProtection(st))
            return;
        if (Volatile.Read(ref _backgroundJob) != 0)
            return;

        // Exploit-protection mitigations: apply once at start, then refresh ~every 12 h.
        if (_cfg.AvHardening && now - _lastExploitProtection >= TimeSpan.FromHours(12))
        {
            _lastExploitProtection = now;
            StartJob("exploit protection", DefenderGuard.EnsureExploitProtection);
            return;
        }

        if (st.SignatureAgeDays >= 1 && now - _lastSignatureAttempt >= SignatureRetryEvery)
        {
            _lastSignatureAttempt = now;
            StartJob("signature update", DefenderGuard.UpdateSignatures);
            return;
        }

        // Full scan (weekly by default) takes priority over the quick scan when both are due.
        if (_cfg.AvFullScanDays > 0 &&
            (st.FullScanAt is null || now - st.FullScanAt.Value >= TimeSpan.FromDays(_cfg.AvFullScanDays)) &&
            now - _lastFullScanAttempt >= TimeSpan.FromHours(12))
        {
            _lastFullScanAttempt = now;
            StartJob("full scan", DefenderGuard.FullScan);
            return;
        }

        if (_cfg.AvQuickScanHours > 0 &&
            (st.QuickScanAt is null || now - st.QuickScanAt.Value >= TimeSpan.FromHours(_cfg.AvQuickScanHours)) &&
            now - _lastScanAttempt >= ScanRetryEvery)
        {
            _lastScanAttempt = now;
            StartJob("quick scan", DefenderGuard.QuickScan);
        }
    }

    private void StartJob(string name, Func<(bool Ok, string Detail)> job)
    {
        if (Interlocked.CompareExchange(ref _backgroundJob, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Antivirus guard: starting {Job}", name);
                var (ok, detail) = job();
                _logger.LogInformation("Antivirus guard: {Job} finished ok={Ok} {Detail}", name, ok, detail);
                await _store.AuditAsync("av_" + name.Replace(' ', '_'), $"ok={ok} {detail}", CancellationToken.None);
                _lastStatusRead = DateTimeOffset.MinValue; // refresh status next tick
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Antivirus guard {Job} failed", name);
            }
            finally
            {
                Volatile.Write(ref _backgroundJob, 0);
            }
        });
    }

    // ── Status sync ─────────────────────────────────────────────────────────

    private async Task MaybeSendStatusAsync(CancellationToken ct)
    {
        if (_status is not { } st) return;
        _cfg.RefreshCredentials();
        if (string.IsNullOrWhiteSpace(_cfg.ServerUrl) || string.IsNullOrWhiteSpace(_cfg.DeviceToken))
            return;

        var health = DefenderGuard.Health(st);
        var fingerprint = string.Join("|", health, st.RealTimeEnabled, st.SignatureVersion, st.TamperProtected,
            st.QuickScanAt, st.FullScanAt, st.ExclusionCount, _lastTamperAt,
            st.AsrBlock, st.ControlledFolderAccess, st.NetworkProtectionMode, st.MemoryIntegrityEnabled,
            st.CredentialGuardEnabled, st.ExploitProtectionEnabled, st.SmartScreenEnabled, st.QuarantinedThreatCount);
        var now = DateTimeOffset.UtcNow;
        if (fingerprint == _lastSentFingerprint &&
            now - _lastStatusSent < TimeSpan.FromSeconds(Math.Max(60, _cfg.AvStatusSec)))
            return;

        var payload = new Dictionary<string, object?>
        {
            ["health"] = health,
            ["productName"] = st.ProductName,
            ["runningMode"] = st.RunningMode,
            ["serviceEnabled"] = st.ServiceEnabled,
            ["antivirusEnabled"] = st.AntivirusEnabled,
            ["realTimeEnabled"] = st.RealTimeEnabled,
            ["behaviorMonitorEnabled"] = st.BehaviorMonitorEnabled,
            ["onAccessEnabled"] = st.OnAccessEnabled,
            ["ioavEnabled"] = st.IoavEnabled,
            ["tamperProtected"] = st.TamperProtected,
            ["cloudEnabled"] = st.CloudEnabled,
            ["puaEnabled"] = st.PuaEnabled,
            ["networkProtectionEnabled"] = st.NetworkProtectionEnabled,
            ["signatureVersion"] = st.SignatureVersion,
            ["signatureAgeDays"] = st.SignatureAgeDays,
            ["signatureUpdatedAt"] = st.SignatureUpdatedAt?.UtcDateTime.ToString("o"),
            ["quickScanAt"] = st.QuickScanAt?.UtcDateTime.ToString("o"),
            ["fullScanAt"] = st.FullScanAt?.UtcDateTime.ToString("o"),
            ["engineVersion"] = st.EngineVersion,
            ["thirdPartyProducts"] = string.Join("; ", st.ThirdPartyProducts),
            ["exclusionCount"] = st.ExclusionCount,
            ["asrConfigured"] = st.AsrConfigured,
            ["asrBlock"] = st.AsrBlock,
            ["controlledFolderAccess"] = st.ControlledFolderAccess,
            ["networkProtectionMode"] = st.NetworkProtectionMode,
            ["smartScreenEnabled"] = st.SmartScreenEnabled,
            ["memoryIntegrityEnabled"] = st.MemoryIntegrityEnabled,
            ["credentialGuardEnabled"] = st.CredentialGuardEnabled,
            ["emailScanEnabled"] = st.EmailScanEnabled,
            ["exploitProtectionEnabled"] = st.ExploitProtectionEnabled,
            ["quarantinedThreatCount"] = st.QuarantinedThreatCount,
            ["hardeningEnabled"] = _cfg.AvHardening,
            ["guardEnabled"] = _cfg.AvEnforce,
            ["lastEnforceAt"] = _lastEnforceAt?.UtcDateTime.ToString("o"),
            ["lastTamperAt"] = _lastTamperAt?.UtcDateTime.ToString("o"),
            ["tamperCount24h"] = _tamperCount24h,
            ["agentVersion"] = AgentVersion,
            ["error"] = st.Error,
            ["reportedAt"] = now.UtcDateTime.ToString("o"),
        };

        var client = _httpFactory.CreateClient("dlp");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.ServerUrl}/api/v1/antivirus/status/sync")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Device", _cfg.DeviceToken);
        req.Headers.TryAddWithoutValidation("X-Client-Platform", "windows");
        try
        {
            using var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                _lastSentFingerprint = fingerprint;
                _lastStatusSent = now;
            }
            else
            {
                // 429/503 (admission) or older server without the route: retry next tick.
                _logger.LogDebug("Antivirus status sync HTTP {Status}", (int)resp.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Antivirus status sync failed");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task EnqueueAsync(string id, string type, string severity, string action,
        DateTimeOffset at, Dictionary<string, string> evidence, CancellationToken ct)
    {
        var version = (await _store.GetAppliedAsync(ct)).Version;
        await _store.EnqueueAlertAsync(id, null, version, type, severity, action, at,
            JsonSerializer.Serialize(evidence), ct);
    }

    private static Dictionary<string, string> Evidence(DefenderGuard.DefenderEvent ev, params string[] keys)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eventId"] = ev.EventId.ToString(),
            ["source"] = "Microsoft Defender",
        };
        foreach (var k in keys)
        {
            if (ev.Data.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                map[ToCamel(k)] = v.Length > 1000 ? v[..1000] : v;
        }
        return map;
    }

    private static string ToCamel(string label)
    {
        var parts = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return label;
        var sb = new StringBuilder(parts[0].ToLowerInvariant());
        foreach (var p in parts.Skip(1))
            sb.Append(char.ToUpperInvariant(p[0])).Append(p[1..].ToLowerInvariant());
        return sb.ToString();
    }

    private static string MapSeverity(string defenderSeverity) => defenderSeverity.Trim().ToLowerInvariant() switch
    {
        "severe" => "critical",
        "high" => "high",
        "moderate" or "medium" => "medium",
        "low" => "low",
        _ => "high",
    };

    private static Guid StableGuid(string seed)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // RFC 4122 v3 layout
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }

    private static readonly string AgentVersion =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];
}
