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
    public int PolicyPollSec { get; private set; } = 300;
    public int AlertSyncSec { get; private set; } = 60;
    /// <summary>
    /// Local kill-switches for each blocker (client .env -> service.env). When false the
    /// service never applies that blocker, even if the published server policy asks for
    /// it, and clears anything an earlier run left behind.
    /// </summary>
    public bool UsbBlock { get; private set; } = true;
    /// <summary>Web Blocker domains (<c>/web-blocker</c>): hosts sink + firewall + DNS pin.</summary>
    public bool WebBlock { get; private set; } = true;
    /// <summary>Network lockdown (<c>/allowlists/web</c>): default-deny outbound 80/443 firewall.</summary>
    public bool NetworkLockdown { get; private set; } = true;
    /// <summary>App lockdown (<c>/allowlists/apps</c>): AppLocker default-deny + running-app kill sweep.</summary>
    public bool AppBlock { get; private set; } = true;
    /// <summary>Hardware/port control (<c>/device-specs/ports</c>): disable blocked device classes/instances.</summary>
    public bool PortBlock { get; private set; } = true;
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

    /// <summary>
    /// 24/7 antivirus guard (<see cref="AntivirusGuardWorker"/>): keeps Microsoft Defender
    /// real-time scanning ON at maximum strength, reverts tampering, reports detections.
    /// Independent of <see cref="Enabled"/> and of any web policy — on whenever installed.
    /// </summary>
    public bool AvEnforce { get; init; } = true;
    public int AvGuardSec { get; init; } = 30;
    public int AvStatusSec { get; init; } = 300;
    /// <summary>Run a Defender quick scan when none completed in this many hours (0 = off).</summary>
    public int AvQuickScanHours { get; init; } = 24;
    /// <summary>Run a Defender full scan when none completed in this many days (0 = off).</summary>
    public int AvFullScanDays { get; init; } = 7;
    /// <summary>Defender exclusions IT approved (<c>;</c>-separated). Every other exclusion is removed.</summary>
    public IReadOnlyList<string> AvAllowedExclusions { get; init; } = [];

    /// <summary>
    /// Advanced hardening: Attack Surface Reduction (ASR) rules, email scanning, catch-up
    /// scans, scan CPU cap, quarantine retention, signature-before-scan, and system
    /// exploit-protection mitigations. Enforced + drift-reported like the real-time settings.
    /// </summary>
    public bool AvHardening { get; init; } = true;
    /// <summary>ASR rules in Audit (report-only) instead of Block mode — safer initial rollout.</summary>
    public bool AvAsrAudit { get; init; } = false;
    /// <summary>Controlled Folder Access (anti-ransomware on user folders). Opt-in: can block legit writes.</summary>
    public bool AvControlledFolderAccess { get; init; } = false;
    /// <summary>When CFA is on, run it in Audit mode (report-only) instead of Block.</summary>
    public bool AvCfaAudit { get; init; } = true;
    /// <summary>Max average CPU load during a scan (1-100; 0 = leave Defender default).</summary>
    public int AvScanCpuLimit { get; init; } = 50;
    /// <summary>Days before quarantined items are auto-purged (0 = leave Defender default).</summary>
    public int AvQuarantinePurgeDays { get; init; } = 30;

    /// <summary>Chrome-like silent updater (LocalSystem). Independent of <see cref="Enabled"/>.</summary>
    public bool UpdateEnabled { get; init; } = true;
    public int UpdateCheckIntervalSec { get; init; } = 60;
    public bool UpdateAutoInstall { get; init; } = true;
    /// <summary>
    /// Spread silent downloads across this many seconds (stable per-machine hash) so a
    /// fleet does not stampede <c>/downloads</c>. GUI force uses a short window (≤30s).
    /// </summary>
    public int UpdateDownloadJitterSec { get; init; } = 3600;

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
            PortBlock = GetBool("ALPHA_DLP_PORT_BLOCK", true),
            DeleteBlock = GetBool("ALPHA_DLP_DELETE_BLOCK", true),
            LoginReboot = GetBool("ALPHA_DLP_LOGIN_REBOOT", true),
            MaxLocalAlerts = GetInt("ALPHA_DLP_MAX_LOCAL_ALERTS", 50_000),
            DedupSec = GetInt("ALPHA_DLP_DEDUP_SEC", 300),
            PipeName = Environment.GetEnvironmentVariable("ALPHA_DLP_PIPE_NAME") is { Length: > 0 } p
                ? p
                : defaultPipe,
            WatchdogEnabled = GetBool("ALPHA_DLP_WATCHDOG", true),
            WatchdogPollSec = GetInt("ALPHA_DLP_WATCHDOG_POLL_SEC", 8),
            AvEnforce = GetBool("ALPHA_AV_ENFORCE", true),
            AvGuardSec = GetInt("ALPHA_AV_GUARD_SEC", 30),
            AvStatusSec = GetInt("ALPHA_AV_STATUS_SEC", 300),
            AvQuickScanHours = GetIntAllowZero("ALPHA_AV_QUICK_SCAN_HOURS", 24),
            AvFullScanDays = GetIntAllowZero("ALPHA_AV_FULL_SCAN_DAYS", 7),
            AvAllowedExclusions = (Environment.GetEnvironmentVariable("ALPHA_AV_ALLOWED_EXCLUSIONS") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AvHardening = GetBool("ALPHA_AV_HARDENING", true),
            AvAsrAudit = GetBool("ALPHA_AV_ASR_AUDIT", false),
            AvControlledFolderAccess = GetBool("ALPHA_AV_CFA", false),
            AvCfaAudit = GetBool("ALPHA_AV_CFA_AUDIT", true),
            AvScanCpuLimit = GetIntAllowZero("ALPHA_AV_SCAN_CPU_LIMIT", 50),
            AvQuarantinePurgeDays = GetIntAllowZero("ALPHA_AV_QUARANTINE_PURGE_DAYS", 30),
            // Mirror client defaults: on unless explicitly disabled.
            UpdateEnabled = GetBool("ALPHA_UPDATE_ENABLED", true),
            UpdateCheckIntervalSec = Math.Max(15, GetInt("ALPHA_UPDATE_CHECK_INTERVAL_SEC", 60)),
            UpdateAutoInstall = GetBool("ALPHA_UPDATE_AUTO_INSTALL", true),
            UpdateDownloadJitterSec = Math.Max(0, GetIntAllowZero("ALPHA_UPDATE_DOWNLOAD_JITTER_SEC", 3600)),
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

    /// <summary>
    /// Re-read local blocker kill-switches from the process environment (after
    /// <see cref="EnvBootstrap.SyncServiceEnvAfterPolicyApply"/> reloads <c>service.env</c>).
    /// </summary>
    public void RefreshLocalSwitches()
    {
        UsbBlock = GetBool("ALPHA_DLP_USB_BLOCK", true);
        WebBlock = GetBool("ALPHA_DLP_WEB_BLOCK", true);
        NetworkLockdown = GetBool("ALPHA_DLP_NETWORK_LOCKDOWN", true);
        AppBlock = GetBool("ALPHA_DLP_APP_BLOCK", true);
        PortBlock = GetBool("ALPHA_DLP_PORT_BLOCK", true);
        var poll = GetInt("ALPHA_DLP_POLICY_POLL_SEC", PolicyPollSec);
        if (poll > 0) PolicyPollSec = poll;
        var alert = GetInt("ALPHA_DLP_ALERT_SYNC_SEC", AlertSyncSec);
        if (alert > 0) AlertSyncSec = alert;
    }

    private static bool GetBool(string key, bool fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(v)) return fallback;
        return v is "1" or "true" or "TRUE" or "yes" or "YES";
    }

    private static int GetIntAllowZero(string key, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return int.TryParse(v, out var n) && n >= 0 ? n : fallback;
    }

    private static int GetInt(string key, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return int.TryParse(v, out var n) && n > 0 ? n : fallback;
    }

    private static int GetIntAllowZero(string key, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return int.TryParse(v, out var n) && n >= 0 ? n : fallback;
    }
}
