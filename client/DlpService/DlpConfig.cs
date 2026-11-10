namespace AlphaAITracker.Service;

/// <summary>
/// Env-driven config for the SYSTEM DLP service (plan.md §10).
/// Defaults keep workers parked until ALPHA_DLP_ENABLED=true.
/// DeviceToken/ServerUrl can refresh from device.auth after a late tracker login.
/// </summary>
public sealed class DlpConfig
{
    public bool Enabled { get; init; }
    public string ServerUrl { get; private set; } = "";
    public string DeviceToken { get; private set; } = "";
    public string DataDir { get; init; } = "";
    public int PolicyPollSec { get; init; } = 300;
    public int AlertSyncSec { get; init; } = 60;
    /// <summary>
    /// Local kill-switches for each blocker (client .env -> service.env). When false the
    /// service never applies that blocker, even if the published server policy asks for
    /// it, and clears anything an earlier run left behind.
    /// </summary>
    public bool UsbBlock { get; init; } = true;
    /// <summary>Web Blocker domains (<c>/web-blocker</c>): hosts sink + firewall + DNS pin.</summary>
    public bool WebBlock { get; init; } = true;
    /// <summary>Network lockdown (<c>/allowlists/web</c>): default-deny outbound 80/443 firewall.</summary>
    public bool NetworkLockdown { get; init; } = true;
    /// <summary>App lockdown (<c>/allowlists/apps</c>): AppLocker default-deny + running-app kill sweep.</summary>
    public bool AppBlock { get; init; } = true;
    /// <summary>
    /// NTFS Deny Delete on Fixed/Removable volume roots (create/edit allowed).
    /// Same lockdown gate as app/network; false clears leftover ACEs.
    /// </summary>
    public bool DeleteBlock { get; init; } = true;
    /// <summary>
    /// After tracker login (credentials appear mid-run): pre-kill non-allowlisted apps,
    /// force-apply blockers, then reboot once. Off = no reboot (still polls policy).
    /// </summary>
    public bool LoginReboot { get; init; } = true;
    /// <summary>
    /// Parsed so an existing <c>ALPHA_DLP_MAX_LOCAL_ALERTS</c> still loads.
    /// Unsynced alerts are not deleted when this count is passed.
    /// </summary>
    public int MaxLocalAlerts { get; init; } = 50_000;
    public int DedupSec { get; init; } = 300;
    public string PipeName { get; init; } = "";
    /// <summary>Relaunch user-session tracker if Task Manager End Task kills it.</summary>
    public bool WatchdogEnabled { get; init; } = true;
    public int WatchdogPollSec { get; init; } = 8;

    /// <summary>Chrome-like silent updater (LocalSystem). Independent of <see cref="Enabled"/>.</summary>
    public bool UpdateEnabled { get; init; } = true;
    public int UpdateCheckIntervalSec { get; init; } = 60;
    public bool UpdateAutoInstall { get; init; } = true;

    public string DbPath => Path.Combine(DataDir, "dlp.db");
    public string PolicyDir => Path.Combine(DataDir, "policy");
    /// <summary>Sibling of dlp/: <c>%ProgramData%\&lt;mutex&gt;\updates</c>.</summary>
    public string UpdatesDir => UpdatePaths.RootDir;

    public static DlpConfig FromEnvironment()
    {
        var defaultDir = DlpDataPaths.DlpRoot;
        var defaultPipe = ServiceIdentity.DlpPipeName;

        var cfg = new DlpConfig
        {
            Enabled = GetBool("ALPHA_DLP_ENABLED", false),
            ServerUrl = (Environment.GetEnvironmentVariable("ALPHA_SERVER_URL") ?? "").TrimEnd('/'),
            DeviceToken = Environment.GetEnvironmentVariable("ALPHA_DEVICE_TOKEN") ?? "",
            DataDir = Environment.GetEnvironmentVariable("ALPHA_DLP_DATA_DIR") is { Length: > 0 } d
                ? d
                : defaultDir,
            PolicyPollSec = GetInt("ALPHA_DLP_POLICY_POLL_SEC", 300),
            AlertSyncSec = GetInt("ALPHA_DLP_ALERT_SYNC_SEC", 60),
            UsbBlock = GetBool("ALPHA_DLP_USB_BLOCK", true),
            WebBlock = GetBool("ALPHA_DLP_WEB_BLOCK", true),
            NetworkLockdown = GetBool("ALPHA_DLP_NETWORK_LOCKDOWN", true),
            AppBlock = GetBool("ALPHA_DLP_APP_BLOCK", true),
            DeleteBlock = GetBool("ALPHA_DLP_DELETE_BLOCK", true),
            LoginReboot = GetBool("ALPHA_DLP_LOGIN_REBOOT", true),
            MaxLocalAlerts = GetInt("ALPHA_DLP_MAX_LOCAL_ALERTS", 50_000),
            DedupSec = GetInt("ALPHA_DLP_DEDUP_SEC", 300),
            PipeName = Environment.GetEnvironmentVariable("ALPHA_DLP_PIPE_NAME") is { Length: > 0 } p
                ? p
                : defaultPipe,
            WatchdogEnabled = GetBool("ALPHA_DLP_WATCHDOG", true),
            WatchdogPollSec = GetInt("ALPHA_DLP_WATCHDOG_POLL_SEC", 8),
            // Mirror client defaults: on unless explicitly disabled.
            UpdateEnabled = GetBool("ALPHA_UPDATE_ENABLED", true),
            UpdateCheckIntervalSec = Math.Max(15, GetInt("ALPHA_UPDATE_CHECK_INTERVAL_SEC", 60)),
            UpdateAutoInstall = GetBool("ALPHA_UPDATE_AUTO_INSTALL", true),
        };
        cfg.RefreshCredentials();
        return cfg;
    }

    /// <summary>
    /// Re-read device.auth / env so workers can start after a post-boot tracker login.
    /// Never clears a non-empty value already held unless the file has a newer token.
    /// </summary>
    public void RefreshCredentials()
    {
        if (DeviceCredentialStore.TryLoad(out var fileUrl, out var fileTok))
        {
            if (!string.IsNullOrWhiteSpace(fileUrl))
                ServerUrl = fileUrl;
            if (!string.IsNullOrWhiteSpace(fileTok))
                DeviceToken = fileTok;
        }

        var envUrl = (Environment.GetEnvironmentVariable("ALPHA_SERVER_URL") ?? "").TrimEnd('/');
        var envTok = Environment.GetEnvironmentVariable("ALPHA_DEVICE_TOKEN") ?? "";
        if (string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(envUrl))
            ServerUrl = envUrl;
        if (string.IsNullOrWhiteSpace(DeviceToken) && !string.IsNullOrWhiteSpace(envTok))
            DeviceToken = envTok;
    }

    private static bool GetBool(string key, bool fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(v)) return fallback;
        return v is "1" or "true" or "TRUE" or "yes" or "YES";
    }

    private static int GetInt(string key, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return int.TryParse(v, out var n) && n > 0 ? n : fallback;
    }
}
