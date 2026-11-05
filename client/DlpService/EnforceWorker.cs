using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Fast-path enforcer.
/// P1: USBSTOR Start=4 when any usb_* block rule is published.
/// P2: hosts DNS sink for web_navigate / shadowDeny domains (schedule-aware).
/// </summary>
public sealed class EnforceWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly RemovableDriveMonitor _drives;
    private readonly WebBlockState _webBlock;
    private readonly ILogger<EnforceWorker> _logger;
    private long _lastVersion = -1;
    private bool? _lastUsbBlock;
    private string _lastWebFingerprint = "\0";
    private DateTimeOffset _lastAccessDeniedLog = DateTimeOffset.MinValue;
    private bool _accessDeniedAlertQueued;
    private bool _webAccessDeniedAlertQueued;
    private string? _lastLockdownState;
    private bool _lockdownOverflowAudited;
    private DateTimeOffset _lastLockdownWarn = DateTimeOffset.MinValue;

    public EnforceWorker(
        DlpConfig cfg,
        PolicyStore store,
        RemovableDriveMonitor drives,
        WebBlockState webBlock,
        ILogger<EnforceWorker> logger)
    {
        _cfg = cfg;
        _store = store;
        _drives = drives;
        _webBlock = webBlock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.Enabled)
        {
            _logger.LogInformation("EnforceWorker parked (ALPHA_DLP_ENABLED=false)");
            // Never strand a PC behind lockdown filters left by an earlier enabled run.
            try
            {
                var parkClear = await Task.Run(LockdownNetworkEnforcer.Clear, stoppingToken);
                if (parkClear.Changed || parkClear.Detail.StartsWith("lock_clear_failed", StringComparison.Ordinal))
                    _logger.LogWarning("Parked: cleared lockdown filters ({Detail})", parkClear.Detail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lockdown clear on park failed");
            }
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        _logger.LogInformation(
            "EnforceWorker running (usbBlock={Usb}, webBlock={Web})",
            _cfg.UsbBlock, _cfg.WebBlock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ApplyAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EnforceWorker tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ApplyAsync(CancellationToken ct)
    {
        var (version, hash, bundleJson) = await _store.GetAppliedAsync(ct);
        var bundle = PolicyBundleParser.Parse(bundleJson);
        var now = DateTimeOffset.UtcNow;
        var wantUsbBlock = _cfg.UsbBlock && PolicyBundleParser.WantsUsbStorageBlock(bundle);
        var wantDomains = _cfg.WebBlock
            ? PolicyBundleParser.ActiveWebBlockDomains(bundle, now)
            : Array.Empty<string>();
        var webFp = PolicyBundleParser.WebBlockFingerprint(wantDomains);

        if (version != _lastVersion)
        {
            _lastVersion = version;
            // New published policy must always re-sync hosts (clear OR re-block).
            // Without this, empty→same-domain fingerprints could skip Apply after a clear.
            _lastWebFingerprint = "\0";
            _accessDeniedAlertQueued = false;
            _webAccessDeniedAlertQueued = false;
            await _store.AuditAsync("enforce_reload", $"version={version} hash={hash}", ct);
            _logger.LogInformation("EnforceWorker loaded policy version {Version}", version);
            if (wantUsbBlock)
                _logger.LogInformation("Policy requests USB mass-storage BLOCK");
            if (wantDomains.Count > 0)
                _logger.LogInformation("Policy requests WEB domain BLOCK ({Count} hosts)", wantDomains.Count);
            else if (version == 0)
                _logger.LogInformation(
                    "No allow-list publish yet (version 0). After login, web is default-deny (bootstrap only) until Allowlists → Publish.");
        }

        _drives.SetUsbBlocked(wantUsbBlock);

        await ApplyUsbAsync(bundle, version, wantUsbBlock, ct);
        // Lockdown (allow-world) first; explicit web-block domains below still apply inside it.
        await ApplyLockdownAsync(bundle, ct);
        await ApplyWebAsync(bundle, version, wantDomains, webFp, ct);
    }

    /// <summary>
    /// Default-deny web after DeviceAuth. ON unless paused or the published bundle sets
    /// <c>lockdown: false</c>. Publish is only required to expand <c>allow.hosts</c>, not to block.
    /// </summary>
    private async Task ApplyLockdownAsync(PolicyBundle bundle, CancellationToken ct)
    {
        _cfg.RefreshCredentials();
        var hasCreds = !string.IsNullOrWhiteSpace(_cfg.ServerUrl) &&
                       !string.IsNullOrWhiteSpace(_cfg.DeviceToken);
        var pausedUntil = await _store.GetLockdownPausedUntilAsync(ct);
        var paused = pausedUntil is { } p && p > DateTimeOffset.UtcNow;
        var want = hasCreds && !paused && bundle.LockdownEnabled;

        if (!want)
        {
            var clear = await Task.Run(LockdownNetworkEnforcer.Clear, ct);
            var state = paused ? "paused" : "off";
            if (clear.Changed || _lastLockdownState is not (null or "off" or "paused"))
            {
                await _store.AuditAsync("lockdown_clear", $"state={state} detail={clear.Detail}", ct);
                _logger.LogInformation("Lockdown filters CLEARED (state={State}, detail={Detail})", state, clear.Detail);
            }
            if (!clear.Detail.StartsWith("lock_clear_failed", StringComparison.Ordinal))
                _lastLockdownState = state;
            _lockdownOverflowAudited = false;
            return;
        }

        var hosts = bundle.PlatformAllowHosts.Concat(bundle.AllowHosts).ToList();
        var serverUrl = _cfg.ServerUrl;
        var result = await Task.Run(() => LockdownNetworkEnforcer.Apply(hosts, serverUrl), ct);

        if (result.Overflow && !_lockdownOverflowAudited)
        {
            _lockdownOverflowAudited = true;
            await _store.AuditAsync(
                "lockdown_ip_overflow",
                $"cap={LockdownNetworkEnforcer.MaxAllowIps} hosts={hosts.Count}",
                ct);
            _logger.LogWarning(
                "Lockdown allow set exceeded {Cap} IPs — extra IPs dropped (bootstrap kept first)",
                LockdownNetworkEnforcer.MaxAllowIps);
        }
        else if (!result.Overflow)
        {
            _lockdownOverflowAudited = false;
        }

        if (result.Applied)
        {
            if (result.Changed || _lastLockdownState != "on")
            {
                await _store.AuditAsync(
                    "lockdown_apply",
                    $"hosts={hosts.Count} ips={result.AllowIps} detail={result.Detail}",
                    ct);
                _logger.LogInformation(
                    "Lockdown ENFORCED (hosts={Hosts}, allowIps={Ips}, detail={Detail})",
                    hosts.Count, result.AllowIps, result.Detail);
            }
            _lastLockdownState = "on";
            return;
        }

        // Not applied (bootstrap unresolved / access denied / netsh error / stale gen): never
        // strand the PC — existing rules are left as-is, retry next tick; log at most every 2 min.
        if (result.Detail != "stale_generation" &&
            DateTimeOffset.UtcNow - _lastLockdownWarn > TimeSpan.FromMinutes(2))
        {
            _lastLockdownWarn = DateTimeOffset.UtcNow;
            _logger.LogWarning("Lockdown not applied: {Detail}", result.Detail);
            await _store.AuditAsync("lockdown_apply_failed", result.Detail, ct);
        }
    }

    private async Task ApplyUsbAsync(
        PolicyBundle bundle, long version, bool wantUsbBlock, CancellationToken ct)
    {
        // Do not touch the registry just to "confirm allow" on cold start with no block rules.
        if (!wantUsbBlock && _lastUsbBlock != true)
        {
            _lastUsbBlock = false;
            return;
        }

        if (_lastUsbBlock == wantUsbBlock)
            return;

        var result = UsbStorEnforcer.Apply(wantUsbBlock);
        await _store.AuditAsync(
            "usbstor_apply",
            $"block={wantUsbBlock} changed={result.Changed} detail={result.Detail} prev={result.PreviousStart}",
            ct);

        if (result.Detail == "access_denied_need_admin")
        {
            if (DateTimeOffset.UtcNow - _lastAccessDeniedLog > TimeSpan.FromMinutes(2))
            {
                _lastAccessDeniedLog = DateTimeOffset.UtcNow;
                _logger.LogWarning(
                    "USBSTOR enforce needs Administrator / LocalSystem. " +
                    $"Right-click {ServiceIdentity.ServiceName}.exe → Run as administrator, or install as a Windows Service.");
            }

            if (!_accessDeniedAlertQueued && wantUsbBlock)
            {
                _accessDeniedAlertQueued = true;
                var rule = PolicyBundleParser.FirstUsbBlockRule(bundle);
                await _store.EnqueueAlertAsync(
                    Guid.NewGuid().ToString(),
                    string.IsNullOrEmpty(rule?.Id) ? null : rule.Id,
                    version,
                    "usb_storage",
                    rule?.Severity ?? "critical",
                    "blocked_failed",
                    DateTimeOffset.UtcNow,
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["reason"] = result.Detail ?? "",
                        ["desiredBlock"] = wantUsbBlock ? "true" : "false",
                    }),
                    ct);
            }
            return;
        }

        _lastUsbBlock = wantUsbBlock;
        _accessDeniedAlertQueued = false;
        _logger.LogInformation(
            "USBSTOR {State} (changed={Changed}, detail={Detail})",
            wantUsbBlock ? "BLOCK" : "ALLOW", result.Changed, result.Detail);

        if (result.Changed && wantUsbBlock)
        {
            var rule = PolicyBundleParser.FirstUsbBlockRule(bundle);
            await _store.EnqueueAlertAsync(
                Guid.NewGuid().ToString(),
                string.IsNullOrEmpty(rule?.Id) ? null : rule.Id,
                version,
                "usb_storage",
                rule?.Severity ?? "critical",
                "blocked",
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["action"] = "usbstor_disabled",
                    ["previousStart"] = result.PreviousStart?.ToString() ?? "",
                    ["desiredStart"] = result.DesiredStart.ToString(),
                }),
                ct);
        }
    }

    private async Task ApplyWebAsync(
        PolicyBundle bundle,
        long version,
        IReadOnlyList<string> wantDomains,
        string webFp,
        CancellationToken ct)
    {
        _cfg.RefreshCredentials();
        var hasCreds = !string.IsNullOrWhiteSpace(_cfg.ServerUrl) &&
                       !string.IsNullOrWhiteSpace(_cfg.DeviceToken);
        var pausedUntil = await _store.GetLockdownPausedUntilAsync(ct);
        var paused = pausedUntil is { } p && p > DateTimeOffset.UtcNow;
        var lockdownOn = hasCreds && !paused && bundle.LockdownEnabled;
        var lockdownAllow = lockdownOn
            ? BuildLockdownBrowserAllowHosts(bundle, wantDomains, _cfg.ServerUrl)
            : null;
        var allowFp = lockdownAllow is null
            ? "allow=-"
            : "allow=" + string.Join(",", lockdownAllow);
        var combinedFp = webFp + "|" + allowFp;

        // Skip hosts write only on true cold start (no published policy yet) AND no
        // leftover OS artifacts. If URLBlocklist/hosts/FW still present, fall through
        // to Apply([]) so a wiped dlp.db / version-0 boot still self-heals.
        if (wantDomains.Count == 0 && !lockdownOn && _lastWebFingerprint == "\0" && version <= 0
            && _cfg.WebBlock && !DomainBlockEnforcer.HasLeftoverOsArtifacts())
        {
            _lastWebFingerprint = "";
            _webBlock.Set(version, Array.Empty<string>(), DomainBlockEnforcer.CurrentGeneration);
            return;
        }

        var drift = !DomainBlockEnforcer.OsStateMatchesDesired(wantDomains, lockdownAllow);
        if (combinedFp == _lastWebFingerprint && !drift)
        {
            // Domain set + OS match — still refresh CDN IPs when cache TTL expires.
            if (wantDomains.Count > 0)
            {
                var refresh = DomainBlockEnforcer.RefreshStaleFirewallIps(wantDomains);
                if (refresh.Changed)
                {
                    _logger.LogInformation(
                        "Web firewall IP refresh ({Detail})", refresh.Detail);
                    await _store.AuditAsync("web_fw_ip_refresh", refresh.Detail, ct);
                }
            }
            _webBlock.Set(version, wantDomains, DomainBlockEnforcer.CurrentGeneration);
            return;
        }

        if (combinedFp == _lastWebFingerprint && drift)
        {
            _logger.LogWarning(
                "Web block OS drift vs policy (count={Count}) — reconciling hosts/FW/PIN/URLBlocklist",
                wantDomains.Count);
            await _store.AuditAsync(
                "web_domain_reconcile",
                $"count={wantDomains.Count} fp_unchanged=1",
                ct);
        }

        var result = DomainBlockEnforcer.Apply(wantDomains, lockdownAllow);
        var auditAction = wantDomains.Count == 0 ? "web_domain_clear" : "web_domain_apply";
        await _store.AuditAsync(
            auditAction,
            $"count={wantDomains.Count} changed={result.Changed} detail={result.Detail}",
            ct);

        if (result.Detail == "access_denied_need_admin"
            || result.Detail.StartsWith("browser_error", StringComparison.Ordinal)
            || result.Detail.StartsWith("pin_clear_failed", StringComparison.Ordinal)
            || result.Detail.StartsWith("fw_clear_incomplete", StringComparison.Ordinal)
            || result.Detail.StartsWith("fw_error", StringComparison.Ordinal))
        {
            if (DateTimeOffset.UtcNow - _lastAccessDeniedLog > TimeSpan.FromMinutes(2))
            {
                _lastAccessDeniedLog = DateTimeOffset.UtcNow;
                _logger.LogWarning(
                    "Web domain enforce needs Administrator / LocalSystem or clear incomplete ({Path}). detail={Detail}",
                    DomainBlockEnforcer.HostsPath, result.Detail);
            }

            if (!_webAccessDeniedAlertQueued && wantDomains.Count > 0
                && result.Detail == "access_denied_need_admin")
            {
                _webAccessDeniedAlertQueued = true;
                var rule = PolicyBundleParser.FirstWebBlockRule(bundle, DateTimeOffset.UtcNow);
                await _store.EnqueueAlertAsync(
                    Guid.NewGuid().ToString(),
                    string.IsNullOrEmpty(rule?.Id) ? null : rule.Id,
                    version,
                    "web_navigate",
                    rule?.Severity ?? "high",
                    "blocked_failed",
                    DateTimeOffset.UtcNow,
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["reason"] = result.Detail ?? "",
                        ["domains"] = string.Join(",", wantDomains),
                    }),
                    ct);
            }
            // Do NOT advance fingerprint — retry next tick (PIN leftovers / URLBlocklist).
            return;
        }

        // Refuse fingerprint advance until OS matches desired (empty or shrunk set).
        if (!DomainBlockEnforcer.OsStateMatchesDesired(wantDomains, lockdownAllow))
        {
            _logger.LogWarning(
                "Web domain apply incomplete ({Detail}) — OS still drifts; will retry",
                result.Detail);
            await _store.AuditAsync(
                wantDomains.Count == 0 ? "web_domain_clear_incomplete" : "web_domain_reconcile_incomplete",
                result.Detail ?? "",
                ct);
            return;
        }

        _lastWebFingerprint = combinedFp;
        _webAccessDeniedAlertQueued = false;
        _webBlock.Set(version, wantDomains, DomainBlockEnforcer.CurrentGeneration);
        _logger.LogInformation(
            "Web domains {State} (count={Count}, lockdownAllow={Allow}, changed={Changed}, gen={Gen}, detail={Detail})",
            wantDomains.Count > 0 ? "BLOCK" : (lockdownOn ? "LOCKDOWN" : "CLEAR"),
            wantDomains.Count,
            lockdownAllow?.Count ?? 0,
            result.Changed,
            DomainBlockEnforcer.CurrentGeneration,
            result.Detail);

        if (result.Changed && wantDomains.Count > 0)
        {
            var rule = PolicyBundleParser.FirstWebBlockRule(bundle, DateTimeOffset.UtcNow);
            await _store.EnqueueAlertAsync(
                Guid.NewGuid().ToString(),
                string.IsNullOrEmpty(rule?.Id) ? null : rule.Id,
                version,
                "web_navigate",
                rule?.Severity ?? "high",
                "blocked",
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["action"] = "hosts_and_firewall",
                    ["domains"] = string.Join(",", wantDomains),
                    ["detail"] = result.Detail ?? "",
                    ["count"] = wantDomains.Count.ToString(),
                }),
                ct);
        }
    }

    /// <summary>
    /// Chrome/Edge URLAllowlist hosts: platformAllow + allow.hosts + API bootstrap,
    /// minus explicit web-block domains (block wins over allow in the address bar).
    /// </summary>
    private static IReadOnlyList<string> BuildLockdownBrowserAllowHosts(
        PolicyBundle bundle,
        IReadOnlyList<string> blockDomains,
        string? serverUrl)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in blockDomains)
        {
            foreach (var n in PolicyBundleParser.NormalizeDomain(d))
                blocked.Add(n);
        }

        var allow = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var n in PolicyBundleParser.NormalizeDomain(raw))
            {
                if (!blocked.Contains(n))
                    allow.Add(n);
            }
        }

        foreach (var h in bundle.PlatformAllowHosts) Add(h);
        foreach (var h in bundle.AllowHosts) Add(h);

        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            var s = serverUrl.Trim();
            if (!s.Contains("://", StringComparison.Ordinal))
                s = "https://" + s;
            if (Uri.TryCreate(s, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
                Add(u.Host.Trim('[', ']'));
        }

        return allow.ToList();
    }
}
