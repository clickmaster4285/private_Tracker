using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Self-updater: every <c>ALPHA_UPDATE_CHECK_INTERVAL_SEC</c> (default 60s) checks the
/// API server (<c>GET {ALPHA_SERVER_URL}/api/v1/releases</c>, installers from the
/// server's setup folder) for a newer installer than the
/// running <see cref="AppInfo.Version"/>.
///
/// <b>Windows:</b> a service that returns <c>ok</c> downloads and applies silently.
/// A service that returns <c>unknown_op</c> (installed build before this pipe op)
/// downloads here and asks Windows for approval (<c>RunAs</c>). <c>update_disabled</c>,
/// an update already in progress, and a pipe that does not answer do not start a
/// second installer. Linux still downloads here + <c>pkexec dpkg</c>; macOS opens the dmg.
///
/// Runs a quiet auto-check loop (persisted cadence in app_status) and exposes
/// observable state so the GUI can bind a "Check updates" button, an update banner
/// and a download/install/restart flow.
///
/// Registered as a singleton + hosted service (same pattern as SyncService), and
/// injected into MainViewModel + DashboardViewModel so the top bar and dashboard
/// banner always read the SAME state.
///
/// NOTE: all state and commands are declared EXPLICITLY (no [ObservableProperty] /
/// [RelayCommand] source generators) — the generated members made IDEs show
/// phantom "name does not exist" errors until the analyzer re-ran, even though the
/// CLI build was always clean.
/// </summary>
public class AppUpdateService : ObservableObject, IHostedService
{
    private const string LastCheckStatusKey = "update_last_check_at";
    private const string DismissedVersionKey = "update_dismissed_version";

    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly HttpClient _downloadHttp;
    private readonly ILogStore _store;
    private readonly ILogger<AppUpdateService> _logger;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private UpdateInfo? _pending;

