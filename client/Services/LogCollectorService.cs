using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.BrowserAccessibility;
using client.Core.Models;
using client.Services.FaceVerify;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace client.Services;

public class LogCollectorService : BackgroundService
{
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private readonly AppConfig _config;
    private readonly IActivityCollector _collector;
    private readonly ILogStore _store;
    private readonly ILogger<LogCollectorService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IInstalledAppDetector _appDetector;
    private readonly IPackageDetector _packageDetector;
    private readonly IBrowserRegistry _browserRegistry;
    /// <summary>
    /// Time and Attendance (Phase 1, finalplan section 2.5): all session_events writes
    /// go through this recorder. It owns dedup + idempotency + hard 2s shutdown timeout.
    /// The previous direct call to <c>RecordSessionEventAsync("login", ...)</c> is replaced
    /// with <c>_eventRecorder.RecordAsync(SessionEventTypes.TrackerLogin)</c>.
    /// </summary>
    private readonly IEventRecorder _eventRecorder;
    private readonly ScheduleCacheService _scheduleCache;
    private readonly AttendanceAggregator _attendanceAggregator;
    private int _cycleCount;
    private string? _currentEmployeeId;
    private string? _currentEmployeeName;
    private string? _currentToken;
    private bool _trackingEnabled;
    private readonly object _trackingLock = new();
    // Serializes on-demand inventory rescans (GUI Rescan button vs InstalledSoftwareWatcher)
    // so two full OS walks never overlap.
    private readonly SemaphoreSlim _inventoryScanGate = new(1, 1);
    private DateTime _lastHardwareCollection = DateTime.MinValue;
    private DateTime _lastNetworkCollection = DateTime.MinValue;
    private string? _lastNetworkPublicIp;
    private string? _lastNetworkPrivateIp;
    private string? _lastHardwareFingerprint;

    // Session ended_at tracking: key = pid|machineId|clientSessionId → AppSession.Id
    private readonly Dictionary<string, string> _previousSessionKeys = new();

    // Accumulated foreground/background seconds per OPEN session (key = app_sessions.id).
    // The collector knows the OS foreground window every cycle; each open session earns
    // `interval` seconds of foreground or background time. Flushed to SQLite periodically
    // and on close, then the row re-syncs so the server learns the totals (2026-08-15).
    private readonly Dictionary<string, (double ForegroundSeconds, double BackgroundSeconds)> _sessionFocusSeconds = new();

    // Root app_item id per session key (for context updates)
    private readonly Dictionary<string, string> _sessionRootItems = new();

    // Dedup cooldown for context child items (URL/path): key → last update UTC
    private readonly Dictionary<string, DateTime> _contextCooldown = new();
    // Crash recovery grace period — heartbeat will be considered stale after this duration
    private static readonly TimeSpan CrashGracePeriod = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ContextCooldown = TimeSpan.FromSeconds(30);

