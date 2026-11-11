using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlphaAITracker.Service;

/// <summary>
/// Chrome-like silent Windows updater owned by LocalSystem.
/// Every <c>ALPHA_UPDATE_CHECK_INTERVAL_SEC</c> (default 60s) it asks the API server
/// (<c>GET /api/v1/releases</c>, installers from the server's setup folder) whether a
/// newer version than the installed one exists, Range-downloads the installer under ProgramData
/// while the tracker keeps running, then spawns a detached SYSTEM PowerShell helper
/// that runs Inno <c>/VERYSILENT</c> without UAC. Independent of <c>ALPHA_DLP_ENABLED</c>.
/// </summary>
public sealed class SilentUpdateWorker : BackgroundService
{
    private static readonly TimeSpan[] FailureBackoffs =
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    };

    /// <summary>
    /// A version whose silent apply failed is not retried for this long — otherwise the
    /// helper would stop the tracker and re-run a broken installer every minute.
    /// </summary>
    private static readonly TimeSpan FailedApplyRetryAfter = TimeSpan.FromHours(1);

    private TimeSpan CheckInterval => TimeSpan.FromSeconds(Math.Max(15, _cfg.UpdateCheckIntervalSec));

    private readonly DlpConfig _cfg;
    private readonly SilentUpdateState _state;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SilentUpdateWorker> _log;
    private DateTimeOffset _nextAllowedAttempt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    /// <summary>Version → UTC instant when download jitter elapses (stable per machine).</summary>
    private readonly Dictionary<string, DateTimeOffset> _downloadJitterDue =
        new(StringComparer.OrdinalIgnoreCase);

    public SilentUpdateWorker(
        DlpConfig cfg,
        SilentUpdateState state,
        IHttpClientFactory httpFactory,
        ILogger<SilentUpdateWorker> log)
    {
        _cfg = cfg;
        _state = state;
        _httpFactory = httpFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _log.LogInformation("SilentUpdateWorker parked (Windows-only)");
            return;
        }

        if (!_cfg.UpdateEnabled)
        {
            _log.LogInformation("SilentUpdateWorker parked (ALPHA_UPDATE_ENABLED=false)");
            return;
        }

        _log.LogInformation(
            "SilentUpdateWorker active (checkEverySec={Sec}, autoInstall={Auto})",
            _cfg.UpdateCheckIntervalSec, _cfg.UpdateAutoInstall);

        // Celebration UI removed — clear leftover celebrate.json from older builds.
        try { UpdatePaths.ClearCelebrate(); } catch { /* ignore */ }

        // Short settle so device.auth / service.env can appear after login.
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var force = _state.ConsumeCheckRequest();
                if (force || IsCheckDue() || DateTimeOffset.UtcNow >= _nextAllowedAttempt)
                    await RunOnceAsync(force, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SilentUpdateWorker tick failed");
                _state.MarkFailure(ex.Message);
                ScheduleBackoff();
            }

            // Wait up to CheckInterval, but wake immediately on RequestUpdateCheck.
            try
            {
                var woken = await _wake.WaitAsync(CheckInterval, stoppingToken);
                if (woken)
                {
                    // Drain extra signals so one burst = one follow-up pass.
                    while (_wake.Wait(0)) { }
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Pipe <c>RequestUpdateCheck</c> — wake an immediate pass.</summary>
    public void RequestImmediateCheck()
    {
        _state.RequestCheck();
        _nextAllowedAttempt = DateTimeOffset.MinValue;
        try { _wake.Release(); } catch (SemaphoreFullException) { /* already awake */ }
    }

    private bool IsCheckDue()
    {
        try
        {
            if (!File.Exists(UpdatePaths.LastCheckPath)) return true;
            var raw = File.ReadAllText(UpdatePaths.LastCheckPath).Trim();
            if (!DateTimeOffset.TryParse(raw, out var last)) return true;
            return DateTimeOffset.UtcNow - last >= CheckInterval;
        }
        catch
        {
            return true;
        }
    }

    private async Task RunOnceAsync(bool force, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            if (UpdatePaths.TryClearStaleInProgress(UpdatePaths.InProgressTtl, out var cleared))
                _log.LogWarning("Silent update: cleared stale update_in_progress ({Reason})", cleared);

            if (UpdatePaths.IsUpdateInProgress())
            {
                _state.Set("applying", "update_in_progress");
                return;
            }

            if (!force && DateTimeOffset.UtcNow < _nextAllowedAttempt)
                return;

            _cfg.RefreshCredentials();
            if (string.IsNullOrWhiteSpace(_cfg.ServerUrl))
            {
                _state.Set("waiting", "no_server_url");
                return;
            }

            var current = ResolveInstalledVersion();
            _state.SetVersions(current, null);
            _state.Set("checking", "fetching_releases");

            var (info, hint) = await FetchLatestAsync(ct);
            PersistLastCheck();
            _state.MarkChecked();

            if (info is null)
            {
                _state.Set("idle", hint ?? "no_update");
                _state.MarkSuccess();
                _nextAllowedAttempt = DateTimeOffset.UtcNow + CheckInterval;
                return;
            }

            _state.SetVersions(current, info.Version);
            if (UpdateVersion.Compare(info.Version, current) <= 0)
            {
                UpdatePaths.ClearUpdatePriority();
                _state.MarkSuccess();
                _nextAllowedAttempt = DateTimeOffset.UtcNow + CheckInterval;
                _log.LogDebug("Silent update: already on {Ver}", current);
                return;
            }

            if (!_cfg.UpdateAutoInstall && !force)
            {
                // Banner-only: do not park tracker features until the user clicks Update.
                _state.Set("available", $"update_available:{info.Version}");
                return;
            }

            // GUI force may retry after apply_failed; automatic ticks keep the 1h guard.
            if (!force && RecentlyFailedApply(info.Version))
            {
                UpdatePaths.ClearUpdatePriority();
                _state.Set("available", $"apply_failed_retry_later:{info.Version}");
                _nextAllowedAttempt = DateTimeOffset.UtcNow + CheckInterval;
                return;
            }

            // Upgrade wins: tracker yields sync/live/screenshots while we download/apply.
            UpdatePaths.SetUpdatePriority(info.Version, force ? "force" : "auto");
            _state.Set("priority", $"update_priority:{info.Version}");

            if (!await WaitDownloadJitterAsync(info.Version, force, ct))
            {
                _state.Set("priority", $"download_jitter:{info.Version}");
                return;
            }

            _log.LogInformation(
                "Silent update: downloading {Asset} ({From} → {To}) — features yielding",
                info.AssetName, current, info.Version);

            var installerPath = await DownloadChunkedAsync(info, ct);
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                UpdatePaths.ClearUpdatePriority();
                _state.MarkFailure("installer_missing_after_download");
                ScheduleBackoff();
                return;
            }

            _state.Set("applying", "spawning_helper");
            SpawnApplyHelper(installerPath, info.Version, current);
            // Helper owns the rest; do not clear in_progress here on success.
            _nextAllowedAttempt = DateTimeOffset.UtcNow + CheckInterval;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True when the apply helper recorded a failure for this version within the last hour.</summary>
    private static bool RecentlyFailedApply(string version)
    {
        try
        {
            var path = UpdatePaths.ApplyFailedPath;
            if (!File.Exists(path)) return false;
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || !string.Equals(lines[0].Trim(), version, StringComparison.OrdinalIgnoreCase))
                return false;
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < FailedApplyRetryAfter;
        }
        catch
        {
            return false;
        }
    }

    private void PersistLastCheck()
    {
        try
        {
            UpdatePaths.EnsureRoot();
            File.WriteAllText(UpdatePaths.LastCheckPath, DateTimeOffset.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not persist update last_check_at");
        }
    }

    private void ScheduleBackoff()
    {
        var idx = Math.Clamp(_state.ConsecutiveFailures - 1, 0, FailureBackoffs.Length - 1);
        _nextAllowedAttempt = DateTimeOffset.UtcNow + FailureBackoffs[idx];
        _log.LogInformation(
            "Silent update backoff until {When} (failures={N})",
            _nextAllowedAttempt, _state.ConsecutiveFailures);
    }

    private async Task<(PendingUpdate? Info, string? Hint)> FetchLatestAsync(CancellationToken ct)
    {
        var baseUrl = _cfg.ServerUrl.TrimEnd('/');
        var url = $"{baseUrl}/api/v1/releases";
        var http = _httpFactory.CreateClient("dlp");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.TryParseAdd($"{ServiceIdentity.AppMutex}/{ResolveInstalledVersion()}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var hint = resp.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                ? "bridge_unavailable"
                : $"http_{(int)resp.StatusCode}";
            _state.MarkFailure(hint);
            ScheduleBackoff();
            return (null, hint);
        }

        var payload = await resp.Content.ReadFromJsonAsync<BridgeReleasesPayload>(
            cancellationToken: ct);
        if (payload?.Releases is null || payload.Releases.Count == 0)
            return (null, "no_releases");

        BridgeRelease? release = null;
        if (!string.IsNullOrWhiteSpace(payload.LatestStable))
        {
            release = payload.Releases.FirstOrDefault(r =>
                string.Equals(r.TagName, payload.LatestStable, StringComparison.OrdinalIgnoreCase)
                && !r.Prerelease);
        }
        release ??= payload.Releases.FirstOrDefault(r => !r.Prerelease)
                    ?? payload.Releases[0];

        var version = UpdateVersion.Normalize(release.TagName);
        if (version is null) return (null, "bad_tag");

        var asset = ResolveWindowsAsset(release.Assets, version);
        if (asset is null) return (null, "no_windows_asset");

        return (new PendingUpdate(
            version,
            release.TagName,
            release.Body ?? "",
            asset.Name,
            asset.Id,
            asset.Size,
            $"{baseUrl}/api/v1/downloads/{asset.Id}"), null);
    }

    private static BridgeReleaseAsset? ResolveWindowsAsset(
        IReadOnlyList<BridgeReleaseAsset> assets, string version)
    {
        var forPlatform = assets
            .Where(a => string.Equals(a.Platform, "windows", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (forPlatform.Count == 0)
        {
            forPlatform = assets
                .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var rx = new Regex(
            @"(^|[\-_.vV])" + Regex.Escape(version) + @"(?=$|[\-_.])",
            RegexOptions.IgnoreCase);
        return forPlatform.FirstOrDefault(a => rx.IsMatch(a.Name))
               ?? forPlatform.FirstOrDefault();
    }

    /// <summary>
    /// Spread fleet downloads. Returns false when this tick should wait (not due yet).
    /// Force (GUI) uses ≤30s jitter. Auto priority caps at 120s so upgrade stays ahead of
    /// other features while the server global download gate still admits the fleet.
    /// </summary>
    private async Task<bool> WaitDownloadJitterAsync(string version, bool force, CancellationToken ct)
    {
        const int priorityAutoCapSec = 120;
        var configured = Math.Max(0, _cfg.UpdateDownloadJitterSec);
        var windowSec = force
            ? Math.Min(30, configured)
            : Math.Min(priorityAutoCapSec, configured);
        if (windowSec <= 0)
            return true;

        if (!_downloadJitterDue.TryGetValue(version, out var due))
        {
            var offsetSec = StableJitterOffsetSec(version, windowSec);
            due = DateTimeOffset.UtcNow.AddSeconds(offsetSec);
            _downloadJitterDue[version] = due;
            _log.LogInformation(
                "Silent update: download jitter {Sec}s for {Ver} (due {Due:o})",
                offsetSec, version, due);
        }

        var wait = due - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
            return true;

        // Do not block the worker gate for the whole hour — defer to next tick.
        if (wait > TimeSpan.FromSeconds(5))
            return false;

        try { await Task.Delay(wait, ct); }
        catch (OperationCanceledException) { return false; }
        return true;
    }

    private static int StableJitterOffsetSec(string version, int windowSec)
    {
        if (windowSec <= 0) return 0;
        var seed = $"{Environment.MachineName}|{ServiceIdentity.AppMutex}|{version}";
        var hash = seed.GetHashCode(StringComparison.OrdinalIgnoreCase);
        return (int)((uint)hash % (uint)(windowSec + 1));
    }

    private async Task<string> DownloadChunkedAsync(PendingUpdate info, CancellationToken ct)
    {
        UpdatePaths.EnsureRoot();
        var dest = Path.Combine(UpdatePaths.RootDir, info.AssetName);
        var part = dest + ".part";

        // Fresh dest if a complete file already matches expected size.
        if (File.Exists(dest) && info.Size > 0 && new FileInfo(dest).Length == info.Size)
        {
            _state.Set("downloaded", "already_staged", 100);
            return dest;
        }

        const int maxAttempts = 8;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            long existing = 0;
            if (File.Exists(part))
            {
                existing = new FileInfo(part).Length;
                if (info.Size > 0 && existing > info.Size)
                {
                    try { File.Delete(part); } catch { /* ignore */ }
                    existing = 0;
                }
            }

            if (File.Exists(dest) && attempt == 1)
            {
                try { File.Delete(dest); } catch { /* ignore */ }
            }

            _state.Set("downloading", info.AssetName, info.Size > 0 ? existing * 100.0 / info.Size : 0);

            var http = _httpFactory.CreateClient("update-download");
            using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            if (existing > 0)
                req.Headers.Range = new RangeHeaderValue(existing, null);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)resp.StatusCode is 429 or 503)
            {
                var retryAfter = ParseRetryAfterSeconds(resp) ?? Math.Min(120, 15 * attempt);
                _log.LogWarning(
                    "Silent update: download {Status} — waiting {Sec}s (attempt {N}/{Max})",
                    (int)resp.StatusCode, retryAfter, attempt, maxAttempts);
                _state.Set("waiting", $"download_retry_after:{retryAfter}");
                await Task.Delay(TimeSpan.FromSeconds(retryAfter), ct);
                continue;
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (File.Exists(part))
                {
                    File.Move(part, dest, overwrite: true);
                    return dest;
                }
            }

            if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                resp.EnsureSuccessStatusCode();

            var append = resp.StatusCode == System.Net.HttpStatusCode.PartialContent && existing > 0;
            if (!append && existing > 0)
            {
                try { File.Delete(part); } catch { /* ignore */ }
                existing = 0;
            }

            var total = info.Size > 0
                ? info.Size
                : (resp.Content.Headers.ContentLength ?? 0) + (append ? existing : 0);

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(
                             part,
                             append ? FileMode.Append : FileMode.Create,
                             FileAccess.Write,
                             FileShare.None))
            {
                var buffer = new byte[256 * 1024];
                long read = existing;
                while (true)
                {
                    var n = await src.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0)
                        _state.Set("downloading", info.AssetName, Math.Min(100, read * 100.0 / total));
                }
                await dst.FlushAsync(ct);
            }

            if (info.Size > 0 && new FileInfo(part).Length != info.Size)
            {
                throw new InvalidOperationException(
                    $"download_size_mismatch expected={info.Size} got={new FileInfo(part).Length}");
            }

            File.Move(part, dest, overwrite: true);
            _state.Set("downloaded", info.AssetName, 100);
            return dest;
        }

        throw new InvalidOperationException("download_retry_exhausted");
    }

    private static int? ParseRetryAfterSeconds(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is { } delta)
            return Math.Clamp((int)Math.Ceiling(delta.TotalSeconds), 1, 600);
        if (resp.Headers.RetryAfter?.Date is { } date)
        {
            var sec = (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds);
            return Math.Clamp(sec, 1, 600);
        }
        if (resp.Headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var v in values)
            {
                if (int.TryParse(v, out var sec) && sec > 0)
                    return Math.Clamp(sec, 1, 600);
            }
        }
        return null;
    }

    private void SpawnApplyHelper(
        string installerPath, string newVersion, string oldVersion)
    {
        if (!File.Exists(installerPath))
        {
            _log.LogWarning("Silent update: refuse spawn — installer missing at {Path}", installerPath);
            _state.MarkFailure("installer_missing");
            ScheduleBackoff();
            return;
        }

        UpdatePaths.EnsureRoot();
        UpdatePaths.WriteInProgress(newVersion);

        var exe = ResolveClientExePath() ?? "";
        var exeName = string.IsNullOrEmpty(exe)
            ? ServiceIdentity.ExecutableName + ".exe"
            : Path.GetFileName(exe);
        var procName = Path.GetFileNameWithoutExtension(exeName);
        var logPath = UpdatePaths.LogPath;
        var inProgress = UpdatePaths.InProgressFlag;
        var updatesDir = UpdatePaths.RootDir;
        var serviceName = ServiceIdentity.ServiceName;

        static string PsQuote(string value) => value.Replace("'", "''");

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$log = '{PsQuote(logPath)}'");
        sb.AppendLine("function Write-UpdateLog([string]$Message) {");
        sb.AppendLine("  try { Add-Content -LiteralPath $log -Value (\"[{0}] {1}\" -f (Get-Date -Format o), $Message) } catch {}");
        sb.AppendLine("}");
        // Previous applies appended forever. This apply owns the file.
        sb.AppendLine("Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Write-UpdateLog 'SYSTEM silent apply started (no UAC)'");
        sb.AppendLine($"$procName = '{PsQuote(procName)}'");
        sb.AppendLine($"$installer = '{PsQuote(installerPath)}'");
        sb.AppendLine($"$exe = '{PsQuote(exe)}'");
        sb.AppendLine($"$inProgress = '{PsQuote(inProgress)}'");
        sb.AppendLine($"$celebrate = '{PsQuote(UpdatePaths.CelebrateFlag)}'");
        sb.AppendLine($"$updatesDir = '{PsQuote(updatesDir)}'");
        sb.AppendLine($"$newVersion = '{PsQuote(newVersion)}'");
        sb.AppendLine($"$oldVersion = '{PsQuote(oldVersion)}'");
        sb.AppendLine($"$failedFlag = '{PsQuote(UpdatePaths.ApplyFailedPath)}'");
        sb.AppendLine($"$priorityFlag = '{PsQuote(UpdatePaths.PriorityFlag)}'");
        sb.AppendLine($"$serviceName = '{PsQuote(serviceName)}'");
        sb.AppendLine("if (-not (Test-Path -LiteralPath $installer)) {");
        sb.AppendLine("  Write-UpdateLog 'Installer file missing before launch'");
        sb.AppendLine("  Set-Content -LiteralPath $failedFlag -Value $newVersion -ErrorAction SilentlyContinue");
        sb.AppendLine("  Remove-Item -LiteralPath $inProgress -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  Remove-Item -LiteralPath $priorityFlag -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        // Stop tracker so Program Files files unlock (Inno also CloseApplications=force).
        sb.AppendLine("Write-UpdateLog 'Stopping tracker for file replace'");
        sb.AppendLine("Get-Process -Name $procName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("for ($i = 0; $i -lt 60; $i++) {");
        sb.AppendLine("  if (-not (Get-Process -Name $procName -ErrorAction SilentlyContinue)) { break }");
        sb.AppendLine("  Start-Sleep -Seconds 1");
        sb.AppendLine("}");
        sb.AppendLine("Write-UpdateLog 'Launching silent Inno (SYSTEM — no RunAs)'");
        sb.AppendLine("$exitCode = 1");
        sb.AppendLine("try {");
        sb.AppendLine("  $setup = Start-Process -FilePath $installer `");
        sb.AppendLine("    -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' `");
        sb.AppendLine("    -Wait -PassThru");
        sb.AppendLine("  if ($null -ne $setup) { $exitCode = $setup.ExitCode }");
        sb.AppendLine("  Write-UpdateLog (\"Installer finished (exit {0})\" -f $exitCode)");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Write-UpdateLog (\"Installer failed: {0}\" -f $_.Exception.Message)");
        sb.AppendLine("  Set-Content -LiteralPath $failedFlag -Value $newVersion -ErrorAction SilentlyContinue");
        sb.AppendLine("  Remove-Item -LiteralPath $inProgress -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  Remove-Item -LiteralPath $priorityFlag -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        sb.AppendLine("if ($exitCode -ne 0) {");
        sb.AppendLine("  Write-UpdateLog 'Installer non-zero exit — clearing in_progress'");
        sb.AppendLine("  Set-Content -LiteralPath $failedFlag -Value $newVersion -ErrorAction SilentlyContinue");
        sb.AppendLine("  Remove-Item -LiteralPath $inProgress -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  Remove-Item -LiteralPath $priorityFlag -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  exit $exitCode");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Seconds 3");
        // Reload DLP service so AppLocker v6 / new enforcer bits are not left in the old process.
        sb.AppendLine("Write-UpdateLog (\"Restarting service {0} to load new binary\" -f $serviceName)");
        sb.AppendLine("try {");
        sb.AppendLine("  & sc.exe stop $serviceName | Out-Null");
        sb.AppendLine("  Start-Sleep -Seconds 2");
        sb.AppendLine("  & sc.exe start $serviceName | Out-Null");
        sb.AppendLine("  Write-UpdateLog 'Service restart issued'");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Write-UpdateLog (\"Service restart failed: {0}\" -f $_.Exception.Message)");
        sb.AppendLine("}");
        // No celebration toast — delete any leftover celebrate.json from older helpers.
        sb.AppendLine("Remove-Item -LiteralPath $celebrate -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item -LiteralPath $inProgress -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item -LiteralPath $priorityFlag -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item -LiteralPath $failedFlag -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Get-ChildItem -LiteralPath $updatesDir -Filter '*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Get-ChildItem -LiteralPath $updatesDir -Filter '*.part' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item -LiteralPath (Join-Path $updatesDir 'celebrate_payload.json') -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Write-UpdateLog 'SYSTEM silent apply done'");
        sb.AppendLine("Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue");
        var scriptPath = UpdatePaths.ApplyScriptPath;
        File.WriteAllText(scriptPath, sb.ToString());

        // Detach so Inno can stop/delete this service without killing the helper.
        try
        {
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments =
                    $"/c start \"\" /MIN powershell.exe -NoProfile -ExecutionPolicy Bypass " +
                    $"-WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (started is null)
            {
                UpdatePaths.ClearInProgress();
                UpdatePaths.ClearUpdatePriority();
                _state.MarkFailure("helper_spawn_null");
                ScheduleBackoff();
                _log.LogError("Silent update: Process.Start returned null — cleared update_in_progress");
                return;
            }
        }
        catch (Exception ex)
        {
            UpdatePaths.ClearInProgress();
            UpdatePaths.ClearUpdatePriority();
            _state.MarkFailure("helper_spawn_failed");
            ScheduleBackoff();
            _log.LogError(ex, "Silent update: helper spawn failed — cleared update_in_progress");
            return;
        }

        _state.Set("applying", $"helper_spawned:{newVersion}");
        _log.LogInformation(
            "Silent update helper spawned ({Old} → {New}); service may restart during Inno",
            oldVersion, newVersion);
    }

    private static string ResolveInstalledVersion()
    {
        try
        {
            var path = ResolveClientExePath();
            if (path != null && File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                // Prefer FileVersion — ProductVersion may carry +git metadata that still
                // normalizes, but FileVersion matches the shipped installer VERSION.
                foreach (var raw in new[] { vi.FileVersion, vi.ProductVersion })
                {
                    var n = UpdateVersion.Normalize(raw ?? "");
                    if (n != null) return n;
                }
            }
        }
        catch { /* fall through */ }

        return typeof(SilentUpdateWorker).Assembly.GetName().Version is { } av
            ? $"{av.Major}.{av.Minor}.{av.Build}"
            : "0.0.0";
    }

    private static string? ResolveClientExePath()
    {
        var name = ServiceIdentity.ExecutableName + ".exe";
        var folder = ServiceIdentity.DisplayName;
        foreach (var c in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), folder, name),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), folder, name),
                 })
        {
            if (File.Exists(c)) return c;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside)) return beside;

        var parent = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent))
        {
            var sibling = Path.Combine(parent, name);
            if (File.Exists(sibling)) return sibling;
        }

        return null;
    }

    private sealed record PendingUpdate(
        string Version,
        string TagName,
        string ReleaseNotes,
        string AssetName,
        long AssetId,
        long Size,
        string DownloadUrl);
}