    public AppUpdateService(AppConfig config, HttpClient http, ILogStore store, ILogger<AppUpdateService> logger)
    {
        _config = config;
        _http = http;
        _store = store;
        _logger = logger;

        // The shared HttpClient has a 30s global timeout — fine for the tiny releases
        // metadata call but lethal for a multi-hundred-MB installer download. Dedicated
        // client with a 15-minute ceiling for the actual download.
        _downloadHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

        CheckForUpdatesCommand = new AsyncRelayCommand(
            () => CheckForUpdatesCoreAsync(auto: false, CancellationToken.None));
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync);
        DismissUpdateCommand = new AsyncRelayCommand(DismissUpdateAsync);
        RestartApplicationCommand = new RelayCommand(RestartApplication);
    }

    // ─── Commands (bound to the top bar + dashboard banner) ───

    public IRelayCommand CheckForUpdatesCommand { get; }
    public IRelayCommand InstallUpdateCommand { get; }
    public IRelayCommand DismissUpdateCommand { get; }
    public IRelayCommand RestartApplicationCommand { get; }

    // ─── Observable state (bound by the top bar + dashboard banner) ───

    private bool _isChecking;
    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetProperty(ref _isChecking, value))
                OnPropertyChanged(nameof(ButtonText));
        }
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                OnPropertyChanged(nameof(ButtonText));
                OnPropertyChanged(nameof(DownloadProgressText));
            }
        }
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (SetProperty(ref _downloadProgress, value))
                OnPropertyChanged(nameof(DownloadProgressText));
        }
    }

    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (SetProperty(ref _updateAvailable, value))
            {
                OnPropertyChanged(nameof(UpdateBadgeText));
                OnPropertyChanged(nameof(InstallButtonText));
                OnPropertyChanged(nameof(StatusVisible));
            }
        }
    }

    /// <summary>True after a Linux dpkg install — the new binary is on disk; only a restart applies it.</summary>
    private bool _restartReady;
    public bool RestartReady
    {
        get => _restartReady;
        private set
        {
            if (SetProperty(ref _restartReady, value))
                OnPropertyChanged(nameof(StatusVisible));
        }
    }

    private string _latestVersion = string.Empty;
    public string LatestVersion
    {
        get => _latestVersion;
        private set
        {
            if (SetProperty(ref _latestVersion, value))
            {
                OnPropertyChanged(nameof(UpdateBadgeText));
                OnPropertyChanged(nameof(InstallButtonText));
            }
        }
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
                OnPropertyChanged(nameof(StatusVisible));
        }
    }

    private string _releaseNotes = string.Empty;
    public string ReleaseNotes
    {
        get => _releaseNotes;
        private set => SetProperty(ref _releaseNotes, value);
    }

    private string _assetName = string.Empty;
    public string AssetName
    {
        get => _assetName;
        private set => SetProperty(ref _assetName, value);
    }

    public string VersionLabel => AppInfo.Version;
    public string ButtonText => IsChecking ? "Checking…" : IsDownloading ? "Downloading…" : "Check updates";
    public string DownloadProgressText => IsDownloading ? $"{(int)DownloadProgress}%" : string.Empty;
    public string UpdateBadgeText => string.IsNullOrEmpty(LatestVersion) ? "New version" : $"v{LatestVersion} available";
    public string InstallButtonText => $"Update to v{LatestVersion}";
    /// <summary>
    /// Show status whenever we have text — including while the update banner is up
    /// (Windows silent-service feedback used to be invisible because this required
    /// <c>!UpdateAvailable</c>).
    /// </summary>
    public bool StatusVisible => !string.IsNullOrEmpty(StatusText) && !RestartReady;

    /// <summary>
    /// True when the updater can reach the public releases bridge — needs
    /// <see cref="AppConfig.ServerUrl"/> (same API the tracker already syncs to).
    /// </summary>
    private bool BridgeConfigured => !string.IsNullOrWhiteSpace(_config.ServerUrl);

    /// <summary>How often the background loop asks the server for a newer version (default 60s).</summary>
    private TimeSpan CheckInterval => TimeSpan.FromSeconds(Math.Max(15, _config.UpdateCheckIntervalSec));

    /// <summary>
    /// Linux auto-install shows a polkit password dialog. With a one-minute cadence a
    /// cancelled or failed install would re-prompt every minute, so one version is
    /// auto-installed at most once per hour (the Update button is never throttled).
    /// </summary>
    private static readonly TimeSpan AutoInstallRetryAfter = TimeSpan.FromHours(1);
    private string? _autoInstallVersion;
    private DateTime _autoInstallAt = DateTime.MinValue;

    // ─── IHostedService — the quiet auto-check loop ───

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Startup sweep: anything still in updates/ is a leftover installer from a
        // prior run (the folder is a download staging area, nothing belongs there
        // once the app is running). Wipe it so stale .deb/.exe files never linger
        // on Windows/Linux disks — user rule 2026-08-18.
        _ = Task.Run(() => CleanupUpdatesDirectoryAsync(CancellationToken.None), CancellationToken.None);

        if (!_config.UpdateEnabled || !BridgeConfigured) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunBackgroundLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { }
        return Task.CompletedTask;
    }

    private async Task RunBackgroundLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RunAutoCheckIfDueAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update auto-check loop stopped");
        }
    }

    /// <summary>
    /// Checks now if the configured interval has elapsed since the last check
    /// (persisted in app_status). Called on shell entry and by the periodic loop —
    /// never pops a dialog and never stacks on an in-flight check.
    /// </summary>
    public async Task RunAutoCheckIfDueAsync(CancellationToken ct)
    {
        if (!_config.UpdateEnabled || !BridgeConfigured || IsChecking || IsDownloading) return;
        try
        {
            var last = await _store.GetStatusAsync(LastCheckStatusKey, ct);
            var lastAt = DateTime.TryParse(last, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.MinValue;

            // Small slack so a check stamped a moment after the previous tick is not skipped.
            if (DateTime.UtcNow - lastAt < CheckInterval - TimeSpan.FromSeconds(5))
                return;

            await CheckForUpdatesCoreAsync(auto: true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto update check skipped");
        }
    }

    // ─── Command bodies ───

    private async Task InstallUpdateAsync()
    {
        if (_pending is null)
        {
            StatusText = "No pending update — click Check updates first.";
            return;
        }

        // One installer at a time. A second click while this runs does not start another.
        if (!await _installGate.WaitAsync(0))
        {
            StatusText = "An update is already in progress.";
            return;
        }
        try
        {
            if (OperatingSystem.IsWindows())
                await InstallWindowsUpdateAsync(CancellationToken.None);
            else
                await DownloadAndInstallAsync(_pending, CancellationToken.None);
        }
        finally
        {
            _installGate.Release();
        }
    }

    /// <summary>
    /// A newer build is already known. Ask the service to install it now, then close
    /// this process so the installer can replace the files.
    /// </summary>
    private async Task InstallWindowsUpdateAsync(CancellationToken ct)
    {
        if (UpdateInfo.CompareVersions(_pending!.Version, AppInfo.Version) <= 0)
        {
            StatusText = $"Already on {AppInfo.Version}.";
            UpdateAvailable = false;
            return;
        }

        StatusText = "Asking AlphaAITracker.Service to install the update…";
        var request = await DlpPipeClient.RequestUpdateCheckDetailedAsync(ct);
        // Status poll distinguishes "already applying" / old service from a dead pipe.
        var status = await DlpPipeClient.GetUpdateStatusDetailedAsync(ct);
        var route = UpdateInstallDecision.Decide(
            new UpdatePipeView(request.Connected, request.TimedOut, request.Body),
            new UpdatePipeView(status.Connected, status.TimedOut, status.Body));

        if (route == UpdateInstallRoute.PollService ||
            route == UpdateInstallRoute.AlreadyInProgress)
        {
            StatusText = route == UpdateInstallRoute.AlreadyInProgress
                ? "An update is already in progress via AlphaAITracker.Service. Closing…"
                : $"Update to v{_pending.Version} started. Closing…";
            ShutdownAfterApprovedInstall();
            return;
        }

        var pipeError = request.Error
            ?? PipeError(request.Body)
            ?? status.Error
            ?? PipeError(status.Body);
        StatusText = UpdateInstallDecision.RefuseMessage(route, pipeError);
    }

    private static string? PipeError(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error)) return null;
            return error.ValueKind == System.Text.Json.JsonValueKind.String ? error.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ask LocalSystem to check/download/apply. Never elevates this process.
    /// </summary>
    private async Task<bool> TryQueueSilentServiceUpdateAsync(CancellationToken ct)
    {
        try
        {
            return await DlpPipeClient.RequestUpdateCheckAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RequestUpdateCheck pipe failed");
            return false;
        }
    }

    /// <summary>Dismisses the banner for THIS version (manual checks can still re-trigger it).</summary>
    private async Task DismissUpdateAsync()
    {
        if (string.IsNullOrEmpty(LatestVersion)) return;
        await _store.SetStatusAsync(DismissedVersionKey, LatestVersion, CancellationToken.None);
        _pending = null;
        UpdateAvailable = false;
        StatusText = $"v{LatestVersion} dismissed — check again any time.";
    }

    /// <summary>Relaunches the app so an installed update takes effect.</summary>
    private void RestartApplication()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            StatusText = "Cannot locate the app binary to restart it.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--restart",
                UseShellExecute = true
            });

            StatusText = "Restarting…";
            // The new instance carries --restart and retries the single-instance mutex,
            // so exiting NOW lets it take over. Shutdown (not Environment.Exit) so the
            // host stops cleanly and the mutex is released by the finally block.
            App.AllowShutdown = true;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (Avalonia.Application.Current?.ApplicationLifetime
                        is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown();
                    }
                    else
                    {
                        Environment.Exit(0);
                    }
                }
                catch { Environment.Exit(0); }
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Restart failed: {ex.Message}";
        }
    }

    // ─── Core flow ───

    private async Task CheckForUpdatesCoreAsync(bool auto, CancellationToken ct)
    {
        // Atomic gate: the check can be triggered from the UI button, EnterShellAsync
        // and the background loop (every ALPHA_UPDATE_CHECK_INTERVAL_SEC). Without this, two callers could pass the
        // IsChecking guard and BOTH auto-install (two pkexec dialogs on Linux).
        if (!await _checkGate.WaitAsync(0, ct)) return;
        try
        {
            await CheckForUpdatesInnerAsync(auto, ct);
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task CheckForUpdatesInnerAsync(bool auto, CancellationToken ct)
    {
        if (IsChecking) return;
        IsChecking = true;
        StatusText = auto ? string.Empty : "Checking for updates…";
        try
        {
            if (!BridgeConfigured)
            {
                StatusText = auto
                    ? string.Empty
                    : "No server URL configured — set ALPHA_SERVER_URL in .env.";
                return;
            }

            var (latest, hint) = await FetchLatestAsync(ct);
            await _store.SetStatusAsync(LastCheckStatusKey, DateTime.UtcNow.ToString("O"), ct);

            if (latest is null)
            {
                StatusText = auto ? string.Empty : (hint ?? "No release found yet.");
                return;
            }

            if (UpdateInfo.CompareVersions(latest.Version, AppInfo.Version) <= 0)
            {
                UpdateAvailable = false;
                RestartReady = false;
                StatusText = auto ? string.Empty : $"You're up to date ({AppInfo.Version}).";
                return;
            }

            // A version the user already dismissed stays dismissed on auto-checks.
            var dismissed = await _store.GetStatusAsync(DismissedVersionKey, ct);
            if (auto && string.Equals(dismissed, latest.Version, StringComparison.Ordinal))
            {
                StatusText = string.Empty;
                return;
            }

            LatestVersion = latest.Version;
            ReleaseNotes = latest.ReleaseNotes;
            AssetName = latest.AssetName;
            _pending = latest;

            _logger.LogDebug("Update available: {Version} ({Asset})", latest.Version, latest.AssetName);

            // Windows auto-check may queue the service. It never asks for UAC.
            // The Update button decides silent poll vs the approval installer.
            if (OperatingSystem.IsWindows())
            {
                if (auto)
                {
                    var queued = await TryQueueSilentServiceUpdateAsync(ct);
                    UpdateAvailable = false;
                    StatusText = string.Empty;
                    if (!queued)
                        _logger.LogDebug("Silent update service did not accept the check — will retry next cadence");
                    return;
                }

                UpdateAvailable = true;
                StatusText = $"Version {latest.Version} is ready — installed {AppInfo.Version} is running. Click Update to install.";
                return;
            }

            UpdateAvailable = true;
            StatusText = $"Update available: v{latest.Version}";

            if (auto && _config.UpdateAutoInstall
                && !(latest.Version == _autoInstallVersion && DateTime.UtcNow - _autoInstallAt < AutoInstallRetryAfter))
            {
                _autoInstallVersion = latest.Version;
                _autoInstallAt = DateTime.UtcNow;
                // Linux: download + pkexec. Windows never reaches here.
                _ = Task.Run(() => AutoInstallQuietAsync(latest, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Update check failed: {ex.Message}";
            _logger.LogWarning(ex, "Update check failed");
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task AutoInstallQuietAsync(UpdateInfo info, CancellationToken ct)
    {
        if (!await _installGate.WaitAsync(0)) return; // a manual install is already in flight
        try
        {
            await DownloadAndInstallAsync(info, ct);
        }
        finally
        {
            _installGate.Release();
        }
    }

    /// <summary>Shared download → install pipeline used by BOTH the manual button and auto-install.</summary>
    private async Task DownloadAndInstallAsync(
        UpdateInfo info, CancellationToken ct, bool userApprovedWindowsInstaller = false)
    {
        client.Core.UpdatePriority.SetLocal(info.Version, "gui_download");
        try
        {
            await DownloadAsync(info, ct);
            var installerPath = Path.Combine(GetUpdatesDir(), info.AssetName);
            StatusText = "Verifying the installer…";
            var length = File.Exists(installerPath) ? new FileInfo(installerPath).Length : 0;
            if (!UpdateInstallDecision.PublishedSizeMatches(info.AssetSize, length))
            {
                try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { /* leftover is unused */ }
                StatusText = $"Downloaded installer is {length} bytes; the release list says {info.AssetSize}. It was not run.";
                return;
            }

            var installed = await InstallAsync(installerPath, ct, userApprovedWindowsInstaller);
            if (installed)
            {
                _pending = null;
                UpdateAvailable = false; // banner hides; RestartReady (Linux) keeps the top-bar Restart button
            }
        }
        catch (Exception ex)
        {
            // Progress can hit 100% before the final rename — reset so the UI
            // does not look "done" when install never started.
            DownloadProgress = 0;
            // Keep the banner + install button available for a manual retry.
            StatusText = $"Update failed: {ex.Message}";
            _logger.LogWarning(ex, "Update install failed");
        }
        finally
        {
            // Windows silent path clears via the service helper; GUI/Linux clears here.
            if (!OperatingSystem.IsWindows())
                client.Core.UpdatePriority.ClearLocal();
            else
                client.Core.UpdatePriority.Invalidate();
        }
    }

    /// <summary>
    /// GET published releases from the Alpha API bridge and pick the latest stable
    /// installer for this platform. Returns (null, hint) when there is nothing to
    /// install (no releases / bridge unavailable / no asset for this platform).
    /// </summary>
    private async Task<(UpdateInfo? Info, string? Hint)> FetchLatestAsync(CancellationToken ct)
    {
        var baseUrl = _config.ServerUrl!.TrimEnd('/');
        var url = $"{baseUrl}/api/v1/releases";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.TryParseAdd($"{AppInfo.PackageName}/{AppInfo.Version}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogDebug("Releases bridge check returned {Code}", (int)resp.StatusCode);
            return (null, resp.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                ? "Update service unavailable on the server."
                : resp.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "No release published yet."
                    : $"Update check failed (HTTP {(int)resp.StatusCode}).");
        }

        var payload = await resp.Content.ReadFromJsonAsync<BridgeReleasesPayload>(ct);
        if (payload?.Releases is null || payload.Releases.Count == 0)
            return (null, "No release data returned by the server.");

        // Prefer the server's latestStable; else first non-prerelease; else newest row.
        BridgeRelease? release = null;
        if (!string.IsNullOrWhiteSpace(payload.LatestStable))
        {
            release = payload.Releases.FirstOrDefault(r =>
                string.Equals(r.TagName, payload.LatestStable, StringComparison.OrdinalIgnoreCase)
                && !r.Prerelease);
        }
        release ??= payload.Releases.FirstOrDefault(r => !r.Prerelease)
                    ?? payload.Releases[0];

        var version = UpdateInfo.NormalizeVersion(release.TagName);
        if (version is null)
            return (null, $"Latest release tag \"{release.TagName}\" carries no version.");

        var asset = ResolvePlatformAsset(release.Assets, version);
        if (asset is null)
        {
            _logger.LogDebug("No installer asset for this platform in release {Tag}", release.TagName);
            return (null, $"No installer for this platform in release {release.TagName}.");
        }

        DateTime? published = null;
        if (DateTime.TryParse(release.PublishedAt, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            published = parsed.ToUniversalTime();

        return (new UpdateInfo
        {
            Version = version,
            TagName = release.TagName,
            ReleaseNotes = release.Body ?? string.Empty,
            AssetName = asset.Name,
            AssetId = asset.Id,
            AssetSize = asset.Size,
            DownloadUrl = $"{baseUrl}/api/v1/downloads/{asset.Id}",
            PublishedAt = published
        }, null);
    }

    /// <summary>
    /// Picks the installer asset for this platform from bridge metadata. Prefers the
    /// server's <c>platform</c> field, then filename extension / arch. When multiple
    /// candidates exist, the name embedding the release version wins.
    /// </summary>
    private BridgeReleaseAsset? ResolvePlatformAsset(IReadOnlyList<BridgeReleaseAsset> assets, string version)
    {
        var platform = CurrentPlatformKey();
        var forPlatform = assets
            .Where(a => string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (forPlatform.Count == 0)
        {
            // Backward-compatible: older bridge payloads / mixed naming — fall back to extension.
            forPlatform = platform switch
            {
                "windows" => assets.Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList(),
                "linux" => assets.Where(a => a.Name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase)).ToList(),
                "macos" => assets.Where(a => a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)).ToList(),
                _ => new List<BridgeReleaseAsset>()
            };
        }

        if (platform == "linux")
        {
            var arch = RuntimeArchSuffix();
            var nativeArch = forPlatform
                .Where(a => a.Name.Contains(arch, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return PreferVersion(nativeArch, version)
                   ?? PreferVersion(forPlatform, version)
                   ?? nativeArch.FirstOrDefault()
                   ?? forPlatform.FirstOrDefault();
        }

        return PreferVersion(forPlatform, version) ?? forPlatform.FirstOrDefault();
    }

    private static string CurrentPlatformKey()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "macos";
        return "";
    }

    /// <summary>
    /// Returns the first asset whose name embeds the release version ("1.1.1"), or
    /// null when none does. Boundary-aware so "1.1.10" never matches "1.1.1".
    /// </summary>
    private static BridgeReleaseAsset? PreferVersion(IReadOnlyList<BridgeReleaseAsset> candidates, string version)
    {
        if (string.IsNullOrWhiteSpace(version) || candidates.Count == 0) return null;
        var rx = new System.Text.RegularExpressions.Regex(
            @"(^|[\-_.vV])" + System.Text.RegularExpressions.Regex.Escape(version) + @"(?=$|[\-_.])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return candidates.FirstOrDefault(a => rx.IsMatch(a.Name));
    }

    private static string RuntimeArchSuffix()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        return arch switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "amd64",
            _ => "amd64"
        };
    }

    private async Task DownloadAsync(UpdateInfo info, CancellationToken ct)
    {
        var dir = GetUpdatesDir();
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, info.AssetName);
        var part = dest + ".part";

        IsDownloading = true;
        DownloadProgress = 0;
        StatusText = $"Downloading {info.AssetName}…";
        try
        {
            // Drop stale artifacts from a prior attempt so Move never fights a locked dest.
            await DeleteFileWithRetryAsync(part, ct);
            await DeleteFileWithRetryAsync(dest, ct);

            using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            using var resp = await _downloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            // Write inside a nested block so the part stream is CLOSED before we rename
            // it on Windows — File.Move fails with "used by another process" while the
            // FileStream is still open (Linux rename is more permissive, which hid this).
            {
                await using var dst = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long read = 0;
                while (true)
                {
                    var n = await src.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0) DownloadProgress = Math.Min(100, read * 100.0 / total);
                }
                await dst.FlushAsync(ct);
            }

            await MoveInstallerPartAsync(part, dest, ct);
            StatusText = $"Downloaded {info.AssetName}";
            DownloadProgress = 100;
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            throw;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>
    /// Force-deletes the whole updates download dir — every installer and any .part
    /// leftover, then the folder itself. Download artifacts are single-use; once the
    /// installer has run (or at startup, where anything present is a leftover) they
    /// must not linger on disk. Failures are tolerated and logged — a transient AV
    /// lock on Windows can survive the retries, and the next download already prunes
    /// its own dest before writing.
    /// </summary>
    private async Task CleanupUpdatesDirectoryAsync(CancellationToken ct)
    {
        var dir = GetUpdatesDir();
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir))
            {
                await DeleteFileWithRetryAsync(file, ct);
            }
            // Delete the now-empty folder; a still-locked file leaves it behind and is
            // swept again on the next cleanup pass (startup / next successful install).
            try { Directory.Delete(dir, recursive: false); } catch (IOException) { }
            _logger.LogInformation("Cleaned up downloaded update artifacts in {Dir}", dir);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fully clean the updates directory");
        }
    }

    /// <summary>Renames the finished .part download to the final installer name.</summary>
    private static async Task MoveInstallerPartAsync(string part, string dest, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (File.Exists(dest))
                    File.Delete(dest);
                File.Move(part, dest);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                // AV/indexer may briefly hold the dest from a prior download.
                await Task.Delay(400, ct);
            }
        }
    }

    private static async Task DeleteFileWithRetryAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(250, ct);
            }
        }
    }

    /// <summary>Hands the downloaded installer to the OS. Returns true on success.</summary>
    private async Task<bool> InstallAsync(
        string installerPath, CancellationToken ct, bool userApprovedWindowsInstaller = false)
    {
        if (OperatingSystem.IsLinux())
        {
            StatusText = "Preparing update handoff…";
            try
            {
                var updatesDir = Path.GetDirectoryName(installerPath) ?? "/tmp";
                var exePath = Environment.ProcessPath ?? "/usr/bin/alpha-ai-tracker";
                var pid = Environment.ProcessId;
                var scriptPath = Path.Combine(Path.GetTempPath(), $"aat_update_{Guid.NewGuid():N}.sh");

                var scriptContent = $@"#!/bin/sh
PID={pid}
INSTALLER=""{installerPath}""
EXE_PATH=""{exePath}""
UPDATES_DIR=""{updatesDir}""

# Wait for parent process to exit
COUNT=0
while kill -0 $PID 2>/dev/null; do
    sleep 0.5
    COUNT=$((COUNT+1))
    if [ $COUNT -ge 60 ]; then
        break
    fi
done

# Run installer via pkexec
pkexec dpkg -i ""$INSTALLER""
DPKG_EXIT=$?

if [ $DPKG_EXIT -eq 0 ]; then
    rm -rf ""$UPDATES_DIR"" 2>/dev/null
    if [ -x ""$EXE_PATH"" ]; then
        ""$EXE_PATH"" --background --restart &
    elif [ -x ""/usr/bin/alpha-ai-tracker"" ]; then
        ""/usr/bin/alpha-ai-tracker"" --background --restart &
    fi
fi

rm -f ""$0"" 2>/dev/null
";

                await File.WriteAllTextAsync(scriptPath, scriptContent.Replace("\r\n", "\n"), ct);
                try
                {
                    File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch { }

                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = $"\"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    StatusText = "Could not launch update helper script.";
                    return false;
                }

                StatusText = "Update handoff initiated. Restarting background tracker…";
                RestartReady = true;
                _logger.LogInformation("Linux updater helper spawned detached (script: {ScriptPath}). Exiting for update handoff.", scriptPath);

                // Trigger process exit so the helper can take over cleanly
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    Environment.Exit(0);
                });

                return true;
            }
            catch (Exception ex)
            {
                StatusText = $"Install handoff failed: {ex.Message}";
                return false;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            if (!userApprovedWindowsInstaller)
            {
                _ = installerPath;
                var queued = await TryQueueSilentServiceUpdateAsync(ct);
                StatusText = queued
                    ? "Update handed to AlphaAITracker.Service (silent, no UAC)…"
                    : "AlphaAITracker.Service unreachable — a second installer was not started.";
                return queued;
            }

            return await LaunchApprovedWindowsInstallerAsync(installerPath, ct);
        }

        if (OperatingSystem.IsMacOS())
        {
            // Open the dmg in Finder; the user drags the app to /Applications (no
            // silent installer exists for the dmg build).
            StatusText = "Opened the disk image — drag the app into Applications.";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"\"{installerPath}\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                StatusText = $"Could not open the disk image: {ex.Message}";
                return false;
            }
        }

        StatusText = "Unsupported platform for auto-update.";
        return false;
    }

    /// <summary>
    /// Runs the already-downloaded installer with an explicit Windows approval prompt.
    /// Does not report success until the helper writes <c>ok</c>. A denied prompt or a
    /// bad exit code stays a failure and can be retried.
    /// </summary>
    private async Task<bool> LaunchApprovedWindowsInstallerAsync(string installerPath, CancellationToken ct)
    {
        var dir = GetUpdatesDir();
        Directory.CreateDirectory(dir);
        var resultPath = Path.Combine(dir, "gui-update-result.txt");
        var scriptPath = Path.Combine(dir, "gui-update.ps1");
        try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { /* rewritten by the helper */ }

        var exe = Environment.ProcessPath ?? "";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$result = '{PsQuote(resultPath)}'");
        sb.AppendLine($"$installer = '{PsQuote(installerPath)}'");
        sb.AppendLine($"$exe = '{PsQuote(exe)}'");
        sb.AppendLine("try {");
        sb.AppendLine("  $setup = Start-Process -FilePath $installer `");
        sb.AppendLine("    -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' `");
        sb.AppendLine("    -Verb RunAs -Wait -PassThru");
        sb.AppendLine("  if ($null -eq $setup) { Set-Content -LiteralPath $result -Value 'fail missing-process'; exit 1 }");
        sb.AppendLine("  if ($setup.ExitCode -ne 0) {");
        sb.AppendLine("    Set-Content -LiteralPath $result -Value ('fail ' + $setup.ExitCode)");
        sb.AppendLine("    exit $setup.ExitCode");
        sb.AppendLine("  }");
        sb.AppendLine("  Set-Content -LiteralPath $result -Value 'ok'");
        sb.AppendLine("  if ($exe -and (Test-Path -LiteralPath $exe)) {");
        sb.AppendLine("    Start-Process -FilePath $exe -ArgumentList '--restart'");
        sb.AppendLine("  }");
        sb.AppendLine("  exit 0");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Set-Content -LiteralPath $result -Value ('denied ' + $_.Exception.Message)");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        await File.WriteAllTextAsync(scriptPath, sb.ToString(), ct);

        StatusText = "Waiting for Windows approval…";
        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not ask Windows for approval: {ex.Message}";
            return false;
        }

        if (proc is null)
        {
            StatusText = "Could not ask Windows for approval.";
            return false;
        }

        using (proc)
        {
            var deadline = DateTime.UtcNow.AddMinutes(15);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(resultPath))
                {
                    string text;
                    try { text = File.ReadAllText(resultPath).Trim(); }
                    catch { text = ""; }
                    if (text.StartsWith("ok", StringComparison.Ordinal))
                    {
                        StatusText = "Update installed. Restarting…";
                        ShutdownAfterApprovedInstall();
                        return true;
                    }
                    if (text.StartsWith("denied", StringComparison.Ordinal))
                    {
                        StatusText = "Windows did not approve the installer. You can retry.";
                        return false;
                    }
                    StatusText = string.IsNullOrWhiteSpace(text)
                        ? "The installer did not finish successfully. You can retry."
                        : "The installer did not finish successfully (" + text + "). You can retry.";
                    return false;
                }

                if (proc.HasExited)
                {
                    await Task.Delay(1000, ct);
                    if (!File.Exists(resultPath))
                    {
                        StatusText = "The installer step ended before it reported a result. You can retry.";
                        return false;
                    }
                    continue;
                }

                await Task.Delay(500, ct);
            }
        }

        StatusText = "Timed out waiting for Windows approval. The update was not reported as installed.";
        return false;
    }

    private static string PsQuote(string value) => value.Replace("'", "''");

    private void ShutdownAfterApprovedInstall()
    {
        App.AllowShutdown = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
                else
                {
                    Environment.Exit(0);
                }
            }
            catch
            {
                Environment.Exit(0);
            }
        });
    }

    // ─── Path helpers (Installer-Parity: never write to the install dir) ───

    /// <summary>
    /// User-writable download dir, mirroring the SQLite/log data-dir convention:
    /// %LocalAppData%\AlphaAITracker on Windows, ~/.local/share/alpha-ai-tracker elsewhere.
    /// </summary>
    public static string GetUpdatesDir()
    {
        var baseDir = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlphaAITracker")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "alpha-ai-tracker");
        return Path.Combine(baseDir, "updates");
    }
}
