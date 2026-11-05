using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlphaAITracker.Service;

/// <summary>
/// Chrome-like silent Windows updater owned by LocalSystem.
/// Checks the public releases bridge, Range-downloads the installer under ProgramData
/// while the tracker keeps running, then spawns a detached SYSTEM PowerShell helper
/// that runs Inno <c>/VERYSILENT</c> without UAC. Independent of <c>ALPHA_DLP_ENABLED</c>.
/// </summary>
public sealed class SilentUpdateWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan[] FailureBackoffs =
    {
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    };

    private readonly DlpConfig _cfg;
    private readonly SilentUpdateState _state;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SilentUpdateWorker> _log;
    private DateTimeOffset _nextAllowedAttempt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

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
            "SilentUpdateWorker active (checkHours={Hours}, autoInstall={Auto})",
            _cfg.UpdateAutoCheckHours, _cfg.UpdateAutoInstall);

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

            // Wait up to PollInterval, but wake immediately on RequestUpdateCheck.
            try
            {
                var woken = await _wake.WaitAsync(PollInterval, stoppingToken);
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
            return DateTimeOffset.UtcNow - last >= TimeSpan.FromHours(_cfg.UpdateAutoCheckHours);
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
                _nextAllowedAttempt = DateTimeOffset.UtcNow +
                    TimeSpan.FromHours(_cfg.UpdateAutoCheckHours);
                return;
            }

            _state.SetVersions(current, info.Version);
            if (UpdateVersion.Compare(info.Version, current) <= 0)
            {
                _state.MarkSuccess();
                _nextAllowedAttempt = DateTimeOffset.UtcNow +
                    TimeSpan.FromHours(_cfg.UpdateAutoCheckHours);
                _log.LogDebug("Silent update: already on {Ver}", current);
                return;
            }

            if (!_cfg.UpdateAutoInstall && !force)
            {
                _state.Set("available", $"update_available:{info.Version}");
                return;
            }

            _log.LogInformation(
                "Silent update: downloading {Asset} ({From} → {To})",
                info.AssetName, current, info.Version);

            var installerPath = await DownloadChunkedAsync(info, ct);
            _state.Set("applying", "spawning_helper");
            SpawnApplyHelper(installerPath, info.Version, current);
            // Helper owns the rest; do not clear in_progress here.
            _nextAllowedAttempt = DateTimeOffset.UtcNow + TimeSpan.FromHours(_cfg.UpdateAutoCheckHours);
        }
        finally
        {
            _gate.Release();
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
            @"(^|[\-_.])" + Regex.Escape(version) + @"(?=$|[\-_.])",
            RegexOptions.IgnoreCase);
        return forPlatform.FirstOrDefault(a => rx.IsMatch(a.Name))
               ?? forPlatform.FirstOrDefault();
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

        if (File.Exists(dest))
        {
            try { File.Delete(dest); } catch { /* ignore */ }
        }

        _state.Set("downloading", info.AssetName, info.Size > 0 ? existing * 100.0 / info.Size : 0);

        var http = _httpFactory.CreateClient("update-download");
        using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
        if (existing > 0)
            req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Server thinks we already have the whole file — treat part as complete.
            if (File.Exists(part))
            {
                File.Move(part, dest, overwrite: true);
                return dest;
            }
        }

        if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
            resp.EnsureSuccessStatusCode();

        // If server ignored Range and returned 200, restart from zero.
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

    private void SpawnApplyHelper(
        string installerPath, string newVersion, string oldVersion)
    {
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
        sb.AppendLine("  Remove-Item -LiteralPath $inProgress -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        sb.AppendLine("if ($exitCode -ne 0) {");
        sb.AppendLine("  Write-UpdateLog 'Installer non-zero exit — clearing in_progress'");
        sb.AppendLine("  Remove-Item -LiteralPath $inProgress -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  exit $exitCode");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Seconds 3");
        // No celebration toast — delete any leftover celebrate.json from older helpers.
        sb.AppendLine("Remove-Item -LiteralPath $celebrate -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item -LiteralPath $inProgress -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Get-ChildItem -LiteralPath $updatesDir -Filter '*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Get-ChildItem -LiteralPath $updatesDir -Filter '*.part' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item -LiteralPath (Join-Path $updatesDir 'celebrate_payload.json') -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Write-UpdateLog 'SYSTEM silent apply done'");
        sb.AppendLine("Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue");
        var scriptPath = UpdatePaths.ApplyScriptPath;
        File.WriteAllText(scriptPath, sb.ToString());

        // Detach so Inno can stop/delete this service without killing the helper.
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments =
                $"/c start \"\" /MIN powershell.exe -NoProfile -ExecutionPolicy Bypass " +
                $"-WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

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
                var raw = vi.ProductVersion ?? vi.FileVersion ?? "";
                var n = UpdateVersion.Normalize(raw);
                if (n != null) return n;
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
