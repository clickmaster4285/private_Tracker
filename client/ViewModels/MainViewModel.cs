using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client.Configuration;
using System.Collections.ObjectModel;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;
using client.Services;
using client.Services.FaceVerify;

namespace client.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly HttpClient _httpClient;
    private readonly IInstalledAppDetector _appDetector;
    private readonly AutoStartService _autoStart;
    private readonly LogCollectorService _logCollector;
    private readonly SyncService _syncService;
    private readonly AccountRemovalService _accountRemoval;

    /// <summary>Self-update state — bound by the top-bar buttons and the dashboard banner.</summary>
    public AppUpdateService Update { get; }

    // ─── Branding (APP_IDENTIFIERS + VERSION) ───
    // Bound by every view instead of literals, so editing client/APP_IDENTIFIERS or
    // client/VERSION and rebuilding is all it takes to re-brand the whole GUI.

    public string AppDisplayName => AppInfo.DisplayName;
    public string AppTagline => AppInfo.Tagline;
    public string AppVersionDisplay => AppInfo.VersionDisplay;
    public string AppCopyright => AppInfo.Copyright;
    public string AppInitials => AppInfo.Initials;
    public string AppTitleWithVersion => AppInfo.TitleWithVersion;

    /// <summary>
    /// Environment pill on the splash/login screens. Derived from the configured
    /// server rather than hardcoded, so a dev build never claims to be production.
    /// </summary>
    public string EnvironmentLabel
    {
        get
        {
            var url = _config.ServerUrl;
            if (string.IsNullOrWhiteSpace(url)) return "NOT CONFIGURED";
            return url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || url.Contains("127.0.0.1", StringComparison.Ordinal)
                || url.Contains("::1", StringComparison.Ordinal)
                ? "ENVIRONMENT: DEVELOPMENT"
                : "ENVIRONMENT: PRODUCTION";
        }
    }

    // ─── Splash Screen State ───

    /// <summary>True while the splash/loading screen is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Step1Active))]
    [NotifyPropertyChangedFor(nameof(Step1Done))]
    [NotifyPropertyChangedFor(nameof(Step2Active))]
    [NotifyPropertyChangedFor(nameof(Step2Done))]
    [NotifyPropertyChangedFor(nameof(Step3Active))]
    [NotifyPropertyChangedFor(nameof(Step3Done))]
    private bool _isSplashVisible = true;

    /// <summary>Current splash step: 0 = checking auth, 1 = loading config, 2 = starting service, 3 = complete.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Step1Active))]
    [NotifyPropertyChangedFor(nameof(Step1Done))]
    [NotifyPropertyChangedFor(nameof(Step2Active))]
    [NotifyPropertyChangedFor(nameof(Step2Done))]
    [NotifyPropertyChangedFor(nameof(Step2Pending))]
    [NotifyPropertyChangedFor(nameof(Step3Active))]
    [NotifyPropertyChangedFor(nameof(Step3Done))]
    [NotifyPropertyChangedFor(nameof(Step3Pending))]
    private int _splashStep;

    /// <summary>Progress bar fill percentage (0–100).</summary>
    [ObservableProperty]
    private double _splashProgress;

    public bool Step1Active => IsSplashVisible && SplashStep == 0;
    public bool Step1Done => IsSplashVisible && SplashStep > 0;
    public bool Step2Active => IsSplashVisible && SplashStep == 1;
    public bool Step2Done => IsSplashVisible && SplashStep > 1;
    public bool Step2Pending => IsSplashVisible && SplashStep < 1;
    public bool Step3Active => IsSplashVisible && SplashStep == 2;
    public bool Step3Done => IsSplashVisible && SplashStep > 2;
    public bool Step3Pending => IsSplashVisible && SplashStep < 2;

    /// <summary>
    /// Plays the splash sequence (PDF page 1):
    /// checking authentication → loading configuration → starting monitoring service,
    /// with a smooth progress bar, then hands off to the login/main UI.
    /// </summary>
    public async Task RunSplashSequenceAsync(CancellationToken ct = default)
    {
        if (!IsSplashVisible) return;

        // Step 0: Checking authentication
        SplashStep = 0;
        await AnimateSplashProgressAsync(0, 25, 700, ct);
        await Task.Delay(400, ct);

        // Step 1: Loading configuration
        SplashStep = 1;
        await AnimateSplashProgressAsync(25, 55, 800, ct);
        await Task.Delay(400, ct);

        // Step 2: Starting monitoring service
        SplashStep = 2;
        await AnimateSplashProgressAsync(55, 90, 900, ct);
        await Task.Delay(500, ct);

        // Complete
        SplashStep = 3;
        await AnimateSplashProgressAsync(90, 100, 350, ct);
        await Task.Delay(250, ct);

        IsSplashVisible = false;
    }

    private async Task AnimateSplashProgressAsync(double from, double to, int durationMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            ct.ThrowIfCancellationRequested();
            var t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)durationMs);
            SplashProgress = from + (to - from) * EaseInOut(t);
            await Task.Delay(16, ct);
        }
        SplashProgress = to;
    }

    private static double EaseInOut(double t)
        => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    // ─── Auth State ───

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoStartStep))]
    [NotifyPropertyChangedFor(nameof(IsBackgroundStep))]
    [NotifyPropertyChangedFor(nameof(IsDependencyStep))]
    [NotifyPropertyChangedFor(nameof(IsPermissionStep))]
    [NotifyPropertyChangedFor(nameof(IsProfile))]
    [NotifyPropertyChangedFor(nameof(RequiresPermissionAction))]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string _employeeId = string.Empty;

    [ObservableProperty]
    private string _secretKey = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    // ─── Employee Info ───

    [ObservableProperty]
    private string _employeeName = string.Empty;

    [ObservableProperty]
    private string _employeeDepartment = string.Empty;

    [ObservableProperty]
    private string _employeeAvatar = string.Empty;

    [ObservableProperty]
    private string _employeeAvatarColor = string.Empty;

    // ─── Permission Step Flow ───

    public enum PermissionStep { None, AutoStart, BackgroundRunning, Dependencies, Location, OtherPermissions }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoStartStep))]
    [NotifyPropertyChangedFor(nameof(IsBackgroundStep))]
    [NotifyPropertyChangedFor(nameof(IsDependencyStep))]
    [NotifyPropertyChangedFor(nameof(IsLocationStep))]
    [NotifyPropertyChangedFor(nameof(IsPermissionStep))]
    [NotifyPropertyChangedFor(nameof(IsProfile))]
    [NotifyPropertyChangedFor(nameof(RequiresPermissionAction))]
    [NotifyPropertyChangedFor(nameof(StepTitle))]
    [NotifyPropertyChangedFor(nameof(StepDescription))]
    [NotifyPropertyChangedFor(nameof(StepButtonText))]
    [NotifyPropertyChangedFor(nameof(CurrentPermissionStepNumber))]
    [NotifyPropertyChangedFor(nameof(TotalPermissionSteps))]
    [NotifyPropertyChangedFor(nameof(SetupProgressPercent))]
    private PermissionStep _currentPermissionStep = PermissionStep.None;

    public int CurrentPermissionStepNumber
    {
        get
        {
            var i = 0;
            foreach (var step in ActivePermissionSteps())
            {
                i++;
                if (step == CurrentPermissionStep) return i;
            }
            return 0;
        }
    }

    public int TotalPermissionSteps => ActivePermissionSteps().Count();

    private IEnumerable<PermissionStep> ActivePermissionSteps()
    {
        yield return PermissionStep.AutoStart;
        yield return PermissionStep.BackgroundRunning;
        if (OperatingSystem.IsLinux()) yield return PermissionStep.Dependencies;
        if (_config.LocationEnabled) yield return PermissionStep.Location;
        yield return PermissionStep.OtherPermissions;
    }

    /// <summary>Wizard progress 0–100, for the stepper track on the setup page.</summary>
    public double SetupProgressPercent => TotalPermissionSteps == 0
        ? 0
        : Math.Clamp((CurrentPermissionStepNumber - 1) * 100.0 / TotalPermissionSteps, 0, 100);

    public bool IsAutoStartStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.AutoStart;
    public bool IsBackgroundStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.BackgroundRunning;
    public bool IsDependencyStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.Dependencies;
    public bool IsLocationStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.Location;
    public bool IsPermissionStep => IsLoggedIn && CurrentPermissionStep == PermissionStep.OtherPermissions;
    public bool IsProfile => IsLoggedIn && CurrentPermissionStep == PermissionStep.None;
    public bool RequiresPermissionAction => IsLoggedIn && CurrentPermissionStep != PermissionStep.None;

    public string StepTitle => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => "Enable Auto-Start",
        PermissionStep.BackgroundRunning => "Enable Background Guard",
        PermissionStep.Dependencies => "Install Required Dependencies",
        PermissionStep.Location => "Enable Location Access",
        PermissionStep.OtherPermissions => GetPlatformPermissionTitle(),
        _ => string.Empty
    };

    public string StepDescription => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => "Auto-start ensures tracking resumes automatically after a reboot or system restart.",
        PermissionStep.BackgroundRunning => "The background guard keeps the service alive even when the window is closed.",
        PermissionStep.Dependencies => "The following dependencies are missing and must be installed to grant permissions.",
        PermissionStep.Location => GetLocationPermissionDescription(),
        PermissionStep.OtherPermissions => GetPlatformPermissionDescription(),
        _ => string.Empty
    };

    public string StepButtonText => CurrentPermissionStep switch
    {
        PermissionStep.AutoStart => "Enable Auto-Start",
        PermissionStep.BackgroundRunning => "Enable Background Guard",
        PermissionStep.Location => "Open Location Settings",
        PermissionStep.OtherPermissions => GetPlatformPermissionButtonText(),
        _ => string.Empty
    };

    private string _stepStatusText = string.Empty;
    public string StepStatusText
    {
        get => _stepStatusText;
        set => SetProperty(ref _stepStatusText, value);
    }

    private string _missingDependenciesList = string.Empty;
    public string MissingDependenciesList
    {
        get => _missingDependenciesList;
        set => SetProperty(ref _missingDependenciesList, value);
    }

    private bool _isStepWorking;
    public bool IsStepWorking
    {
        get => _isStepWorking;
        set => SetProperty(ref _isStepWorking, value);
    }

    public MainViewModel(
        AppConfig config,
        ILogStore store,
        HttpClient httpClient,
        IInstalledAppDetector appDetector,
        AutoStartService autoStart,
        LogCollectorService logCollector,
        SyncService syncService,
        AccountRemovalService accountRemoval,
        AppUpdateService updateService,
        DashboardViewModel dashboard,
        SystemSpecsViewModel systemSpecs,
        InstalledAppsViewModel installedApps)
    {
        _config = config;
        _store = store;
        _httpClient = httpClient;
        _appDetector = appDetector;
        _autoStart = autoStart;
        _logCollector = logCollector;
        _syncService = syncService;
        _accountRemoval = accountRemoval;
        Update = updateService;
        Dashboard = dashboard;
        SystemSpecs = systemSpecs;
        InstalledApps = installedApps;
    }

    // ─── Post-setup navigation (pages 4–6) ───

    public enum AppPage { Dashboard, SystemSpecs, InstalledApps }

    /// <summary>Page 4 — Operational Overview.</summary>
    public DashboardViewModel Dashboard { get; }

    /// <summary>Page 5 — System Specifications.</summary>
    public SystemSpecsViewModel SystemSpecs { get; }

    /// <summary>Page 6 — Installed Applications.</summary>
    public InstalledAppsViewModel InstalledApps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboardPage))]
    [NotifyPropertyChangedFor(nameof(IsSystemSpecsPage))]
    [NotifyPropertyChangedFor(nameof(IsInstalledAppsPage))]
    [NotifyPropertyChangedFor(nameof(ActivePageTitle))]
    [NotifyPropertyChangedFor(nameof(ActivePageSubtitle))]
    private AppPage _activePage = AppPage.Dashboard;

    public bool IsDashboardPage => ActivePage == AppPage.Dashboard;
    public bool IsSystemSpecsPage => ActivePage == AppPage.SystemSpecs;
    public bool IsInstalledAppsPage => ActivePage == AppPage.InstalledApps;

    public string ActivePageTitle => ActivePage switch
    {
        AppPage.SystemSpecs => "System Specifications",
        AppPage.InstalledApps => "Installed Applications",
        _ => "Operational Overview"
    };

    public string ActivePageSubtitle => ActivePage switch
    {
        AppPage.SystemSpecs => "Hardware, network identity and attached peripherals for this machine",
        AppPage.InstalledApps => "Software inventory reported to the compliance server",
        _ => "Live monitoring status and device summary"
    };

    /// <summary>
    /// Switches page and lazily refreshes its data. Each page VM guards re-entry with
    /// its own IsBusy flag, so rapid nav clicks can't stack overlapping scans.
    /// </summary>
    [RelayCommand]
    private async Task NavigateAsync(string page)
    {
        ActivePage = page switch
        {
            "specs" => AppPage.SystemSpecs,
            "apps" => AppPage.InstalledApps,
            _ => AppPage.Dashboard
        };

        await RefreshActivePageAsync();
    }

    /// <summary>Refreshes only the page currently on screen.</summary>
    [RelayCommand]
    private async Task RefreshActivePageAsync()
    {
        switch (ActivePage)
        {
            case AppPage.SystemSpecs:
                await SystemSpecs.RefreshAsync();
                break;
            case AppPage.InstalledApps:
                await InstalledApps.RefreshAsync();
                break;
            default:
                await Dashboard.RefreshAsync();
                break;
        }
    }

    /// <summary>
    /// Loads the landing page once the wizard is done. Called at every point where the
    /// user can arrive at the shell (startup, login, last permission granted) rather
    /// than from a property-changed hook, so the load is never fired mid-wizard.
    /// </summary>
    private async Task EnterShellAsync()
    {
        if (!IsProfile) return;
        ActivePage = AppPage.Dashboard;
        await Dashboard.RefreshAsync();

        // Quiet self-update check: only runs when the configured interval has elapsed
        // (persisted in app_status), never blocks the UI, and auto-installs when a
        // newer version exists and ALPHA_UPDATE_AUTO_INSTALL is on.
        _ = Update.RunAutoCheckIfDueAsync(CancellationToken.None);
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var info = await _store.GetEmployeeInfoAsync(ct);
        if (info != null)
        {
            IsLoggedIn = true;
            EmployeeName = info.Name;
            EmployeeDepartment = info.Department;
            EmployeeAvatar = info.Avatar ?? string.Empty;
            EmployeeAvatarColor = info.AvatarColor ?? "#8B5CF6";

            _logCollector.SetEmployeeInfo(info.EmployeeId, info.Name, info.Token ?? string.Empty);
            _logCollector.StartTracking();
            FaceVerifyLauncher.TryLaunchOnSessionStart(_config, info, trigger: "login");

            // Session restored — wake sync after login jitter so reboot storms of 1000
            // agents do not align on the same second.
            _ = Task.Run(async () =>
            {
                await _syncService.RequestImmediateSyncWithLoginJitterAsync(CancellationToken.None);
            });

            // Reset stale permission statuses so they get freshly re-evaluated
            // (GetNextPermissionStep() no longer reads stored statuses, but this
            //  ensures the stored values match reality for any other consumers)
            await _store.SetStatusAsync("perm_auto_start", "false", ct);
            await _store.SetStatusAsync("perm_background", "false", ct);
            await _store.SetStatusAsync("perm_other", "false", ct);

            // Evaluate which permissions are actually configured vs missing.
            // Does NOT trust any previous stored status.
            CurrentPermissionStep = await GetNextPermissionStep();

            // Forced auto-start — always ensure it's configured
            _autoStart.EnableAutoStartForced();

            await EnterShellAsync();
        }
        else
        {
            IsLoggedIn = false;
            CurrentPermissionStep = PermissionStep.None;
        }
    }

    /// <summary>
    /// Completely remove the login account from this machine and return the tool to
    /// the sign-in page.
    ///
    /// Invoked by the <c>client --remove-account</c> CLI command (Linux + Windows) —
    /// either directly on a cold start, or via the REMOVE_ACCOUNT single-instance
    /// signal when the tracker is already running (the web "Remove Current Device"
    /// modal shows the exact command for both platforms). The web already revoked the
    /// machine's device registration server-side; this half removes the LOCAL account
    /// so the machine is handed over with no trace of the previous employee.
    ///
    /// Order matters: collecting stops BEFORE the wipe, otherwise the collector's next
    /// cycle would write a session straight back into the freshly emptied tables.
    ///
    /// The actual sequence (stop → bounded drain → POST /devices/self-remove → wipe) is
    /// owned by <see cref="AccountRemovalService"/> so every entry point behaves the same
    /// (R4/R5, 2026-10-19). This method only resets the view-model state.
    /// </summary>
    public async Task RemoveAccountAsync(CancellationToken ct = default)
    {
        // 1-2. Stop collecting, drain pending rows to the server, notify the server this
        //      machine is going away, then wipe the local DB + machine/device identifiers.
        await _accountRemoval.RunAsync(ct);

        // 3. Land on the sign-in page. IsLoggedIn=false flips MainWindow from the
        //    shell / permission wizard to LoginPage.
        //    MainWindow.axaml binds these flags directly.
        IsLoggedIn = false;
        CurrentPermissionStep = PermissionStep.None;
        IsSplashVisible = false;
        EmployeeName = string.Empty;
        EmployeeDepartment = string.Empty;
        EmployeeAvatar = string.Empty;
        EmployeeAvatarColor = string.Empty;
        EmployeeId = string.Empty;
        SecretKey = string.Empty;
        StatusMessage = string.Empty;
        IsLoading = false;
        IsStepWorking = false;
        StepStatusText = string.Empty;
        MissingDependenciesList = string.Empty;
    }

    /// <summary>
    /// Check each permission condition and return the first incomplete step.
    /// Always re-validates the ACTUAL condition — never trusts stored status alone.
    /// If a permission was previously granted but is now revoked (e.g. user removed
    /// auto-start or systemd service was deleted), it will show up again.
    /// </summary>
    private async Task<PermissionStep> GetNextPermissionStep()
    {
        // Check auto-start (Step 1)
        var autoOk = _autoStart.IsAutoStartEnabled();
        if (!autoOk)
            return PermissionStep.AutoStart;
        await _store.SetStatusAsync("perm_auto_start", "true", CancellationToken.None);

        // Check background guard (Step 2)
        var bgOk = IsBackgroundGuardConfigured();
        if (!bgOk)
            return PermissionStep.BackgroundRunning;
        await _store.SetStatusAsync("perm_background", "true", CancellationToken.None);

        if (OperatingSystem.IsLinux())
        {
            var missingDeps = GetMissingLinuxDependencies();
            if (missingDeps.Count > 0)
            {
                MissingDependenciesList = string.Join(", ", missingDeps);
                return PermissionStep.Dependencies;
            }
        }

        if (_config.LocationEnabled)
        {
            var locOk = await LocationPermission.CheckGrantedAsync(_httpClient, CancellationToken.None);
            if (!locOk)
                return PermissionStep.Location;
            await _store.SetStatusAsync("perm_location", "true", CancellationToken.None);
        }

        // Check other permissions (final step)
        var otherOk = !HasMissingPermissions();
        if (!otherOk)
            return PermissionStep.OtherPermissions;
        await _store.SetStatusAsync("perm_other", "true", CancellationToken.None);

        // All permissions are granted
        return PermissionStep.None;
    }

    private static bool IsBackgroundGuardConfigured()
    {
        if (OperatingSystem.IsLinux())
        {
            return File.Exists(LinuxUserSystemdUnit.ServicePath);
        }
        if (OperatingSystem.IsWindows())
        {
            return Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("AlphaAITracker") != null;
        }
        if (OperatingSystem.IsMacOS())
        {
            // launchd KeepAlive plist = the macOS background guard; EnableAutoStart writes
            // it, so the gate mirrors that check instead of returning false unconditionally
            // (which would strand macOS on step 2 exactly like the missing Linux unit).
            return File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "LaunchAgents", "com.alphaai.tracker.plist"));
        }
        return false;
    }

    private List<string> GetMissingLinuxDependencies()
    {
        var missing = new List<string>();
        if (!IsCommandAvailable("pkexec")) missing.Add("policykit-1");
        if (!IsCommandAvailable("loginctl")) missing.Add("systemd");
        if (!IsCommandAvailable("gsettings")) missing.Add("libglib2.0-bin");
        if (!IsCommandAvailable("setcap")) missing.Add("libcap2-bin");
        if (_config.LocationEnabled && !IsLinuxPackageInstalled("geoclue-2.0"))
            missing.Add("geoclue-2.0");
        return missing;
    }

    private static bool IsLinuxPackageInstalled(string package)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dpkg",
                Arguments = $"-s {package}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sh",
                Arguments = $"-c \"command -v {command}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(2000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Check for any missing OS-level permissions.
    /// Combines detection from:
    /// - InstalledAppDetector (dpkg-query, registry access, etc.)
    /// - Direct OS permission checks (/proc access on Linux, accessibility on macOS, etc.)
    /// </summary>
    private bool HasMissingPermissions()
    {
        // 1. Check InstalledAppDetector for known permission gaps
        var missing = _appDetector.MissingPermissions;
        if (missing.Count > 0) return true;

        // 2. Check if shell collectors can access their history files (Optional)
        // We do not block on this since missing history files (e.g. no WSL installed) is normal.

        // 3. Platform-specific checks
        if (OperatingSystem.IsLinux())
        {
            return CheckLinuxPermissions();
        }
        if (OperatingSystem.IsWindows())
        {
            return CheckWindowsPermissions();
        }
        if (OperatingSystem.IsMacOS())
        {
            return CheckMacOSPermissions();
        }

        return false;
    }

    /// <summary>
    /// Linux: Check Wayland accessibility and other OS-level permissions.
    /// Under Wayland, window title tracking requires toolkit accessibility enabled.
    /// </summary>
    private static bool CheckLinuxPermissions()
    {
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (string.IsNullOrEmpty(sessionType))
        {
            // Can't determine session type — check via logind
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "loginctl",
                    Arguments = "show-session $(loginctl | grep $(whoami) | awk '{print $1}') -p Type | cut -d= -f2",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(2000);
                    if (!string.IsNullOrEmpty(output))
                        sessionType = output;
                }
            }
            catch { }
        }

        // Wayland: check toolkit accessibility (needed for window titles)
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "gsettings",
                    Arguments = "get org.gnome.desktop.interface toolkit-accessibility",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd()?.Trim();
                    proc.WaitForExit(2000);
                    if (!string.Equals(output, "true", StringComparison.OrdinalIgnoreCase))
                        return true; // Wayland accessibility NOT enabled
                }
            }
            catch
            {
                // gsettings unavailable — assume no missing perms
            }
        }

        return false;
    }

    /// <summary>
    /// Windows: Check if we can enumerate processes properly.
    /// </summary>
    private static bool CheckWindowsPermissions()
    {
        try
        {
            // Try to open a system process handle (requires PROCESS_QUERY_INFORMATION)
            // If this fails, we have restricted permissions
            var processes = System.Diagnostics.Process.GetProcesses();
            return false; // We can see processes → OK
        }
        catch
        {
            return true; // Need admin/UAC elevation
        }
    }

    /// <summary>
    /// macOS: Check accessibility and full disk access permissions.
    /// </summary>
    private static bool CheckMacOSPermissions()
    {
        try
        {
            // Try to use `osascript` to get frontmost app (requires Accessibility)
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                Arguments = "-e 'tell application \"System Events\" to get name of first application process whose frontmost is true'",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return true;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return true; // Accessibility not granted
            return false; // Can interact with System Events → OK
        }
        catch
        {
            return true; // osascript not available → missing perms
        }
    }

    /// <summary>
    /// Build a detailed message about what permissions are still missing.
    /// This gives the user actionable feedback instead of a generic retry message.
    /// </summary>
    private string GetMissingPermissionsMessage()
    {
        var issues = new List<string>();

        // Check InstalledAppDetector missing permissions
        var detectorMissing = _appDetector.MissingPermissions;
        if (detectorMissing.Count > 0)
        {
            var grantInstructions = _appDetector.PermissionGrantInstructions;
            issues.AddRange(grantInstructions);
        }

        // Check platform-specific issues
        if (OperatingSystem.IsLinux())
        {
            var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";
            if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("Wayland toolkit accessibility may still be disabled. The pkexec script attempted to enable it. " +
                           "If this persists, manually run in a terminal:\n" +
                           "   gsettings set org.gnome.desktop.interface toolkit-accessibility true\n" +
                           "Or enable it in GNOME Settings → Accessibility → 'Toolkit Accessibility'.");
            }
        }

        if (issues.Count == 0)
        {
            return "Permissions still missing. Please try again or check system settings.";
        }

        return "Permissions still missing:\n" + string.Join("\n", issues.Select(i => "• " + i));
    }

    // ─── Auth Commands ───

    [RelayCommand]
    private void CancelLogin()
    {
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(EmployeeId) || string.IsNullOrWhiteSpace(SecretKey))
        {
            StatusMessage = "Please enter both Employee ID and Secret Key.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Authenticating...";

        try
        {
            // Fresh local database on login (2026-10-19): a login must never inherit
            // another account's local history. A removal wipe normally leaves the DB
            // empty already, but if rows belonging to a DIFFERENT employee are still on
            // disk (a removal that never ran — machine handed over by hand, interrupted
            // wipe — clear them BEFORE the login request). Order matters: the wipe
            // rotates the machine id, so the server registers this login under the very
            // identity the freshly created local rows will be stamped with — otherwise
            // the device registry and the row stamping disagree and Device Specs shows
            // an empty device.
            var prior = await _store.GetEmployeeInfoAsync(CancellationToken.None);
            if (prior != null && !string.IsNullOrWhiteSpace(prior.EmployeeId) &&
                !string.Equals(prior.EmployeeId, EmployeeId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await _store.WipeLocalAccountDataAsync(CancellationToken.None);
                _config.ResetMachineId();
                EncryptedConfigService.ResetCache();
            }

            var serverUrl = _config.ServerUrl ?? "http://localhost:8080";
            // Include machineId/platform/clientVersion so the server binds the device
            // record to THIS specific machine (its persisted .machine-id), not the
            // "default-<employeeId>" fallback. Without it, every login rotates the
            // device record under the same synthetic key, and any other live client
            // (or a manual curl re-login) can revoke the token this client just got
            // back — the next sync then 401s with a token whose server row is gone.
            var payload = new
            {
                employeeId = EmployeeId.Trim(),
                secretKey = SecretKey.Trim(),
                machineId = _config.ClientId,
                platform = RuntimeInformation.OSDescription,
                clientVersion = AppInfo.Version,
                // Hostname is the human label the web Device Specs dropdown shows as
                // "{employee name} ({hostname})" — without it a fresh device reads as unnamed.
                deviceName = System.Environment.MachineName,
            };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{serverUrl}/api/v1/auth/employee-login", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    var errObj = JsonSerializer.Deserialize<ErrorResponse>(responseBody);
                    StatusMessage = errObj?.Message ?? "Login failed. Check your credentials.";
                }
                catch
                {
                    StatusMessage = $"Login failed ({(int)response.StatusCode}). Check your credentials.";
                }
                return;
            }

            var loginResp = JsonSerializer.Deserialize<EmployeeLoginResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (loginResp?.Employee == null)
            {
                StatusMessage = "Invalid response from server.";
                return;
            }

            var emp = loginResp.Employee;

            var employeeInfo = new EmployeeInfo
            {
                Id = emp.Id,
                EmployeeId = emp.EmployeeId,
                Name = emp.Name,
                Email = emp.Email,
                Role = emp.Role,
                Department = emp.Department,
                Shift = emp.Shift,
                Avatar = emp.Avatar,
                AvatarColor = emp.AvatarColor,
                Token = loginResp.Token,
                DeviceToken = loginResp.DeviceToken,
            };

            await _store.SaveEmployeeInfoAsync(employeeInfo, CancellationToken.None);

            _logCollector.SetEmployeeInfo(emp.EmployeeId, emp.Name, loginResp.Token ?? string.Empty);
            _logCollector.StartTracking();
            FaceVerifyLauncher.TryLaunchOnSessionStart(_config, employeeInfo, trigger: "login");

            // Login succeeded — drain after a random jitter so 1000 agents do not storm
            // the API in the same second (ALPHA_SYNC_LOGIN_JITTER_SEC).
            _ = Task.Run(async () =>
            {
                await _syncService.RequestImmediateSyncWithLoginJitterAsync(CancellationToken.None);
            });

            // Hardware / network / software were last collected at process start — possibly
            // BEFORE this machine's local wipe — and their in-memory dedup fingerprints
            // survived that wipe, so the periodic collects would skip forever and this
            // login's machine id would never get a snapshot. Re-collect for the identity
            // this login just established, then wake the sync engine again so those rows
            // are pushed immediately. Fire-and-forget: the OS probes (GPU via PowerShell,
            // package-manager walks) must not block the UI thread, and
            // RecollectAfterLoginAsync logs its own failures.
            _ = Task.Run(async () =>
            {
                await _logCollector.RecollectAfterLoginAsync(CancellationToken.None);
                await _syncService.RequestImmediateSyncWithLoginJitterAsync(CancellationToken.None);
            });

            IsLoggedIn = true;
            EmployeeName = emp.Name;
            EmployeeDepartment = emp.Department;
            EmployeeAvatar = emp.Avatar ?? string.Empty;
            EmployeeAvatarColor = emp.AvatarColor ?? "#8B5CF6";
            StatusMessage = string.Empty;

            // Advance through permission steps
            CurrentPermissionStep = await GetNextPermissionStep();

            // Scan for browsers and enable auto-start after GUI login too
            _autoStart.EnableAutoStartForced();

            await EnterShellAsync();
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Connection error: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusMessage = "Request timed out. Check your connection.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ─── Permission Step Execution ───

    [RelayCommand]
    private async Task GrantCurrentStepPermissionAsync()
    {
        IsStepWorking = true;
        StepStatusText = "Working...";

        try
        {
            switch (CurrentPermissionStep)
            {
                case PermissionStep.AutoStart:
                    await GrantAutoStartAsync();
                    break;
                case PermissionStep.BackgroundRunning:
                    await GrantBackgroundRunningAsync();
                    break;
                case PermissionStep.Dependencies:
                    await InstallDependenciesAsync();
                    break;
                case PermissionStep.Location:
                    await GrantLocationPermissionAsync();
                    break;
                case PermissionStep.OtherPermissions:
                    await GrantOtherPermissionsAsync();
                    break;
            }

            // Re-evaluate progress
            CurrentPermissionStep = await GetNextPermissionStep();
            if (IsProfile)
            {
                StepStatusText = string.Empty;
                await EnterShellAsync();
            }
        }
        catch (Exception ex)
        {
            StepStatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsStepWorking = false;
        }
    }

    private Task GrantAutoStartAsync()
    {
        var ok = _autoStart.EnableAutoStartForced();
        if (!ok) throw new InvalidOperationException("Failed to enable auto-start. Try running as administrator.");
        StepStatusText = "Auto-start enabled successfully.";
        return Task.CompletedTask;
    }

    private async Task GrantBackgroundRunningAsync()
    {
        _autoStart.EnableAutoStartForced();

        // Step 2's gate (IsBackgroundGuardConfigured) checks for the systemd USER unit
        // on Linux, but nothing used to create it: EnableAutoStartForced only wrote the
        // .desktop autostart entry and ran `systemctl --user enable` against a unit file
        // that did not exist yet. The step therefore re-evaluated to ITSELF after showing
        // its success message — "Background guard enabled successfully." and no way to
        // reach step 3 (stuck on SETUP STEP 2 OF 4, 2026-10-19). Install the unit here.
        if (!await _autoStart.EnsureBackgroundGuardAsync())
            throw new InvalidOperationException(
                "Failed to install the background guard service (systemd user unit). Check that systemd is available and try again.");

        StepStatusText = "Background guard enabled successfully.";
    }

    private async Task InstallDependenciesAsync()
    {
        var missingDeps = GetMissingLinuxDependencies();
        if (missingDeps.Count == 0) return;

        var deps = string.Join(" ", missingDeps);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pkexec",
            Arguments = $"apt-get install -y {deps}",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        StepStatusText = "PolKit password dialog opened. Enter your password to install dependencies.";
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc != null)
        {
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0)
                StepStatusText = "Dependencies installed successfully.";
            else
                StepStatusText = "Failed to install dependencies.";
        }
    }

    [RelayCommand]
    private void ShowManualInstall()
    {
        var missingDeps = GetMissingLinuxDependencies();
        if (missingDeps.Count > 0)
        {
            var deps = string.Join(" ", missingDeps);
            StepStatusText = $"sudo apt-get install -y {deps}";
        }
    }

    private async Task GrantOtherPermissionsAsync()
    {
        var exited = false;

        if (OperatingSystem.IsLinux())
        {
            exited = await GrantLinuxPermissionsAsync();
        }
        else if (OperatingSystem.IsWindows())
        {
            GrantWindowsPermissions();
            exited = true;
        }
        else if (OperatingSystem.IsMacOS())
        {
            ShowMacOSPermissionInstructions();
            exited = true;
        }

        // Wait for the process to finish, then re-check
        if (exited)
        {
            await Task.Delay(2000); // brief settle time
        }

        _appDetector.ForceRecheck();

        if (HasMissingPermissions())
        {
            StepStatusText = GetMissingPermissionsMessage();
        }
        else
        {
            StepStatusText = "All permissions granted.";
        }
    }

    private async Task<bool> GrantLinuxPermissionsAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var currentUser = Environment.UserName;

        // Determine if we're running via `dotnet run` (ProcessPath == dotnet host)
        var processPath = Environment.ProcessPath ?? string.Empty;
        var isDotnetRun = processPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
                          processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

        // Detect session type to decide which permissions to grant
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "x11";

        // Write temp script so pkexec arguments are clean
        var tmpScript = Path.Combine(Path.GetTempPath(), "alpha_perm_" + Guid.NewGuid().ToString("N") + ".sh");
        try
        {
            var scriptBuilder = new System.Text.StringBuilder();
            scriptBuilder.Append("#!/bin/sh\n");

            // 1. Make shell history files readable (chmod +r as root ensures it works)
            scriptBuilder.AppendLine($"chmod +r \"{home}/.bash_history\" 2>/dev/null || true");
            scriptBuilder.AppendLine($"chmod +r \"{home}/.zsh_history\" 2>/dev/null || true");
            scriptBuilder.AppendLine($"chmod +r \"{home}/.local/share/fish/fish_history\" 2>/dev/null || true");

            // 2. Enable Wayland toolkit accessibility (needed for window title tracking)
            //    Must run as $USER because gsettings/dconf are per-user settings, not root
            if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
            {
                scriptBuilder.AppendLine($"sudo -u \"{currentUser}\" gsettings set org.gnome.desktop.interface toolkit-accessibility true 2>/dev/null || true");
                scriptBuilder.AppendLine($"sudo -u \"{currentUser}\" dconf write /org/gnome/desktop/interface/toolkit-accessibility true 2>/dev/null || true");
            }

            // 3. Grant capability for process detection (DAC_READ_SEARCH)
            //    Only available for published/installed binaries — dotnet run skips this
            if (!isDotnetRun)
            {
                scriptBuilder.AppendLine($"setcap CAP_DAC_READ_SEARCH+ep \"{processPath}\" 2>/dev/null || true");
            }

            var script = scriptBuilder.ToString();
            await File.WriteAllTextAsync(tmpScript, script);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pkexec",
                Arguments = $"bash {tmpScript}",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            StepStatusText = "PolKit password dialog opened. Enter your password to grant permissions.";
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                StepStatusText = "Failed to launch PolKit. Is pkexec installed?";
                return false;
            }

            // Wait up to 2 minutes for user to complete the dialog
            var exited = proc.WaitForExit(120_000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                StepStatusText = "PolKit timed out. Please try again.";
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            StepStatusText = $"Error: {ex.Message}";
            return false;
        }
        finally
        {
            try { if (File.Exists(tmpScript)) File.Delete(tmpScript); } catch { }
        }
    }

    private async Task GrantLocationPermissionAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:privacy-location",
                    UseShellExecute = true,
                });
                StepStatusText = "Enable location for this device in Windows Settings, then return here.";
            }
            else if (OperatingSystem.IsLinux())
            {
                // GeoClue2 + polkit: open GNOME privacy panel when available
                var opened = false;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = "gnome-control-center privacy",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    opened = true;
                }
                catch { /* fall through to text instructions */ }

                StepStatusText = opened
                    ? "Enable Location Services in Settings → Privacy → Location. GeoClue2 (geoclue-2.0) must be installed."
                    : "Install geoclue-2.0 and enable Location Services in your desktop privacy settings.";
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_LocationServices");
                StepStatusText = "Enable Location Services for Alpha AI Tracker in System Settings → Privacy.";
            }

            await Task.Delay(3000);
            var granted = await LocationPermission.CheckGrantedAsync(_httpClient, CancellationToken.None);
            StepStatusText = granted
                ? "Location access confirmed (GPS, WiFi, or IP fallback)."
                : "Location not detected yet. Grant OS permission or ensure network is available for IP fallback.";
        }
        catch (Exception ex)
        {
            StepStatusText = $"Location check failed: {ex.Message}";
        }
    }

    private static string GetLocationPermissionDescription()
    {
        if (OperatingSystem.IsWindows())
            return "Allow Windows location access so the tracker can record GPS or network-based fixes for the admin dashboard.";
        if (OperatingSystem.IsLinux())
            return "Install GeoClue2 (geoclue-2.0) and grant location permission via your desktop privacy settings. Without GPS, IP-based location is used.";
        if (OperatingSystem.IsMacOS())
            return "Enable Location Services for this app in System Settings → Privacy & Security.";
        return "Grant location permission so the tracker can report position to the server.";
    }

    private void GrantWindowsPermissions()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo Granting Alpha AI Tracker permissions... && whoami",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        System.Diagnostics.Process.Start(psi);
    }

    private void ShowMacOSPermissionInstructions()
    {
        StepStatusText = """
            Open System Settings → Privacy & Security:
            1. Accessibility → Add Alpha AI Tracker → Enable
            2. Full Disk Access → Add Alpha AI Tracker → Enable
            3. Screen Recording → Add Alpha AI Tracker → Enable (optional)
            """;
    }

    // ─── Platform Helpers ───

    private static string GetPlatformPermissionTitle()
    {
        if (OperatingSystem.IsLinux()) return "Grant Linux Permissions";
        if (OperatingSystem.IsWindows()) return "Grant Windows Permissions";
        if (OperatingSystem.IsMacOS()) return "Grant macOS Permissions";
        return "Grant Permissions";
    }

    private static string GetPlatformPermissionDescription()
    {
        if (OperatingSystem.IsLinux())
            return "Grant process detection and shell history access via administrator privileges.";
        if (OperatingSystem.IsWindows())
            return "Grant administrator permissions for full monitoring access.";
        if (OperatingSystem.IsMacOS())
            return "Enable Accessibility, Full Disk Access, and Screen Recording in System Settings.";
        return "Grant required permissions for monitoring.";
    }

    private static string GetPlatformPermissionButtonText()
    {
        if (OperatingSystem.IsLinux()) return "Grant via PolKit";
        if (OperatingSystem.IsWindows()) return "Grant via UAC";
        if (OperatingSystem.IsMacOS()) return "Show Instructions";
        return "Grant Permissions";
    }
}

// ────────────────────────────────
// DTOs for API responses
// ────────────────────────────────

public class EmployeeLoginResponse
{
    [JsonPropertyName("employee")]
    public EmployeeData? Employee { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("deviceToken")]
    public string? DeviceToken { get; set; }
}

public class EmployeeData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("employeeId")]
    public string EmployeeId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("department")]
    public string Department { get; set; } = string.Empty;

    [JsonPropertyName("shift")]
    public string Shift { get; set; } = string.Empty;

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    [JsonPropertyName("avatarColor")]
    public string? AvatarColor { get; set; }
}

public class ErrorResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}