    // System processes that are never application sessions
    private static readonly HashSet<string> NonAppProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "gnome-shell", "Xwayland", "pipewire-pulse", "wireplumber",
        "chrome_crashpad_handler", "cat", "tracker-extract", "tracker-store",
        "evolution-calendar-factory", "evolution-addressbook-factory", "goa-daemon",
        "gsd-power", "gsd-wacom", "gsd-color", "gsd-print-notifications",
        "gsd-keyboard", "gsd-media-keys", "gsd-mouse", "gsd-sound",
        "gsd-xsettings", "gsd-datetime", "gsd-housekeeping",
        "ibus-engine-libpinyin", "ibus-extension-gtk3",
        "waydroid", "gnome-software",
        // 🟡 Issue 6: Additional system services that should not be tracked as user sessions
        "speech-dispatcher", "speech-dispatcher-dummy",
        "snapd-desktop-integration", "snapd",
        "gnome-remote-desktop-daemon",
        "evolution-alarm-notify",
        "update-notifier",
        "packagekitd",
        "fwupd",
        // GNOME daemons that leak through ProcessFilter (2026-07-29)
        "gvfsd-network", "gvfsd-dnssd", "gvfsd-recent", "gvfsd-http",
        "gvfs-udisks2-volume-monitor", "goa-identity-service",
        "gsd-usb-protection", "gsd-smartcard", "gsd-sharing",
        "gsd-screensaver-proxy", "gsd-rfkill", "gsd-printer",
        "gsd-disk-utility-notify", "evolution-source-registry",
        "gnome-shell-calendar-server", "at-spi2-registryd",
        "gnome-shell-calendar-server", "gsd-media-keys",
        "gsd-power", "gsd-print-notifications",
        // Additional system processes found linked to wrong app entries
        "gcr-ssh-agent", "gdm-wayland-session", "gdm",
        "mutter-x11-frames", "user-session-helper",
        "tracker-miner-fs-3", "dconf-service", "VBCSCompiler",
        // 🟡 Phase 0a: Shell interpreters that leak through GUI gate (2026-07-30)
        "sh", "bash", "zsh", "dash", "fish",
        // 🟡 Phase 0a: System tools that have no GUI .desktop but were auto-registered
        "ssh-agent", "unattended-upgrade-shutdown",
        "snap",
        // 🟡 Phase 0a: VS Code subprocess that isn't a user-facing app
        "opencode",
        // 🟡 Phase 0a: Orphan process that was slipping through without installed_app_id
        "file-manager",
        // 🟡 Phase 0a: GNOME virtual filesystem daemons
        "gvfsd-trash",
        // 🟡 Phase 0b: GNOME search provider daemon (not the Settings GUI)
        "gnome-control-center-search-provider",
        // Embedded WebView2 runtime host — not a user-facing application
        // (spawned by WhatsApp, Teams, VS Code, etc. as a child process).
        "msedgewebview2",
    };

    /// <summary>
    /// Prefix patterns for system/background processes that should never be tracked.
    /// Checked after exact NonAppProcesses match.
    /// </summary>
    private static readonly string[] NonAppProcessPrefixes =
    {
        "gvfsd-", "gvfs-", "gsd-", "goa-", "evolution-",
        "ibus-", "at-spi2-", "gnome-shell-", "tracker-",
        "gdm", "mutter-",
        // 🟡 Phase 0a: VS Code / .NET subprocesses (extension host, language server, etc.)
        "microsoft.",
    };

    // Cached known binary names from installed_applications (refreshed from SQLite)
    private HashSet<string> _knownAppBinaryNames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastKnownNamesRefresh = DateTime.MinValue;

    // Cached executable paths per process name (Windows structural gate). Resolving a path
    // via Process.GetProcessesByName enumerates the whole process table, so the result is
    // memoized per name with a 5-minute TTL — the path of a running exe is stable.
    private readonly Dictionary<string, (string path, DateTime at)> _execPathCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ExecPathCacheTtl = TimeSpan.FromMinutes(5);

    public LogCollectorService(
        AppConfig config,
        IActivityCollector collector,
        ILogStore store,
        ILogger<LogCollectorService> logger,
        HttpClient httpClient,
        IInstalledAppDetector appDetector,
        IPackageDetector packageDetector,
        IBrowserRegistry browserRegistry,
        IEventRecorder eventRecorder,
        ScheduleCacheService scheduleCache,
        AttendanceAggregator attendanceAggregator)
    {
        _config = config;
        _collector = collector;
        _store = store;
        _logger = logger;
        _httpClient = httpClient;
        _appDetector = appDetector;
        _packageDetector = packageDetector;
        _browserRegistry = browserRegistry;
        _eventRecorder = eventRecorder;
        _scheduleCache = scheduleCache;
        _attendanceAggregator = attendanceAggregator;
    }

    public void StartTracking()
    {
        // Separate --gui process: the --background tracker already collects.
        if (ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("StartTracking no-op (GUI agent — tracker owns collection)");
            return;
        }

        lock (_trackingLock)
        {
            _trackingEnabled = true;
            _cycleCount = 0;
            _logger.LogInformation("Tracking started");
        }

        // Record login session event — only if the last persisted event isn't already an open login.
        // StartTracking() is called both at session restore AND on every explicit login, so without
        // this guard a relaunch writes a duplicate "login" row while the previous one is never closed.
        //
        // Time and Attendance Phase 1 (finalplan section 2.1 / BUG-2 fix): the literal "login"
        // is replaced with the SessionEventTypes.TrackerLogin constant so the vocabulary
        // lives in ONE place (R5). The IEventRecorder handles dedup + idempotency; we don't
        // need the old "skip if last event has the same type" check here.
        _ = _eventRecorder.RecordAsync(SessionEventTypes.TrackerLogin);

        // Power-on (finalplan section 2.1): capture the boot explicitly instead of inferring
        // it on the next launch via ReconcileStaleSessionsOnBootAsync. The crash-recovery
        // path stays as a defense-in-depth fallback for machines that were powered off
        // abruptly (no graceful shutdown -> no power_off row).
        _ = _eventRecorder.RecordAsync(SessionEventTypes.PowerOn);
        _scheduleCache.RequestImmediatePull();
        _attendanceAggregator.RequestImmediateAggregation();
        _ = PersistInstalledClientVersionAsync();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                _logger.LogInformation("Windows execution state set to prevent sleep");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to set Windows execution state");
            }
        }
    }

    /// <summary>
    /// Stop collecting and forget the signed-in employee.
    ///
    /// Used ONLY by the account-removal flow (<c>--remove-account</c> / the
    /// REMOVE_ACCOUNT single-instance signal): the collector is the one producer the
    /// main loop owns, and without this it would keep writing app_sessions for an
    /// account that no longer exists — including straight back into the tables that
    /// were just emptied. The main loop parks on the 5s "waiting for login" branch
    /// (identical to a fresh install), the browser/desktop-event producers gate on
    /// their own employee lookup, and SyncService already no-ops without credentials.
    ///
    /// Tracking resumes normally on the next successful login (StartTracking) or
    /// session restore — nothing here is persisted, so the flag dies with the process.
    /// </summary>
    public void StopTracking()
    {
        lock (_trackingLock)
        {
            _trackingEnabled = false;
            // Clear the in-memory identity too — a stale employee id/name/token would
            // otherwise be stamped onto anything that slipped through before the wipe.
            _currentEmployeeId = null;
            _currentEmployeeName = null;
            _currentToken = null;
        }
        _logger.LogInformation("Tracking stopped (account removed)");
    }

    /// <summary>
    /// After reinstall/upgrade the employee stays logged in (SQLite credentials), so
    /// employee-login never re-runs and <c>employee_devices.client_version</c> used to
    /// stay stale. Writing app_status.client_version with is_synced=0 queues a sync, and
    /// DeviceAuth X-Client-Version headers refresh the device row on every request.
    /// </summary>
    private async Task PersistInstalledClientVersionAsync()
    {
        try
        {
            var version = AppInfo.Version;
            var previous = await _store.GetStatusAsync("client_version", CancellationToken.None);
            if (string.Equals(previous, version, StringComparison.Ordinal))
                return;
            await _store.SetStatusAsync("client_version", version, CancellationToken.None);
            _logger.LogInformation("Client version status updated {Prev} → {Version} (queued for sync)", previous ?? "(none)", version);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PersistInstalledClientVersionAsync failed");
        }
    }

    public void SetEmployeeInfo(string employeeId, string employeeName, string token)
    {
        lock (_trackingLock)
        {
            _currentEmployeeId = employeeId;
            _currentEmployeeName = employeeName;
            _currentToken = token;
        }
    }

    public bool IsTrackingEnabled
    {
        get { lock (_trackingLock) { return _trackingEnabled; } }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("LogCollectorService parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        _logger.LogInformation(
            "LogCollectorService starting (machine={MachineId}, interval={Interval}s, session={SessionId}) — waiting for login to begin tracking",
            _config.ClientId, _config.CollectIntervalSec, SessionInfo.SessionId);

        await _store.InitializeAsync(stoppingToken);
        await RefreshEmployeeInfo(stoppingToken);

        // ─── Headless session restore (boot / --background mode) ───
        // _trackingEnabled is ONLY ever set by StartTracking(), which was previously
        // called exclusively from the Avalonia GUI (MainViewModel.InitializeAsync on
        // session restore, LoginAsync on explicit login). At boot the client auto-starts
        // with --background (services only — no window, no MainViewModel), so the main
        // loop below spun on "waiting for login" forever: browser journeys kept flowing
        // (AccessibilityBrowserTracker restores the login from SQLite itself) but ZERO
        // app sessions were ever collected — the web dashboard showed browser activity
        // only, with no installed-app activity or app sessions. Restore the persisted
        // session here exactly like the GUI does: if employee credentials exist in
        // SQLite, begin tracking immediately. The GUI then becomes login-only — opening
        // or closing it can no longer start or stop the tracking services.
        if (!string.IsNullOrEmpty(_currentEmployeeId) && !string.IsNullOrEmpty(_currentToken))
        {
            StartTracking();
            _logger.LogInformation("Session restored from SQLite — tracking started in background mode");
            try
            {
                var info = await _store.GetEmployeeInfoAsync(stoppingToken);
                if (info != null)
                    FaceVerifyLauncher.TryLaunchOnSessionStart(_config, info);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Face verify launch after headless restore failed");
            }
        }

        // ─── Reconcile stale sessions from previous crashes ───
        // Uses last_heartbeat_at timestamp to detect crashes/poweroffs and
        // close orphaned sessions with the correct approximate ended_at time.
        await ReconcileStaleSessionsOnBootAsync(stoppingToken);

        // ─── Clean up garbage app_session rows from old Chromium subprocess bug ───
        // Removes rows where process_name contains --type= or is abnormally long.
        // These were created before the headless-subprocess filter was in place.
        await CleanupGarbageSessionRowsAsync(stoppingToken);

        // ─── Phase 0a: Clean up non-GUI processes that were auto-registered before the GUI gate ───
        // Removes installed_applications entries for shell interpreters (sh), system tools (snap),
        // and closes their orphaned open sessions. These were auto-registered by the old code path
        // that put anything in /usr/bin/ into the DB without checking for a .desktop file.
        await CleanupNonGuiAppEntriesAsync(stoppingToken);

        // ─── Phase 0b: Windows runtime-auto-registration junk sweep ───
        // Removes installed_applications rows with an empty desktop_id (the structural signature
        // of runtime auto-registration — svchost → "Windows Services", dllhost, conhost, Video.UI,
        // SearchApp…) and closes the sessions they produced. Real inventory rows always carry a
        // Start Menu shortcut name / registry key / .desktop filename as desktop_id.
        await CleanupWindowsJunkSessionsAsync(stoppingToken);

        var interval = TimeSpan.FromSeconds(Math.Max(5, _config.CollectIntervalSec));

        // Collect hardware and network info immediately on startup
        await CollectDeviceHardwareAsync(stoppingToken);
        await CollectNetworkInfoAsync(stoppingToken);

        // Collect the installed app/package inventory immediately too — otherwise a fresh
        // DB shows empty installed_* tables for the first ~15 minutes (the periodic scan
        // runs every 30 collection cycles). Long-lived Linux DBs never hit this; fresh
        // Windows DBs did, which read as "the tables are missing".
        try
        {
            await CollectInstalledApplicationsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Initial installed software scan failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (ClientProcessMode.ParkTrackingWork || UpdatePriority.ShouldYield)
                {
                    await Task.Delay(UpdatePriority.ShouldYield ? 10_000 : 5000, stoppingToken);
                    continue;
                }

                if (!_trackingEnabled)
                {
                    // Soft face-verify / GUI login must not leave the --background tracker
                    // parked forever: GUI SaveEmployeeInfo writes SQLite while this process
                    // had no credentials at boot. Poll and StartTracking when a session appears.
                    try
                    {
                        var pending = await _store.GetEmployeeInfoAsync(stoppingToken);
                        if (pending != null
                            && !string.IsNullOrWhiteSpace(pending.EmployeeId)
                            && (!string.IsNullOrWhiteSpace(pending.Token)
                                || !string.IsNullOrWhiteSpace(pending.DeviceToken)))
                        {
                            SetEmployeeInfo(
                                pending.EmployeeId,
                                pending.Name,
                                pending.Token ?? string.Empty);
                            StartTracking();
                            // Every session start must open face-verify (GUI login race may lose).
                            FaceVerifyLauncher.TryLaunchOnSessionStart(_config, pending);
                            _logger.LogInformation(
                                "Session appeared in SQLite while waiting — tracking started + face-verify launch");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Login poll while waiting for tracking failed");
                    }

                    await Task.Delay(5000, stoppingToken);
                    continue;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();

                if (_cycleCount % 6 == 0)
                {
                    await RefreshEmployeeInfo(stoppingToken);
                }

                // ─── Refresh known app/package names from SQLite ───
                if ((DateTime.UtcNow - _lastKnownNamesRefresh).TotalMinutes > 1)
                {
                    await RefreshKnownNames(stoppingToken);
                }

                // ─── Collect process snapshots → resolve per-process ───
                var allLogs = await _collector.CollectAsync(stoppingToken);

                var processTree = ParentProcessResolver.BuildProcessTree();
                var pidNames = ParentProcessResolver.BuildProcessNameMap();
                var openRecords = await _store.GetOpenSessionRecordsAsync(stoppingToken);
                var hierarchy = new SessionHierarchyResolver(processTree, openRecords, _logger);

                // Hydrate in-memory maps from DB open sessions (survives client restart)
                // N4: recompute the scope live for still-running processes — same-boot
                // restart reuses the session; cross-boot (process gone) falls through to
                // the PID key and the stale session closes on the next cycle as before.
                //
                // Browser sessions are OWNED by the accessibility browser tracker (they
                // are excluded from resolvedLogs below), so they must NOT be hydrated
                // here: hydrating them would make the close phase treat them as stale
                // (their key never appears in currentKeys) and close live browser
                // sessions while the windows are still open — silently destroying the
                // whole journey hierarchy ~one cycle after each window opens.
                //
                // Root-PID grouping (Windows/macOS, no cgroups): one logical app runs as
                // MANY same-binary processes (VS Code = main window + renderer + GPU +
                // utility + extension hosts). Legacy DBs hold one OPEN session PER child
                // pid, so the same window hydrates to several rows. Resolve the top-most
                // same-binary ancestor (the main window process) so they collapse to ONE
                // key — keep the earliest record, close the duplicates so they stop
                // showing as "running" forever.
                var hydratedKeys = new Dictionary<string, string>(); // key → app_session_id (earliest wins)
                var duplicateCloseSessions = new List<AppSession>();
                foreach (var rec in openRecords)
                {
                    // Skip tracker-owned sessions: real browsers (by process name) AND
                    // embedded webview windows (browser_tab root on a non-browser process
                    // like VS Code). Without the item-type check the webview session would
                    // hydrate under the SAME key as the host app's own session and be
                    // closed as a "duplicate" one cycle later.
                    if (_browserRegistry.IsBrowser(
                            AppProcessClassifier.ExtractBaseProcessName(rec.ProcessName)) ||
                        rec.ItemType == "browser_tab")
                        continue;

                    var scope = CgroupResolver.GetAppScope(rec.ProcessId);
                    var rootPid = ResolveRootPid(rec.ProcessId, processTree, pidNames);
                    var openKey = BuildSessionKey(rootPid, scope, rec.InstalledAppId);

                    if (hydratedKeys.ContainsKey(openKey))
                    {
                        duplicateCloseSessions.Add(new AppSession { Id = rec.AppSessionId, EndedAt = DateTime.UtcNow });
                        continue;
                    }
                    hydratedKeys[openKey] = rec.AppSessionId;
                    _sessionRootItems[openKey] = rec.RootItemId;
                    if (!_previousSessionKeys.ContainsKey(openKey))
                        _previousSessionKeys[openKey] = rec.AppSessionId;
                }

                if (duplicateCloseSessions.Count > 0)
                {
                    _logger.LogInformation(
                        "Closing {Count} legacy duplicate open session(s) collapsed by root-PID grouping",
                        duplicateCloseSessions.Count);
                    await _store.CloseSessionsAndAppItemsAsync(duplicateCloseSessions, DateTime.UtcNow, stoppingToken);
                }

                // Resolve display name, FK, isBrowser, and filter for each process.
                // rootPid = the top-most same-binary ancestor (main window process on
                // Windows/macOS) — the session-grouping identity when no cgroup scope exists.
                var resolvedLogs = new List<(ActivityLog log, string? displayName, string? appId, string? pkgId, bool isBrowser, string? scope, int rootPid)>();
                foreach (var log in allLogs)
                {
                    try
                    {
                        var (isKnown, displayName, appId, pkgId, isBrowser) = await ResolveAppInfo(
                            log.ProcessName, log.WindowTitle, stoppingToken);

                        if (!isKnown) continue;

                        // Browsers are owned by the accessibility browser tracker, which
                        // captures the full journey (per-window sessions, tab rotations,
                        // navigations, downloads, incognito) with per-window identity.
                        // Skipping them here prevents one browser window from producing
                        // TWO parallel sessions (main loop + tracker) and their duplicated
                        // items. When browser tracking is disabled, the main loop still
                        // tracks browsers as plain GUI apps (fallback).
                        //
                        // The hints check covers the window right after a DB wipe, before
                        // the installed-app scan has marked the browser rows is_browser=1
                        // (auto-registered entries start with IsBrowser=false).
                        if (_config.BrowserTrackingEnabled &&
                            (isBrowser ||
                             _browserRegistry.IsBrowser(
                                 AppProcessClassifier.ExtractBaseProcessName(log.ProcessName))))
                            continue;

                        // N3: resolve the systemd cgroup scope ONCE per log, then thread it
                        // through the tuple so every BuildSessionKey call site derives the
                        // same key. Do NOT re-read /proc/<pid>/cgroup at multiple places.
                        var scope = CgroupResolver.GetAppScope(log.ProcessId);
                        // N5: same for the root pid — resolve it once so every BuildSessionKey
                        // call site (session key, focus accounting, context updates) derives
                        // the same identity.
                        var rootPid = ResolveRootPid(log.ProcessId, processTree, pidNames);

                        resolvedLogs.Add((log, displayName, appId, pkgId, isBrowser, scope, rootPid));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Skipping process '{Process}' due to resolution error", log.ProcessName);
                        continue;
                    }
                }

                resolvedLogs.Sort((a, b) =>
                    GetProcessPriority(AppProcessClassifier.ExtractBaseProcessName(a.log.ProcessName), a.isBrowser)
                        .CompareTo(GetProcessPriority(AppProcessClassifier.ExtractBaseProcessName(b.log.ProcessName), b.isBrowser)));

                // Build set of current running session keys (scope-aware / root-pid aware)
                var currentKeys = new Dictionary<string, string>();
                foreach (var (_, _, appId, _, _, scope, rootPid) in resolvedLogs)
                {
                    var key = BuildSessionKey(rootPid, scope, appId);
                    currentKeys[key] = string.Empty;
                }

                // Close sessions that were running last cycle but aren't now
                var closeSessions = new List<AppSession>();
                foreach (var kvp in _previousSessionKeys)
                {
                    if (!currentKeys.ContainsKey(kvp.Key))
                    {
                        closeSessions.Add(new AppSession
                        {
                            Id = kvp.Value,
                            EndedAt = DateTime.UtcNow
                        });
                        _sessionRootItems.Remove(kvp.Key);
                    }
                    else
                    {
                        currentKeys[kvp.Key] = kvp.Value;
                    }
                }

                var newSessions = new List<AppSession>();
                var newItems = new List<AppItem>();
                var contextUpdates = new List<AppItem>();

                foreach (var (log, displayName, appId, pkgId, isBrowser, scope, rootPid) in resolvedLogs)
                {
                    var key = BuildSessionKey(rootPid, scope, appId);

                    if (!string.IsNullOrEmpty(currentKeys.GetValueOrDefault(key)))
                    {
                        await UpdateActivityContextAsync(
                            currentKeys[key], log, appId, pkgId, isBrowser, contextUpdates, stoppingToken, displayName, scope, rootPid);
                        continue;
                    }

                    var baseProcessName = AppProcessClassifier.ExtractBaseProcessName(log.ProcessName);
                    // Resolve the parent chain for the TOP-MOST process (the main window), not
                    // the individual child — children no longer create their own sessions.
                    var parentLink = hierarchy.ResolveParent(rootPid, baseProcessName);
                    var appDisplayName = displayName ?? log.WindowTitle ?? log.ProcessName;
                    var rootItemType = AppProcessClassifier.ResolveRootItemType(
                        baseProcessName, appId, pkgId, log.WindowTitle, isBrowser);

                    var browserProfile = isBrowser
                        ? ParentProcessResolver.GetBrowserProfile(baseProcessName, log.ProcessId)
                        : null;

                    var parsed = ActivityContextParser.Parse(
                        baseProcessName, log.WindowTitle, rootItemType, browserProfile, displayName);

                    var session = new AppSession
                    {
                        ProcessName = log.ProcessName,
                        AppDisplayName = appDisplayName,
                        StartedAt = log.Timestamp,
                        EndedAt = null,
                        MachineId = _config.ClientId,
                        EmployeeId = _currentEmployeeId,
                        EmployeeName = _currentEmployeeName,
                        SessionId = SessionInfo.SessionId,
                        Platform = log.Platform,
                        InstalledAppId = appId,
                        InstalledPackageId = pkgId,
                        // One session per logical window: store the main process pid so
                        // hierarchy + hydration lookups stay stable across cycles.
                        ProcessId = rootPid,
                        ParentProcessId = parentLink?.ParentProcessId,
                        GroupedBy = string.IsNullOrEmpty(scope) ? "pid" : "cgroup",
                        CgroupScope = scope,
                        ContextLabel = SessionLabelResolver.Resolve(baseProcessName, log.ProcessId, _browserRegistry),
                        // Initial activity stamp = start time. The store + the
                        // focus-flush UPDATE both refresh this on every cycle
                        // while the session is open, so the server sweeper
                        // can tell a live tracker from an offline one.
                        LastActivityAt = log.Timestamp,
                    };
                    newSessions.Add(session);
                    currentKeys[key] = session.Id;

                    var rootItem = new AppItem
                    {
                        AppSessionId = session.Id,
                        ParentItemId = parentLink?.ParentItemId,
                        ItemType = rootItemType,
                        Title = parsed.RootTitle,
                        Identifier = parsed.RootIdentifier,
                        OpenedAt = log.Timestamp,
                        ProcessId = rootPid,
                    };
                    newItems.Add(rootItem);

                    foreach (var child in parsed.Children)
                    {
                        newItems.Add(new AppItem
                        {
                            AppSessionId = session.Id,
                            ParentItemId = rootItem.Id,
                            ItemType = child.ItemType,
                            Title = child.Title,
                            Identifier = child.Identifier,
                            OpenedAt = log.Timestamp,
                        });
                    }

                    hierarchy.Register(new OpenSessionRecord
                    {
                        ProcessId = rootPid,
                        AppSessionId = session.Id,
                        RootItemId = rootItem.Id,
                        ProcessName = baseProcessName,
                        ItemType = rootItemType,
                    });

                    _sessionRootItems[key] = rootItem.Id;
                }

                // ── Foreground/background focus accounting ──
                // The collector marks the exact OS foreground PID; map it through the same
                // root-PID grouping to the SESSION that currently holds focus. Every open
                // session earns `interval` seconds this cycle — the focused one into
                // foreground_seconds, every other into background_seconds. Values are flushed
                // to SQLite every 10 cycles and on close (then the row re-syncs to the server).
                var fgKey = string.Empty;
                foreach (var (log, _, appId, _, _, scope, rootPid) in resolvedLogs)
                {
                    if (log.IsForeground)
                    {
                        fgKey = BuildSessionKey(rootPid, scope, appId);
                        break;
                    }
                }
                var stepSeconds = interval.TotalSeconds;
                foreach (var kvp in currentKeys)
                {
                    if (string.IsNullOrEmpty(kvp.Value)) continue;
                    var isFg = !string.IsNullOrEmpty(fgKey) && kvp.Key == fgKey;
                    var cur = _sessionFocusSeconds.TryGetValue(kvp.Value, out var c) ? c : (ForegroundSeconds: 0.0, BackgroundSeconds: 0.0);
                    _sessionFocusSeconds[kvp.Value] = isFg
                        ? (cur.ForegroundSeconds + stepSeconds, cur.BackgroundSeconds)
                        : (cur.ForegroundSeconds, cur.BackgroundSeconds + stepSeconds);
                }

                if (closeSessions.Count > 0)
                {
                    // Persist the final focus totals before closing, then close + re-sync.
                    await FlushSessionFocusAsync(stoppingToken);
                    await _store.CloseSessionsAndAppItemsAsync(closeSessions, DateTime.UtcNow, stoppingToken);
                }

                if (newSessions.Count > 0)
                    await _store.StoreAppSessionsAsync(newSessions, stoppingToken);

                if (newItems.Count > 0)
                    await _store.StoreAppItemsAsync(newItems, stoppingToken);

                if (contextUpdates.Count > 0)
                    await _store.StoreAppItemsAsync(contextUpdates, stoppingToken);

                _previousSessionKeys.Clear();
                foreach (var kvp in currentKeys)
                    _previousSessionKeys[kvp.Key] = kvp.Value;

                if (newSessions.Count > 0 || closeSessions.Count > 0)
                {
                    _logger.LogDebug(
                        "Sessions: {New} new, {Closed} closed, from {Total} processes in {Elapsed}ms",
                        newSessions.Count, closeSessions.Count, allLogs.Count, sw.ElapsedMilliseconds);
                }

                // ─── Collect device hardware info (every 30 cycles ~15 min) ───
                if (_cycleCount % 30 == 0)
                {
                    await CollectDeviceHardwareAsync(stoppingToken);
                }

                // ─── Collect network info (every 10 cycles ~5 min) ───
                if (_cycleCount % 10 == 0)
                {
                    await CollectNetworkInfoAsync(stoppingToken);
                }

                // ─── NO periodic installed-software scan ───
                // Install/uninstall detection is 100% EVENT-DRIVEN by InstalledSoftwareWatcher:
                // it watches the OS install locations (.desktop dirs, dpkg state, Start Menu,
                // /Applications, package-manager dirs) and calls RescanInventoryAsync the
                // moment anything changes — so an uninstall/install through terminal, software
                // center, control panel, cmd/powershell or manual file delete updates the DB
                // within seconds. There is intentionally NO every-N-minutes scan (user rule
                // 2026-08-10: no minute-based inventory polling). The only other scan is the
                // one-time startup scan at boot.

                // ─── Periodic cleanup ───
                // NOTE: network sync moved to the dedicated SyncService background loop
                // (2026-08-11) — collection NEVER blocks on the network anymore. Huge
                // backlogs (50k+ rows) drain there in byte-bounded chunks without adding
                // latency to this loop.
                _cycleCount++;
                if (_cycleCount % 10 == 0)
                {
                    await StorePermissionStatus(stoppingToken);
                }
                // Periodic focus-duration flush every 2 cycles (~1 min at the default 30s
                // interval — user rule 2026-08-18: push is_synced=0 rows to the server at
                // least every minute, so the dashboard learns growing foreground/background
                // totals for long-running sessions instead of only at close).
                if (_cycleCount % 2 == 0)
                {
                    await FlushSessionFocusAsync(stoppingToken);
                }

                // ─── Write heartbeat for crash recovery ───
                // Persists every cycle so a future crash can estimate when sessions died.
                try
                {
                    await _store.SetStatusAsync("last_heartbeat_at", DateTime.UtcNow.ToString("O"), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to write heartbeat");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during log collection cycle");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Final focus-duration flush on graceful shutdown (covers sessions that never
        // hit the periodic flush). Failures are logged at Debug — data already lives
        // in memory and the periodic flush already persisted most of it.
        try { await FlushSessionFocusAsync(CancellationToken.None); }
        catch { }

        _logger.LogInformation("LogCollectorService stopped");
    }

    /// <summary>
    /// Persist the accumulated foreground/background focus durations for all open
    /// sessions, then clear the in-memory counters. On failure the counters are kept
    /// so the next flush retries (nothing is lost).
    /// </summary>
    private async Task FlushSessionFocusAsync(CancellationToken ct)
    {
        if (_sessionFocusSeconds.Count == 0) return;
        var updates = _sessionFocusSeconds
            .Select(kv => (kv.Key, kv.Value.ForegroundSeconds, kv.Value.BackgroundSeconds))
            .ToList();
        try
        {
            await _store.UpdateAppSessionFocusAsync(updates, ct);
            _sessionFocusSeconds.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to flush session focus durations (will retry next cycle)");
        }
    }

    /// <summary>
    /// On platforms without systemd cgroups (Windows/macOS), one logical app runs as MANY
    /// same-binary processes (VS Code = main window + renderer + GPU + utility + extension
    /// hosts; Electron apps likewise). Keying sessions per-PID produced N duplicate
    /// "Visual Studio Code" rows for ONE window. This walks the PPID chain up to the
    /// TOP-MOST process with the same base name — the app's main window process — so the
    /// whole family collapses into ONE session per window (the Windows/macOS analog of the
    /// Linux systemd scope). A different binary (explorer, a shell, another app) is a hard
    /// boundary: sessions are never merged across apps.
    /// </summary>
    private static int ResolveRootPid(int pid, Dictionary<int, int> processTree, Dictionary<int, string> processNames)
    {
        if (processTree.Count == 0 || processNames.Count == 0) return pid;
        if (!processNames.TryGetValue(pid, out var baseName)) return pid;

        var visited = new HashSet<int> { pid };
        var current = pid;
        var root = pid;

        while (true)
        {
            if (!processTree.TryGetValue(current, out var ppid) || ppid <= 0) break;
            if (visited.Contains(ppid)) break;
            if (!processNames.TryGetValue(ppid, out var parentName)) break;
            if (!string.Equals(parentName, baseName, StringComparison.OrdinalIgnoreCase)) break;

            visited.Add(ppid);
            root = ppid;
            current = ppid;
        }

        return root;
    }

    private async Task RefreshKnownNames(CancellationToken ct)
    {
        try
        {
            _knownAppBinaryNames = await _store.GetAllInstalledAppBinaryNamesAsync(ct);
            _lastKnownNamesRefresh = DateTime.UtcNow;
        }
        catch
        {
            // Non-critical — fall back to in-memory detector
        }
    }

    /// <summary>
    /// Stable session identity key.
    /// Scoped processes (under a systemd app-*.scope): "scope|{scope}|{installedAppId}|{machine}|{session}"
    /// Unscoped processes: "{processId}|{machine}|{session}" (today's behavior, unchanged).
    /// The scope+appId form collapses all subprocesses of one logical window into one session;
    /// two separate windows of the same app get different scopes → different keys.
    /// </summary>
    private string BuildSessionKey(int processId, string? scope = null, string? installedAppId = null) =>
        !string.IsNullOrEmpty(scope) && !string.IsNullOrEmpty(installedAppId)
            ? $"scope|{scope}|{installedAppId}|{_config.ClientId}|{SessionInfo.SessionId}"
            : $"{processId}|{_config.ClientId}|{SessionInfo.SessionId}";

    private static int GetProcessPriority(string processName, bool isBrowser)
    {
        if (AppProcessClassifier.IsIdeProcess(processName)) return 0;
        if (isBrowser) return 1;
        if (AppProcessClassifier.IsFileManagerProcess(processName)) return 2;
        if (AppProcessClassifier.IsTerminalEmulator(processName)) return 3;
        if (AppProcessClassifier.IsShellInterpreter(processName)) return 4;
        return 5;
    }

    private async Task UpdateActivityContextAsync(
        string appSessionId,
        ActivityLog log,
        string? appId,
        string? pkgId,
        bool isBrowser,
        List<AppItem> contextUpdates,
        CancellationToken ct,
        string? displayName = null,
        string? scope = null,
        int? rootPid = null)
    {
        var baseProcessName = AppProcessClassifier.ExtractBaseProcessName(log.ProcessName);
        var rootType = AppProcessClassifier.ResolveRootItemType(
            baseProcessName, appId, pkgId, log.WindowTitle, isBrowser);

        var browserProfile = isBrowser
            ? ParentProcessResolver.GetBrowserProfile(baseProcessName, log.ProcessId)
            : null;

        var parsed = ActivityContextParser.Parse(
            log.ProcessName, log.WindowTitle, rootType, browserProfile, displayName ?? log.ProcessName);

        var existingRoot = await _store.GetOpenAppItemAsync(
            appSessionId, rootType, parsed.RootIdentifier, ct);
        if (existingRoot != null &&
            (existingRoot.Title != parsed.RootTitle || existingRoot.Identifier != parsed.RootIdentifier))
        {
            await _store.UpdateAppItemContextAsync(
                existingRoot.Id, parsed.RootTitle, parsed.RootIdentifier, ct);
            _sessionRootItems[BuildSessionKey(rootPid ?? log.ProcessId, scope, appId)] = existingRoot.Id;
        }
        else if (existingRoot == null && !string.IsNullOrWhiteSpace(log.WindowTitle))
        {
            // Root-PID grouping: the session's root item may have been created by a
            // TITLE-LESS child process first (root identifier = process name, e.g.
            // "code" instead of "project — Visual Studio Code"). When a window-bearing
            // process of the same session arrives with a REAL title, upgrade the root
            // in place so the stored item shows the actual window title.
            var openRoot = await _store.GetOpenRootItemAsync(appSessionId, rootType, ct);
            if (openRoot != null &&
                string.Equals(openRoot.Identifier, baseProcessName, StringComparison.OrdinalIgnoreCase))
            {
                await _store.UpdateAppItemContextAsync(openRoot.Id, parsed.RootTitle, parsed.RootIdentifier, ct);
                _sessionRootItems[BuildSessionKey(rootPid ?? log.ProcessId, scope, appId)] = openRoot.Id;
            }
        }

        foreach (var child in parsed.Children)
        {
            var cooldownKey = $"{appSessionId}|{child.ItemType}|{child.Identifier}";
            if (_contextCooldown.TryGetValue(cooldownKey, out var last) &&
                DateTime.UtcNow - last < ContextCooldown)
            {
                continue;
            }

            var existing = await _store.GetOpenAppItemAsync(
                appSessionId, child.ItemType, child.Identifier, ct);

            if (existing != null)
            {
                if (existing.Title != child.Title)
                {
                    await _store.UpdateAppItemContextAsync(
                        existing.Id, child.Title, child.Identifier, ct);
                }
            }
            else
            {
                var rootItemId = _sessionRootItems.GetValueOrDefault(BuildSessionKey(rootPid ?? log.ProcessId, scope, appId));
                if (string.IsNullOrEmpty(rootItemId))
                {
                    var open = await _store.GetOpenAppItemAsync(appSessionId, rootType, parsed.RootIdentifier, ct);
                    rootItemId = open?.Id ?? string.Empty;
                }

                contextUpdates.Add(new AppItem
                {
                    AppSessionId = appSessionId,
                    ParentItemId = string.IsNullOrEmpty(rootItemId) ? null : rootItemId,
                    ItemType = child.ItemType,
                    Title = child.Title,
                    Identifier = child.Identifier,
                    OpenedAt = log.Timestamp,
                });
            }

            _contextCooldown[cooldownKey] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Resolve app/package info for a process. Returns (isKnown, displayName, appId, pkgId).
    /// If the process is not yet in the DB, attempts auto-detection and saves it.
    /// Automatically strips cmdline arguments from process names (e.g., "npm run dev" → "npm").
    /// </summary>
    private async Task<(bool isKnown, string? displayName, string? appId, string? pkgId, bool isBrowser)> ResolveAppInfo(
        string processName, string? windowTitle, CancellationToken ct)
    {
        // 🟡 Check for --type= in the ORIGINAL process name (before stripping) to detect
        // headless browser subprocesses (renderer, GPU, utility, zygote, etc.).
        // Chromium-based browsers use --type=renderer, --type=zygote, etc.
        // Firefox uses --contentproc for its child processes.
        var isHeadlessSubProcess = 
            processName.Contains("--type=", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("--contentproc", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("-contentproc", StringComparison.OrdinalIgnoreCase);

        // Strip cmdline arguments from process name (e.g., "npm run dev" → "npm")
        var strippedName = AppProcessClassifier.ExtractBaseProcessName(processName);
        if (strippedName != processName && !string.IsNullOrWhiteSpace(strippedName))
        {
            return await ResolveAppInfoInner(strippedName, windowTitle, isHeadlessSubProcess, ct);
        }
        return await ResolveAppInfoInner(processName, windowTitle, isHeadlessSubProcess, ct);
    }

    private async Task<(bool isKnown, string? displayName, string? appId, string? pkgId, bool isBrowser)> ResolveAppInfoInner(
        string processName, string? windowTitle, bool isHeadlessSubProcess, CancellationToken ct)
    {
        // 🟡 Filter out headless browser subprocesses (renderer, GPU, utility, zygote, etc.)
        // at the very top — regardless of which resolution path matches.
        // Chromium-based browsers spawn --type=renderer, --type=zygote, etc.
        // Firefox uses --contentproc or -contentproc for its child processes.
        // These should NEVER be tracked as separate browser sessions.
        if (isHeadlessSubProcess && string.IsNullOrWhiteSpace(windowTitle))
            return (false, null, null, null, false);

        if (NonAppProcesses.Contains(processName) ||
            NonAppProcessPrefixes.Any(p => processName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return (false, null, null, null, false);

        // 1. Check known app binary names from installed_applications (fast in-memory path)
        // 🟡 Phase 0a: Even if the process is in the DB, verify it's actually a GUI app.
        // Pre-existing rows for non-GUI tools (sh, snap) were auto-registered before the
        // GUI gate existed and would bypass the gate if we only check _knownAppBinaryNames.
        if (_knownAppBinaryNames.Contains(processName))
        {
            var app = await _store.GetInstalledAppByBinaryNameAsync(processName, ct);
            if (app != null)
            {
                // Skip non-GUI apps that happen to be in the DB from before the GUI gate.
                // A GUI app has non-empty Categories OR is flagged as a browser. On Windows,
                // Categories is only ever set for browsers — Start Menu/registry entries have
                // no .desktop-Categories equivalent — so the gate is Linux-only.
                if (!OperatingSystem.IsWindows() && !app.IsBrowser && string.IsNullOrWhiteSpace(app.Categories))
                    return (false, null, null, null, false);

                // Windows junk guard: a real inventory row always carries a desktop_id
                // (Start Menu shortcut name / registry key / .desktop filename). Rows with
                // an empty desktop_id are the runtime auto-registered junk (svchost,
                // dllhost, conhost, Video.UI…) — refuse them even before the cleanup sweep
                // deletes them, so no new junk session can ever attach to one.
                if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(app.DesktopId))
                    return (false, null, null, null, false);

                if (app.IsBrowser && string.IsNullOrWhiteSpace(windowTitle) && isHeadlessSubProcess)
                    return (false, null, null, null, true);
                return (true, app.AppName, app.Id, null, app.IsBrowser);
            }
        }

        // 2b. Windows structural gate (no product names) — runs BEFORE the fuzzy match so a
        // CUI/system-tree binary can never fuzzy-match its way into an app row (powershell.exe
        // was matching the "Windows PowerShell ISE" row and produced 7 junk console sessions).
        // Resolve the running executable and let the OS's own metadata decide:
        //   - Anything inside C:\Windows (System32 / SystemApps / WinSxS / …) is an OS-provided
        //     component (RuntimeBroker, GameBar, Video.UI, conhost, SearchApp…) — never a user app.
        //   - A console-subsystem executable (node, git, dotnet, cmd…) is a CLI tool/runtime
        //     that belongs in installed_packages, not installed_applications.
        //
        // ⚠️ LOAD-BEARING ORDER: the gate stays AFTER the known-binary fast path above. The
        // inventory scan registers inbox System32 GUI apps (Notepad, WordPad, mspaint…) as
        // installed_applications, so their binary names hit the fast path and are never
        // rejected here. Moving this gate above the fast path would silently stop tracking
        // every inbox Windows tool.
        if (OperatingSystem.IsWindows())
        {
            var gatePath = GetCachedExecutablePath(processName);
            if (!string.IsNullOrEmpty(gatePath))
            {
                if (ExecutableMetadata.IsWindowsSystemTree(gatePath))
                    return (false, null, null, null, false);
                if (ExecutableMetadata.GetSubsystem(gatePath) == ExecutableMetadata.SubsystemWindowsCui)
                    return (false, null, null, null, false);
            }
        }

        // 2c. Fuzzy match: process may exist in DB under a similar binary name
        // 🟡 Phase 0a: Only consider fuzzy matches that could plausibly be the same app.
        // Reject matches where the process name is very short (≤3 chars) because short
        // SQL LIKE patterns like '%sh%' or '%go%' are too broad and match unrelated processes.
        var existingFuzzy = processName.Length > 3
            ? await _store.GetInstalledAppByBinaryNameFuzzyAsync(processName, ct)
            : null;
        if (existingFuzzy != null)
        {
            // Also verify the fuzzy match is actually a GUI app (Linux-only gate — see above).
            if (!OperatingSystem.IsWindows() && !existingFuzzy.IsBrowser && string.IsNullOrWhiteSpace(existingFuzzy.Categories))
                return (false, null, null, null, false);

            // Same Windows junk guard as the fast path: only inventory rows (desktop_id
            // set) can be fuzzy-matched into a session.
            if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(existingFuzzy.DesktopId))
                return (false, null, null, null, false);

            _knownAppBinaryNames.Add(processName);
            return (true, existingFuzzy.AppName, existingFuzzy.Id, null, existingFuzzy.IsBrowser);
        }

        // 3. Windows: NO runtime auto-registration. Tracking is driven STRICTLY by the
        // installed_applications inventory (Start Menu shortcut + registry Uninstall scan —
        // the Windows analog of the .desktop scan). Runtime auto-registration was the source
        // of every junk row: it registered system components (svchost → "Windows Services",
        // conhost → "Windows Console Host", Video.UI, SearchApp, …) that have no GUI of their
        // own. The inventory scan re-runs every ~15 min, so a genuinely new app is picked up
        // there within minutes — no runtime fallback needed.
        if (OperatingSystem.IsWindows())
            return (false, null, null, null, false);

        // 3b. Auto-detect (non-Windows): is this a GUI application? (has .desktop / .app bundle)
        // Only GUI applications get registered into installed_applications and tracked.
        // CLI-only tools, shells, build tools, runtimes, and daemons are all skipped.
        var execPath = GetCachedExecutablePath(processName);
        if (_appDetector.IsGuiApplication(processName, execPath))
        {
            if (!string.IsNullOrEmpty(execPath))
            {
                var autoApp = await AutoDetectInstalledGuiApp(processName, execPath, ct);
                if (autoApp != null)
                {
                    var storedAutoAppId = await _store.StoreInstalledAppAsync(autoApp, ct);
                    _knownAppBinaryNames.Add(processName);
                    return (true, autoApp.AppName, storedAutoAppId, null, autoApp.IsBrowser);
                }
            }

            // Process name matches a known GUI app but exec path unavailable — still register
            var displayName = _appDetector.ResolveDisplayName(processName);
            if (displayName != null)
            {
                var app = new InstalledApplication
                {
                    AppName = displayName,
                    BinaryName = processName,
                    InstallPath = "",
                    ChangeType = "seen",
                    DetectedAt = DateTime.UtcNow,
                };
                var storedAppId = await _store.StoreInstalledAppAsync(app, ct);
                _knownAppBinaryNames.Add(processName);
                return (true, displayName, storedAppId, null, false);
            }
        }

        // 4. Not identifiable or not a GUI app — skip
        return (false, null, null, null, false);
    }

    private static string? GetExecutablePath(string processName)
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(processName);
            if (procs.Length > 0)
            {
                try { return procs[0].MainModule?.FileName; }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Memoized executable-path lookup (5-min TTL) so the Windows structural gate never
    /// enumerates the process table for the same name on every collection cycle.</summary>
    private string? GetCachedExecutablePath(string processName)
    {
        if (_execPathCache.TryGetValue(processName, out var entry) &&
            DateTime.UtcNow - entry.at < ExecPathCacheTtl)
            return entry.path;
        var path = GetExecutablePath(processName) ?? "";
        _execPathCache[processName] = (path, DateTime.UtcNow);
        return path.Length == 0 ? null : path;
    }

    private async Task<string?> ResolveDisplayNameFromPath(string processName, string? execPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(execPath)) return null;

        if (OperatingSystem.IsLinux())
        {
            // Try to find a matching .desktop file by scanning standard locations
            var desktopDirs = new[]
            {
                "/usr/share/applications/",
                "/usr/local/share/applications/",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications")
            };
            foreach (var dir in desktopDirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.desktop"))
                {
                    try
                    {
                        var lines = await File.ReadAllLinesAsync(file, ct);
                        string? name = null, exec = null;
                        foreach (var line in lines)
                        {
                            if (line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase) && name == null)
                                name = line["Name=".Length..].Trim();
                            if (line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
                                exec = line["Exec=".Length..].Trim();
                        }
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(exec))
                        {
                            var binary = ExtractBinaryFromExecLine(exec);
                            if (string.Equals(binary, processName, StringComparison.OrdinalIgnoreCase))
                                return name;
                        }
                    }
                    catch { }
                }
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            // On Windows, the InstallPath in registry might match
            // We already checked the DB, so just return null for now
        }
        else if (OperatingSystem.IsMacOS())
        {
            // For macOS, the .app bundle name matches the binary name
            // Already handled by InstalledAppDetector
        }

        return null;
    }

    /// <summary>
    /// Auto-detect a GUI application by scanning OS application metadata.
    /// On Linux: scans .desktop files for a matching Exec= binary.
    /// On Windows: checks Program Files / WindowsApps paths.
    /// On macOS: checks for .app bundle paths.
    /// Only returns a record if the process corresponds to a real GUI application.
    /// CLI-only tools, shells, build tools, and runtimes will return null.
    /// </summary>
    private async Task<InstalledApplication?> AutoDetectInstalledGuiApp(string processName, string execPath, CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // Check if the executable is in a standard install path
                // Also accept /home/* and /media/* (project-local binaries like ./bin/server)
                if (execPath.StartsWith("/usr/bin/") || execPath.StartsWith("/usr/local/bin/") ||
                    execPath.StartsWith("/opt/") || execPath.StartsWith("/snap/bin/") ||
                    execPath.Contains("/flatpak/") ||
                    execPath.StartsWith("/home/") || execPath.StartsWith("/media/"))
                {
                    return new InstalledApplication
                    {
                        AppName = processName,
                        BinaryName = processName,
                        InstallPath = execPath,
                        ChangeType = "seen",
                        DetectedAt = DateTime.UtcNow,
                    };
                }

                // Try to find a .desktop file for this binary
                var desktopDirs = new[]
                {
                    "/usr/share/applications/",
                    "/usr/local/share/applications/",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications")
                };
                foreach (var dir in desktopDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in Directory.GetFiles(dir, "*.desktop"))
                    {
                        try
                        {
                            var lines = await File.ReadAllLinesAsync(file, ct);
                            string? name = null, exec = null;
                            foreach (var line in lines)
                            {
                                if (line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase) && name == null)
                                    name = line["Name=".Length..].Trim();
                                if (line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
                                    exec = line["Exec=".Length..].Trim();
                            }
                            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(exec))
                            {
                                var binary = ExtractBinaryFromExecLine(exec);
                                if (string.Equals(binary, processName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return new InstalledApplication
                                    {
                                        AppName = name,
                                        BinaryName = processName,
                                        InstallPath = execPath,
                                        ChangeType = "seen",
                                        DetectedAt = DateTime.UtcNow,
                                    };
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                // Executable in standard Windows paths → likely an app. Browsers get flagged so
                // they resolve as real browser apps (not raw binary names / window titles).
                if (execPath.Contains("Program Files", StringComparison.OrdinalIgnoreCase) ||
                    execPath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                    execPath.Contains("\\Microsoft\\", StringComparison.OrdinalIgnoreCase))
                {
                    var isBrowser = _browserRegistry.IsBrowser(processName);
                    return new InstalledApplication
                    {
                        AppName = _appDetector.ResolveDisplayName(processName) ?? processName,
                        BinaryName = processName,
                        InstallPath = execPath,
                        ChangeType = "seen",
                        IsBrowser = isBrowser,
                        Categories = isBrowser ? "WebBrowser" : "",
                        DetectedAt = DateTime.UtcNow,
                    };
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (execPath.Contains(".app/", StringComparison.OrdinalIgnoreCase) ||
                    execPath.StartsWith("/Applications/", StringComparison.Ordinal))
                {
                    return new InstalledApplication
                    {
                        AppName = processName,
                        BinaryName = processName,
                        InstallPath = execPath,
                        ChangeType = "seen",
                        DetectedAt = DateTime.UtcNow,
                    };
                }
            }
        }
        catch { }

        return null;
    }

    private static string? ExtractBinaryFromExecLine(string? exec)
    {
        if (string.IsNullOrWhiteSpace(exec)) return null;
        exec = exec.Trim();
        var spaceIdx = exec.IndexOf(' ');
        var firstPart = spaceIdx > 0 ? exec[..spaceIdx] : exec;
        firstPart = firstPart.Trim('"');
        var binary = Path.GetFileNameWithoutExtension(firstPart);
        return string.IsNullOrWhiteSpace(binary) ? null : binary;
    }

    private async Task RefreshEmployeeInfo(CancellationToken ct)
    {
        try
        {
            var info = await _store.GetEmployeeInfoAsync(ct);
            _currentEmployeeId = info?.EmployeeId;
            _currentEmployeeName = info?.Name;
            _currentToken = info?.Token;
        }
        catch
        {
            _currentEmployeeId = null;
            _currentEmployeeName = null;
            _currentToken = null;
        }
    }

    // ────────────────────────────────────────────
    // Device Hardware Info Collection
    // Fixes Issue 1: now collects mac_address, storage_devices, gpu_model
    // ────────────────────────────────────────────

    private async Task CollectDeviceHardwareAsync(CancellationToken ct)
    {
        try
        {
            var hostname = Environment.MachineName;
            var osName = RuntimeInformation.OSDescription;
            var osVersion = Environment.OSVersion.VersionString;
            var cpuCores = Environment.ProcessorCount;
            var cpuModel = GetCpuModel();
            var ramTotalMb = GetTotalRamMb();
            var macAddress = GetMacAddress();
            var rawStorageDevices = GetStorageDevices();
            var (gpuModel, gpuVramMb) = GetGpuInfo();

            // Build a fingerprint WITHOUT storage devices (they're relational now — doesn't change often)
            var fingerprint = $"{macAddress}|{hostname}|{osName}|{osVersion}|{cpuModel}|{cpuCores}|{ramTotalMb}|{gpuModel}";

            // Dedup against the LAST row PERSISTED IN THE DB (not just this process run) —
            // the old in-memory-only _lastHardwareFingerprint reset on every launch, so
            // relaunching with unchanged hardware always wrote a duplicate row.
            if (_lastHardwareFingerprint != fingerprint)
            {
                var lastHw = await _store.GetLastDeviceHardwareInfoAsync(ct);
                if (lastHw != null &&
                    lastHw.MacAddress == macAddress && lastHw.Hostname == hostname &&
                    lastHw.OsName == osName && lastHw.CpuModel == cpuModel &&
                    lastHw.CpuCores == cpuCores && lastHw.RamTotalMb == ramTotalMb &&
                    lastHw.GpuModel == gpuModel)
                {
                    _logger.LogDebug("Hardware unchanged since last persisted row, skipping");
                    _lastHardwareFingerprint = fingerprint;
                    if (!await _store.HasStorageDevicesAsync(ct))
                    {
                        await StoreStorageDevicesFromProbeAsync(ct);
                    }
                    return;
                }
            }
            else
            {
                // Same-process fast path: nothing changed since this process already stored it.
                if (!await _store.HasStorageDevicesAsync(ct))
                {
                    await StoreStorageDevicesFromProbeAsync(ct);
                }
                return;
            }

            var hw = new DeviceHardwareInfo
            {
                MacAddress = macAddress,
                Hostname = hostname,
                OsName = osName,
                OsVersion = osVersion,
                CpuModel = cpuModel,
                CpuCores = cpuCores,
                RamTotalMb = ramTotalMb,
                GpuModel = gpuModel,
                GpuVramMb = gpuVramMb,
                StorageDevices = JsonSerializer.Serialize(rawStorageDevices.Select(d => new
                {
                    deviceType = d.type,
                    model = d.model,
                    capacityMb = d.capacity_mb
                })),
                CollectedAt = DateTime.UtcNow
            };

            await _store.StoreDeviceHardwareInfoAsync(new[] { hw }, ct);

            // Store storage devices as relational rows
            if (rawStorageDevices.Count > 0)
            {
                var storageRows = rawStorageDevices.Select(d => new StorageDevice
                {
                    DeviceHardwareId = hw.Id,
                    DeviceType = d.type,
                    Model = d.model,
                    CapacityMb = d.capacity_mb
                }).ToList();
                await _store.StoreStorageDevicesAsync(storageRows, ct);
            }

            _lastHardwareFingerprint = fingerprint;
            _logger.LogDebug(
                "Collected device hardware info: {Hostname}, {Os}, {Cores} cores, {Ram}MB RAM, MAC={Mac}",
                hostname, osName, cpuCores, ramTotalMb, macAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect device hardware info");
        }
    }

    private async Task StoreStorageDevicesFromProbeAsync(CancellationToken ct)
    {
        var rawStorageDevices = GetStorageDevices();
        if (rawStorageDevices.Count == 0) return;

        // Attach to the LATEST hardware row (whether synced or not) — the probe may run
        // on a later cycle than the hardware insert, and the latest row is the current one.
        var lastHw = await _store.GetLastDeviceHardwareInfoAsync(ct);
        if (lastHw == null) return;

        var hwId = lastHw.Id;
        var storageRows = rawStorageDevices.Select(d => new StorageDevice
        {
            DeviceHardwareId = hwId,
            DeviceType = d.type,
            Model = d.model,
            CapacityMb = d.capacity_mb
        }).ToList();
        await _store.StoreStorageDevicesAsync(storageRows, ct);
        _logger.LogDebug("Backfilled {Count} storage device rows", storageRows.Count);
    }

    private static string GetMacAddress()
    {
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    var mac = ni.GetPhysicalAddress().ToString();
                    if (!string.IsNullOrEmpty(mac) && mac.Length >= 6)
                        return mac;
                }
            }
        }
        catch { }
        return "";
    }

    private static List<(string type, string model, long capacity_mb)> GetStorageDevices()
    {
        try
        {
            var devices = new List<(string, string, long)>();
            if (OperatingSystem.IsLinux())
            {
                // Use lsblk -J for reliable structured JSON output
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "lsblk",
                    Arguments = "-J -o NAME,TYPE,SIZE,MODEL,ROTA",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    using var doc = JsonDocument.Parse(output);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("blockdevices", out var blockDevices))
                    {
                        foreach (var dev in blockDevices.EnumerateArray())
                        {
                            var name = dev.GetProperty("name").GetString() ?? "";
                            var devType = dev.GetProperty("type").GetString() ?? "";
                            var sizeStr = dev.GetProperty("size").GetString() ?? "";
                            var model = dev.GetProperty("model").GetString() ?? "";

                            // Skip loop devices and partitions
                            if (devType != "disk") continue;

                            // Determine SSD vs HDD via ROTA flag (lsblk -J emits a JSON boolean: true = HDD, false = SSD)
                            var isRotational = dev.TryGetProperty("rota", out var rota) && rota.ValueKind == JsonValueKind.True;
                            var diskType = isRotational ? "HDD" : "SSD";

                            // Parse size string like "119.2G" to MB
                            long capacityMb = ParseSizeToMb(sizeStr);

                            devices.Add((diskType, string.IsNullOrEmpty(model) ? name : model, capacityMb));
                        }
                    }
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                foreach (var drive in System.IO.DriveInfo.GetDrives())
                {
                    if (drive.IsReady && drive.DriveType == System.IO.DriveType.Fixed)
                    {
                        devices.Add(("Fixed", drive.VolumeLabel ?? drive.Name, drive.TotalSize / (1024 * 1024)));
                    }
                }
            }
            return devices;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetStorageDevices error: {ex.Message}");
        }
        return new List<(string, string, long)>();
    }

    private static long ParseSizeToMb(string sizeStr)
    {
        try
        {
            if (string.IsNullOrEmpty(sizeStr)) return 0;
            sizeStr = sizeStr.Trim();
            // Patterns: 119.2G, 1.8T, 512M, 1024K
            if (sizeStr.EndsWith("T", StringComparison.OrdinalIgnoreCase))
            {
                var val = double.Parse(sizeStr.TrimEnd('T', 't'), System.Globalization.CultureInfo.InvariantCulture);
                return (long)(val * 1024 * 1024);
            }
            if (sizeStr.EndsWith("G", StringComparison.OrdinalIgnoreCase))
            {
                var val = double.Parse(sizeStr.TrimEnd('G', 'g'), System.Globalization.CultureInfo.InvariantCulture);
                return (long)(val * 1024);
            }
            if (sizeStr.EndsWith("M", StringComparison.OrdinalIgnoreCase))
            {
                var val = double.Parse(sizeStr.TrimEnd('M', 'm'), System.Globalization.CultureInfo.InvariantCulture);
                return (long)val;
            }
            if (sizeStr.EndsWith("K", StringComparison.OrdinalIgnoreCase))
            {
                var val = double.Parse(sizeStr.TrimEnd('K', 'k'), System.Globalization.CultureInfo.InvariantCulture);
                return (long)(val / 1024);
            }
        }
        catch { }
        return 0;
    }

    private static (string model, long vramMb) GetWindowsGpuInfo()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Select-Object -First 1 Name, AdapterRAM | ConvertTo-Json -Compress\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return ("", 0);

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return ("", 0);

            var model = doc.RootElement.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            var ram = doc.RootElement.TryGetProperty("AdapterRAM", out var r) ? r.GetInt64() : 0;
            return (model, ram > 0 ? ram / (1024 * 1024) : 0);
        }
        catch { return ("", 0); }
    }

    private static (string model, long vramMb) GetGpuInfo()
    {
        try
        {
            if (OperatingSystem.IsLinux() && System.IO.File.Exists("/proc/driver/nvidia/version"))
            {
                // NVIDIA GPU detected
                var lines = System.IO.File.ReadAllLines("/proc/driver/nvidia/version");
                if (lines.Length > 0)
                {
                    return ($"NVIDIA {lines[0].Trim()}", 0);
                }
            }
            if (OperatingSystem.IsLinux())
            {
                // Try lspci for GPU info
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "lspci",
                    Arguments = "-vnn",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(2000);
                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Contains("VGA") || line.Contains("3D") || line.Contains("Display"))
                        {
                            return (line.Trim(), 0);
                        }
                    }
                }
            }
            if (OperatingSystem.IsWindows())
            {
                // PowerShell + Win32_VideoController (no System.Management package needed).
                return GetWindowsGpuInfo();
            }
        }
        catch { }
        return ("", 0);
    }

    private static string GetCpuModel()
    {
        try
        {
            if (OperatingSystem.IsLinux() && System.IO.File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in System.IO.File.ReadAllLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name"))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1)
                            return parts[1].Trim();
                    }
                }
            }
            if (OperatingSystem.IsWindows())
                return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        }
        catch { }
        return "";
    }

    private static long GetTotalRamMb()
    {
        try
        {
            if (OperatingSystem.IsLinux() && System.IO.File.Exists("/proc/meminfo"))
            {
                foreach (var line in System.IO.File.ReadAllLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:"))
                    {
                        var parts = line.Split(':', StringSplitOptions.TrimEntries);
                        if (parts.Length > 1)
                        {
                            var valStr = parts[1].Split(' ')[0];
                            if (long.TryParse(valStr, out var kb))
                                return kb / 1024;
                        }
                    }
                }
            }
            if (OperatingSystem.IsWindows())
            {
                // Real physical RAM via GlobalMemoryStatusEx (no fake hardcoded value).
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref status))
                    return (long)(status.ullTotalPhys / (1024 * 1024));
            }
        }
        catch { }
        return 0;
    }

    // ────────────────────────────────────────────
    // Network Info Collection
    // Fixes Issue 3: adds public_ip lookup, dedup by IP change,
    // removes mac_address (already in device_hardware_info)
    // ────────────────────────────────────────────

    private async Task CollectNetworkInfoAsync(CancellationToken ct)
    {
        try
        {
            // Get public IP from external service
            var publicIp = await GetPublicIpAsync(ct);

            var now = DateTime.UtcNow;

            // Collect interface-derived private IPs FIRST. The dedup key must come from the
            // SAME source that gets stored (interface unicast addresses) — the old code
            // keyed dedup on Dns.GetHostEntry (which resolves to 127.0.1.1 on this host)
            // while storing interface IPs (192.168.88.66), so the two NEVER matched and a
            // fresh DB row was written on every cycle/startup.
            var upInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                             ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .ToList();

            var primaryPrivateIp = string.Empty;
            foreach (var ni in upInterfaces)
            {
                try
                {
                    var unicastAddr = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (unicastAddr == null) continue;
                    primaryPrivateIp = unicastAddr.Address.ToString();
                    break;
                }
                catch { }
            }

            // Dedup against the last row PERSISTED IN THE DB — the old in-memory
            // _lastNetwork* fields reset on every launch, so relaunching with the same
            // IPs always wrote a fresh duplicate row. The DB row carries is_current so
            // exactly one row per IP-identity is "active" at any time.
            if (publicIp == _lastNetworkPublicIp && primaryPrivateIp == _lastNetworkPrivateIp &&
                !string.IsNullOrEmpty(publicIp))
            {
                // Same-process fast path — already recorded in this run.
                await _store.TouchCurrentNetworkInfoAsync(now, ct);
                _logger.LogDebug("Network info unchanged (public={PublicIp}, private={PrivateIp}), skipping", publicIp, primaryPrivateIp);
                return;
            }

            var current = await _store.GetLastNetworkInfoAsync(ct);
            if (current != null && !string.IsNullOrEmpty(current.PublicIp) &&
                current.PublicIp == publicIp && current.PrivateIp == primaryPrivateIp)
            {
                // Same identity as the persisted current row — touch it, don't duplicate.
                _lastNetworkPublicIp = publicIp;
                _lastNetworkPrivateIp = primaryPrivateIp;
                await _store.TouchCurrentNetworkInfoAsync(now, ct);
                _logger.LogDebug("Network info unchanged vs persisted row (public={PublicIp}, private={PrivateIp}), skipping", publicIp, primaryPrivateIp);
                return;
            }

            // Network identity changed (or first ever record) — demote old current rows
            // and insert fresh is_current=1 rows so the history preserves each IP change.
            await _store.MarkAllNetworkInfoNotCurrentAsync(ct);

            foreach (var ni in upInterfaces)
            {
                try
                {
                    var ipProps = ni.GetIPProperties();
                    var unicastAddr = ipProps.UnicastAddresses
                        .FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (unicastAddr == null) continue;

                    var info = new NetworkInfo
                    {
                        PublicIp = publicIp,
                        PrivateIp = unicastAddr.Address.ToString(),
                        NetworkInterfaceName = ni.Name,
                        CollectedAt = now,
                        FirstSeenAt = now,
                        LastSeenAt = now,
                        IsCurrent = true
                    };

                    await _store.StoreNetworkInfoAsync(new[] { info }, ct);
                }
                catch { }
            }

            _lastNetworkPublicIp = publicIp;
            _lastNetworkPrivateIp = primaryPrivateIp;
            _logger.LogDebug("Network info saved (public={PublicIp}, private={PrivateIp})", publicIp, primaryPrivateIp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect network info");
        }
    }

    private async Task<string> GetPublicIpAsync(CancellationToken ct)
    {
        try
        {
            // Try multiple public IP services for reliability
            string[] services = [
                "https://api.ipify.org",
                "https://icanhazip.com",
                "https://checkip.amazonaws.com"
            ];

            foreach (var service in services)
            {
                try
                {
                    var response = await _httpClient.GetStringAsync(service, ct);
                    var ip = response.Trim();
                    if (!string.IsNullOrEmpty(ip) && System.Net.IPAddress.TryParse(ip, out _))
                        return ip;
                }
                catch { }
            }
        }
        catch { }
        return "";
    }

    // ────────────────────────────────────────────
    // Installed Application Scanner
    // Fixes Issue 2: scans actual installed apps from OS registry/db,
    // NOT from running processes. Collects metadata (version, publisher, etc.)
    // ────────────────────────────────────────────

    /// <summary>
    /// Re-runs the startup collects right after an EXPLICIT login, because a local
    /// account wipe may have run in this very process (remove-account CLI, GUI removal,
    /// or the server's {"action":"wipe_local"} response — 2026-10-19).
    ///
    /// Why a plain login is not enough: hardware and network collection dedup against
    /// BOTH the last row persisted in SQLite and the in-memory fingerprints captured
    /// earlier in this process, and the installed-software scan only runs at startup
    /// (inventory detection is event-driven — there is deliberately no periodic scan).
    /// A wipe deletes the rows but leaves those in-memory fingerprints intact, so the
    /// machine still "looks unchanged": every later collect takes the fast path, and
    /// the machine id minted for the re-login never receives a hardware snapshot, a
    /// network row or a software inventory — the web Device Specs page then shows
    /// "No hardware snapshot synced yet" / "No installed applications synced yet"
    /// forever for that device. Dropping the fingerprints here makes the dedup fall
    /// back to the (wiped, therefore empty) DB, which stores fresh rows stamped with
    /// the new machine identity.
    ///
    /// Safe on an ordinary login: the same DB-compare dedup prevents duplicate rows.
    /// </summary>
    public async Task RecollectAfterLoginAsync(CancellationToken ct = default)
    {
        try
        {
            _lastHardwareFingerprint = null;
            _lastNetworkPublicIp = null;
            _lastNetworkPrivateIp = null;

            await CollectDeviceHardwareAsync(ct);
            await CollectNetworkInfoAsync(ct);
            await RescanInventoryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-login recollection (hardware / network / software) failed");
        }
    }

    /// <summary>
    /// On-demand OS inventory rescan — used by the Installed Applications page's
    /// Rescan button AND the InstalledSoftwareWatcher background service. Re-runs
    /// the SAME joint app+package scan the periodic loop uses (deduped + classified
    /// via SoftwareClassifier) and stores the result into SQLite; the GUI then
    /// re-reads the tables. The OS scan stays here in the collector — the UI never
    /// scans the OS for display.
    /// </summary>
    public async Task RescanInventoryAsync(CancellationToken ct = default)
    {
        // Serialize with the background watcher: if an event-driven rescan is already
        // running, wait for it rather than stacking a second full OS walk.
        await _inventoryScanGate.WaitAsync(ct);
        try
        {
            await CollectInstalledApplicationsAsync(ct);
        }
        finally
        {
            _inventoryScanGate.Release();
        }
    }

    private async Task CollectInstalledApplicationsAsync(CancellationToken ct)
    {
        try
        {
            // Force recheck to pick up newly installed apps
            _appDetector.ForceRecheck();
            _packageDetector.ForceRecheck();

            var rawApps = _appDetector.GetAllInstalledApplications();
            var rawPackages = _packageDetector.GetAllInstalledPackages();

            // Joint classification: dedup app+pkg sources and route each software to exactly
            // one table. Fixes Firefox-snap appearing in installed_packages — the snap .desktop
            // (now discovered via $XDG_DATA_DIRS) wins as a browser application.
            var (apps, packages) = SoftwareClassifier.Classify(rawApps, rawPackages);

            if (apps.Count > 0)
                await _store.StoreInstalledApplicationsAsync(apps, ct);
            if (packages.Count > 0)
                await _store.StoreInstalledPackagesAsync(packages, ct);

            // Inventory lifecycle: rows are never deleted — ONE ROW PER INSTALL CYCLE. After
            // this completed scan, open cycles no longer found close (is_installed=0 +
            // uninstall_date = the uninstall event). A reinstall opens a NEW cycle row with a
            // fresh install_date in the Store* pass above, so install→uninstall→reinstall is
            // visible as separate records. The store skips a pass when its seen-set is empty
            // and applies a 50% confidence guard, so a failed/partial scan can't falsely
            // uninstall the whole inventory.
            await _store.ApplyInventoryLifecycleAsync(
                apps.Select(a => a.AppName).ToHashSet(StringComparer.OrdinalIgnoreCase),
                packages.Select(p => $"{p.PackageName}|{p.SourceManager}").ToHashSet(StringComparer.OrdinalIgnoreCase),
                DateTime.UtcNow,
                ct);

            _logger.LogDebug("Software inventory: {Apps} applications, {Packages} packages (pre-classify: {RawApps}/{RawPkgs})",
                apps.Count, packages.Count, rawApps.Count, rawPackages.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scan installed applications");
        }
    }

    // ────────────────────────────────────────────
    // Installed Package Scanner
    // ────────────────────────────────────────────

    // CollectInstalledApplicationsAsync now performs the joint app+package classification.
    // CollectInstalledPackagesAsync is retained for the periodic loop but delegates to the
    // joint collector so the classifier always sees both sources together.
    private Task CollectInstalledPackagesAsync(CancellationToken ct) => CollectInstalledApplicationsAsync(ct);

    // ────────────────────────────────────────────
    // Session Event Recording
    // ────────────────────────────────────────────

    public async Task RecordSessionEventAsync(string eventType, CancellationToken stoppingToken)
    {
        try
        {
            var last = await _store.GetLastSessionEventAsync(stoppingToken);
            if (last != null && last.EventType == eventType)
            {
                _logger.LogDebug("Skipping duplicate session event: {EventType} (last event is already {EventType})", eventType, eventType);
                return;
            }

            var evt = new SessionEvent
            {
                EventType = eventType,
                OsUsername = Environment.UserName,
                EventAt = DateTime.UtcNow
            };
            var token = stoppingToken == CancellationToken.None ? CancellationToken.None : stoppingToken;
            await _store.StoreSessionEventsAsync(new[] { evt }, token);
            _logger.LogDebug("Recorded session event: {EventType} for user {User}", eventType, Environment.UserName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record session event: {EventType}", eventType);
        }
    }

    // ────────────────────────────────────────────
    // Crash Recovery — Session Reconciliation
    // Fixes ended_at timestamps when PC is abruptly powered off.
    // Uses a persisted heartbeat timestamp + system uptime to detect
    // crashes and estimate the crash time.
    // ────────────────────────────────────────────

    /// <summary>
    /// Called once on startup before the main collection loop.
    /// Checks if the last heartbeat is stale (process/machine restarted)
    /// and closes any orphaned sessions with the last heartbeat time
    /// as the approximate crash time.
    ///
    /// Handles:
    /// - Power outage: uptime fresh, heartbeat stale → close with heartbeat time
    /// - Process crash (not reboot): uptime normal, heartbeat stale → close with heartbeat time
    /// - Fast restart (systemctl restart): heartbeat within grace period → preserve sessions
    /// - First-ever run: no heartbeat in DB → skip
    /// </summary>
    private async Task ReconcileStaleSessionsOnBootAsync(CancellationToken ct)
    {

        try
        {
            var heartbeatStr = await _store.GetStatusAsync("last_heartbeat_at", ct);
            if (string.IsNullOrEmpty(heartbeatStr))
            {
                _logger.LogDebug("No last_heartbeat_at found — first ever run, skipping reconciliation");
                return;
            }

            if (!DateTime.TryParse(heartbeatStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastHeartbeat))
            {
                _logger.LogWarning("Invalid last_heartbeat_at value '{Value}', skipping reconciliation", heartbeatStr);
                return;
            }

            var elapsed = DateTime.UtcNow - lastHeartbeat;

            // If heartbeat is still fresh (within grace period), no crash happened
            if (elapsed <= CrashGracePeriod)
            {
                _logger.LogDebug(
                    "Last heartbeat was {Elapsed:F1}s ago — within {GracePeriod}s grace period, skipping reconciliation",
                    elapsed.TotalSeconds, CrashGracePeriod.TotalSeconds);
                return;
            }

            // Heartbeat is stale — the process (and possibly the machine) restarted.
            // Use GetAllOpenSessionRecordsAsync (no process_id filter) to catch ALL
            // orphaned sessions, including those that never had a PID assigned.
            var openRecords = await _store.GetAllOpenSessionRecordsAsync(ct);
            if (openRecords.Count == 0)
            {
                _logger.LogDebug("No open sessions to reconcile after stale heartbeat");
                return;
            }

            var uptime = GetSystemUptime();
            var closeList = openRecords.Select(r => new AppSession
            {
                Id = r.AppSessionId,
                // ProcessName is unused by CloseSessionsAndAppItemsAsync — only EndedAt matters
                ProcessName = string.Empty,
                EndedAt = lastHeartbeat,
            }).ToList();

            // Atomic: close the sessions AND their app_items in one transaction
            await _store.CloseSessionsAndAppItemsAsync(closeList, lastHeartbeat, ct);

            _logger.LogWarning(
                "⚠️ Crash recovery: reconciled {Count} stale sessions + their app_items — " +
                "ended_at/closed_at set to {CrashTime}, heartbeat was {Elapsed:F0}s stale, system uptime={Uptime}",
                closeList.Count, lastHeartbeat.ToString("O"), elapsed.TotalSeconds, uptime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile stale sessions on boot");
        }
    }

    /// <summary>
    /// Get system uptime for all supported platforms.
    /// Used by ReconcileStaleSessionsOnBootAsync for diagnostic logging.
    /// </summary>
    private static string GetSystemUptime()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/uptime"))
            {
                var content = File.ReadAllText("/proc/uptime");
                var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && double.TryParse(parts[0],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var seconds))
                {
                    var ts = TimeSpan.FromSeconds(seconds);
                    return $"{ts.Days}d {ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
                }
            }

            if (OperatingSystem.IsWindows())
            {
                var ts = TimeSpan.FromMilliseconds(Environment.TickCount64);
                return $"{ts.Days}d {ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
            }

            if (OperatingSystem.IsMacOS())
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sysctl",
                    Arguments = "-n kern.boottime",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(2000);
                    // Output format: { sec = 1234567, usec = 123456 } 
                    var secMarker = "sec = ";
                    var secIdx = output.IndexOf(secMarker);
                    if (secIdx >= 0)
                    {
                        var afterSec = output[(secIdx + secMarker.Length)..];
                        var commaIdx = afterSec.IndexOf(',');
                        var secStr = commaIdx > 0 ? afterSec[..commaIdx] : afterSec;
                        secStr = secStr.Trim();
                        if (long.TryParse(secStr, out var bootSec))
                        {
                            var bootTime = DateTimeOffset.FromUnixTimeSeconds(bootSec);
                            var ts = DateTimeOffset.UtcNow - bootTime;
                            return $"{ts.Days}d {ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetSystemUptime error: {ex.Message}");
        }

        return "unknown";
    }

    /// <summary>
    /// Phase 3: Close garbage app_session rows created before the headless-subprocess
    /// filter was in place. These are rows where process_name contains --type= flags
    /// (Chromium/Electron helpers) or is abnormally long (corrupted cmdline data).
    /// Called once at startup. Closes them with ended_at = now rather than deleting,
    /// so any already-synced data remains consistent on the server.
    /// </summary>
    private async Task CleanupGarbageSessionRowsAsync(CancellationToken ct)
    {
        try
        {
            var openRecords = await _store.GetAllOpenSessionRecordsAsync(ct);
            var garbageSessions = new List<string>();
            var closeList = new List<AppSession>();

            foreach (var rec in openRecords)
            {
                if (rec.ProcessName.Contains("--type=", StringComparison.OrdinalIgnoreCase) ||
                    rec.ProcessName.Contains("--contentproc", StringComparison.OrdinalIgnoreCase) ||
                    rec.ProcessName.Length > 200)
                {
                    garbageSessions.Add(rec.AppSessionId);
                    closeList.Add(new AppSession
                    {
                        Id = rec.AppSessionId,
                        ProcessName = string.Empty,
                        EndedAt = DateTime.UtcNow,
                    });
                }
            }

            if (garbageSessions.Count > 0)
            {
                await _store.CloseSessionsAndAppItemsAsync(closeList, DateTime.UtcNow, ct);
                _logger.LogWarning(
                    "⚠️ Closed {Count} garbage sessions with --type= flags or long process names",
                    garbageSessions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean up garbage session rows");
        }
    }

    /// <summary>
    /// Phase 0a: Close sessions and remove installed_applications entries for non-GUI
    /// processes that were auto-registered before the GUI-only tracking gate existed.
    /// These include shell interpreters (sh), system tools (snap), and other CLI-only
    /// processes that were incorrectly registered into installed_applications by the old
    /// AutoDetectInstalledApp code (which put anything in /usr/bin/ into the DB).
    /// </summary>
    /// <summary>
    /// Windows boot sweep that removes the junk produced by the old runtime auto-registration
    /// path (which registered ANY process whose exe lived in Program Files/WindowsApps or whose
    /// display name could be resolved — svchost → "Windows Services", dllhost → "Windows DCOM
    /// Host", conhost → "Windows Console Host", Video.UI, WebPluginService, SDXHelper,
    /// SearchApp, M365Copilot…).
    ///
    /// Structural signal (no product names): real inventory rows ALWAYS carry a desktop_id
    /// (Start Menu shortcut name / registry Uninstall key / .desktop filename). Runtime
    /// auto-registered rows have an empty desktop_id, so they are safe to delete, and the
    /// sessions that reference them are closed (their process is a background system component
    /// that is not a user application).
    /// </summary>
    private async Task CleanupWindowsJunkSessionsAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var junkApps = await _store.GetInstalledAppsWithEmptyDesktopIdAsync(ct);
            var junkIds = junkApps.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Runtime-registered junk has no desktop_id; its BINARY names mark the sessions it
            // produced, so also close any open session whose process name matches one of them
            // (covers sessions whose installed_app_id was never linked).
            var junkBinaries = new HashSet<string>(
                junkApps.Select(a => a.BinaryName).Where(b => !string.IsNullOrWhiteSpace(b)),
                StringComparer.OrdinalIgnoreCase);

            var openRecords = await _store.GetAllOpenSessionRecordsAsync(ct);
            var closeSessions = new List<AppSession>();

            foreach (var rec in openRecords)
            {
                var processName = AppProcessClassifier.ExtractBaseProcessName(rec.ProcessName);

                // Sessions that reference a SURVIVING legit app row (desktop_id set, e.g.
                // inbox GUI apps like Notepad/Task Manager whose exe lives in C:\Windows)
                // are NEVER junk — the structural check below must not touch them.
                var hasLegitApp = rec.InstalledAppId != null && !junkIds.Contains(rec.InstalledAppId);

                // Linked to a deleted junk row, OR the process binary is a deleted junk binary,
                // OR (only for sessions with NO legit app link) the executable is structurally
                // not an app (system tree / console subsystem).
                var linkedToJunk = rec.InstalledAppId != null && junkIds.Contains(rec.InstalledAppId);
                var junkBinary = junkBinaries.Contains(processName);
                var structuralJunk = false;
                if (!hasLegitApp && !linkedToJunk && !junkBinary &&
                    !_browserRegistry.IsBrowser(processName))
                {
                    var path = GetCachedExecutablePath(processName);
                    if (!string.IsNullOrEmpty(path))
                    {
                        structuralJunk = ExecutableMetadata.IsWindowsSystemTree(path) ||
                                         ExecutableMetadata.GetSubsystem(path) == ExecutableMetadata.SubsystemWindowsCui;
                    }
                }

                if (linkedToJunk || junkBinary || structuralJunk)
                {
                    closeSessions.Add(new AppSession
                    {
                        Id = rec.AppSessionId,
                        ProcessName = string.Empty,
                        EndedAt = DateTime.UtcNow,
                    });
                }
            }

            if (closeSessions.Count > 0)
            {
                await _store.CloseSessionsAndAppItemsAsync(closeSessions, DateTime.UtcNow, ct);
            }

            foreach (var app in junkApps)
            {
                await _store.DeleteInstalledAppAsync(app.Id, ct);
            }

            if (junkApps.Count > 0 || closeSessions.Count > 0)
            {
                _logger.LogWarning(
                    "🧹 Windows junk sweep: removed {Apps} runtime-auto-registered app entries (empty desktop_id) and closed {Sessions} junk session(s)",
                    junkApps.Count, closeSessions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean Windows junk app entries");
        }
    }

    private async Task CleanupNonGuiAppEntriesAsync(CancellationToken ct)
    {
        try
        {
            // Non-GUI binary names that should never have been in installed_applications
            var nonGuiBinaryNames = new[] { "sh", "snap" };

            foreach (var binaryName in nonGuiBinaryNames)
            {
                // Find the installed_applications entry for this non-GUI binary
                var app = await _store.GetInstalledAppByBinaryNameAsync(binaryName, ct);
                if (app == null || app.IsBrowser || !string.IsNullOrWhiteSpace(app.Categories))
                    continue;

                _logger.LogWarning(
                    "⚠️ Phase 0a: Removing non-GUI entry '{AppName}' (binary={Binary}) from installed_applications and closing its open sessions",
                    app.AppName, binaryName);

                // Close all open sessions linked to this app
                var openRecords = await _store.GetAllOpenSessionRecordsAsync(ct);
                var closeSessions = new List<AppSession>();

                foreach (var rec in openRecords)
                {
                    if (string.Equals(rec.ProcessName, binaryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(rec.ProcessName, app.AppName, StringComparison.OrdinalIgnoreCase))
                    {
                        closeSessions.Add(new AppSession
                        {
                            Id = rec.AppSessionId,
                            ProcessName = string.Empty,
                            EndedAt = DateTime.UtcNow,
                        });
                    }
                }

                if (closeSessions.Count > 0)
                {
                    await _store.CloseSessionsAndAppItemsAsync(closeSessions, DateTime.UtcNow, ct);
                }

                // Delete the installed_applications entry
                await _store.DeleteInstalledAppAsync(app.Id, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean up non-GUI app entries");
        }
    }

    private async Task StorePermissionStatus(CancellationToken ct)
    {
        try
        {
            var sessionType = "unknown";
            if (OperatingSystem.IsLinux())
            {
                sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";
                var perms = new Dictionary<string, bool>(client.Platform.Linux.ProcessCollector.GetPermissionStatus());
                if (_config.LocationEnabled)
                {
                    foreach (var kv in LocationPermission.GetPermissionStatus(_httpClient))
                        perms[kv.Key] = kv.Value;
                }
                await _store.SetPermissionStatusAsync(perms, sessionType, ct);
            }
            else if (OperatingSystem.IsWindows())
            {
                sessionType = "windows";
                var perms = new Dictionary<string, bool>(client.Platform.Windows.ProcessCollector.GetPermissionStatus());
                if (_config.LocationEnabled)
                {
                    foreach (var kv in LocationPermission.GetPermissionStatus(_httpClient))
                        perms[kv.Key] = kv.Value;
                }
                await _store.SetPermissionStatusAsync(perms, sessionType, ct);
            }
            else if (OperatingSystem.IsMacOS())
            {
                sessionType = "macos";
                var perms = new Dictionary<string, bool>(client.Platform.MacOS.ProcessCollector.GetPermissionStatus());
                if (_config.LocationEnabled)
                {
                    foreach (var kv in LocationPermission.GetPermissionStatus(_httpClient))
                        perms[kv.Key] = kv.Value;
                }
                await _store.SetPermissionStatusAsync(perms, sessionType, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store permission status");
        }
    }
}
