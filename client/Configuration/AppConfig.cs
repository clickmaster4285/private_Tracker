using System.Reflection;

namespace client.Configuration;

public class AppConfig
{
    private string _clientId = string.Empty;
    public string ClientId
    {
        get => string.IsNullOrWhiteSpace(_clientId) ? (_clientId = GenerateMachineId()) : _clientId;
        set => _clientId = value;
    }
    public string DbPath { get; init; } = "data/alpha_tracker.db";
    public string? DbEncryptionKey { get; init; }
    public int CollectIntervalSec { get; init; } = 30;
    public string LogLevel { get; init; } = "Info";
    public string? ServerUrl { get; init; }
    public string? ApiKey { get; init; }

    // Browser journey (Option B — accessibility-based; see Core/BrowserAccessibility)
    public bool BrowserTrackingEnabled { get; init; } = true;
    public int BrowserAccessibilityPollSec { get; init; } = 3;   // how often the OS accessibility tree is polled
    public int BrowserJourneyIdleMinutes { get; init; } = 15;    // close journeys after this many idle minutes
    public bool BrowserCaptureIncognito { get; init; } = false;  // store incognito URLs (legal review required; default off)

    // Browser journey URL fallback — reads the browser's OWN profile history DB when the
    // a11y tree cannot expose the omnibox URL (Linux Chrome 136+ / snap Firefox). Works
    // while the browser runs; no restart, no flag, no extension.
    public bool BrowserHistoryEnabled { get; init; } = true;
    public int BrowserHistoryPollSec { get; init; } = 10;        // how often history DBs are re-scanned/re-read

    // File Explorer journey tracking (Desktop Event Bus — AT-SPI on Linux, Shell COM
    // on Windows, FileSystemWatcher + recent-files on every platform). Captures file
    // manager navigations + file create/rename/delete into app_items journeys.
    public bool FileJourneyEnabled { get; init; } = true;

    // USB / peripheral hotplug tracking (synced to the server since 2026-08-11;
    // never deleted client-side)
    public bool HardwareDevicesEnabled { get; init; } = true;

    // Software inventory — 100% EVENT-DRIVEN detection via InstalledSoftwareWatcher: it watches
    // the OS install locations (.desktop dirs, dpkg state, Start Menu, /Applications, package-
    // manager dirs) and triggers an instant rescan when software is installed/uninstalled through
    // terminal / software center / control panel / cmd / powershell / manual file delete. NO
    // minute-based periodic scan (user rule 2026-08-10).
    public bool InventoryWatchEnabled { get; init; } = true;

    // ─── Sync engine (dedicated SyncService background loop) ───
    // Collection NEVER blocks on the network anymore: SyncService drains unsent SQLite rows on
    // its own loop, in chunks bounded by BOTH row count and serialized payload bytes, with a
    // politeness pause between chunks and exponential backoff on failure. A 50k+ row backlog
    // drains in minutes without spiking CPU or adding latency to collection.
    public int SyncIntervalSec { get; init; } = 60;          // min wait between drain passes when idle
    public int SyncMaxRows { get; init; } = 1000;            // max rows per chunk
    public int SyncMaxBytes { get; init; } = 1_000_000;      // max serialized payload bytes per chunk (~1MB)
    public int SyncChunkDelayMs { get; init; } = 150;        // politeness pause between chunks
    public int SyncMaxDurationSec { get; init; } = 300;      // per-pass time budget — a huge backlog never monopolizes CPU
    public int SyncBackoffMaxSec { get; init; } = 300;       // exponential-backoff ceiling on failure (5 min)
    public bool SyncCompression { get; init; } = true;       // gzip request bodies (server: middleware.Decompress)
    public int SyncRetentionHours { get; init; } = 24;       // retention: synced app_items/app_sessions older than this are deleted client-side
    /// <summary>
    /// Max random delay (seconds) before the first post-login sync drain.
    /// Spreads 09:00 login storms across ~N seconds so 1000 agents do not hit the API in the same second.
    /// 0 = immediate (legacy behaviour).
    /// </summary>
    public int SyncLoginJitterSec { get; init; } = 120;
    // Account removal (R4, 2026-10-19): how long `client --remove-account` waits for the
    // final drain before wiping anyway. Bounded so a huge backlog / offline machine can
    // never block removal forever (Removal always proceeds; the unsent rows are the
    // operator's explicit trade-off and are reported).
    public int RemoveAccountSyncTimeoutSec { get; init; } = 120;

    // ─── Time & Attendance (Phase 1, finalplan §3) ───
    // Idle thresholds (A.4): when does "idle" start, and how often to poll the OS
    // idle source. The away threshold is reserved for A.8's status computation.
    public bool TaEnabled { get; init; } = true;
    public int IdleThresholdSeconds { get; init; } = 120;   // when does "idle" start
    public int IdleAwayThresholdSeconds { get; init; } = 600; // when does "away" start (A.8)
    public int IdlePollSeconds { get; init; } = 30;         // poll cadence
    public int LockHysteresisSeconds { get; init; } = 30;   // screen_lock dedup window
    public int EventAggregationWindowSec { get; init; } = 300; // 5-min sync buckets (S1)
    public int TaMaxLocalRows { get; init; } = 50_000;       // unsynced row ceiling (S6)
    /// <summary>Spawn <c>--timedatewarning</c> when clock/timezone is wrong (default on with T&amp;A).</summary>
    public bool TimeDateWarnEnabled { get; init; } = true;
    /// <summary>Hours between clock/timezone health checks (default 1). First check runs at start.</summary>
    public int TimeDateCheckHours { get; init; } = 1;
    /// <summary>Absolute skew vs server UTC (seconds) that triggers the warning (default 60).</summary>
    public int TimeDateSkewSec { get; init; } = 60;
    /// <summary>After Dismiss, do not re-spawn the warning window for this many hours (default 4).</summary>
    public int TimeDateCooldownHours { get; init; } = 4;

    // ─── GPS & Location (Phase 3, finalplan §16) ───
    // Default OFF — requires OS location permission + employee consent.
    public bool LocationEnabled { get; init; } = false;
    public int LocationPollSec { get; init; } = 300;         // 5 min — not continuous GPS
    /// <summary>When false, only OS GPS/WiFi fixes are stored — no coarse IP geolocation.</summary>
    public bool LocationIpFallback { get; init; } = false;

    // ─── Live stream (WebRTC SFU screen preview) ───
    // Outbound signaling WS to /live-stream/push + WebRTC publish. Media flows
    // only after the server sends start (an admin is watching). Default OFF.
    public bool StreamEnabled { get; init; } = false;
    public int StreamFps { get; init; } = 12;
    /// <summary>Capture scale cap. Hard-capped at 1920 — 4K burns bits without looking sharper on VP8.</summary>
    public int StreamMaxWidth { get; init; } = 1920;
    /// <summary>
    /// VP8 target bitrate. With ~1s keyframes (P-frames between), 6–8 Mbps
    /// is enough for sharp Full-HD screen text. Cap 15000.
    /// </summary>
    public int StreamMaxBitrateKbps { get; init; } = 12000;
    /// <summary>
    /// Seconds between forced VP8 keyframes. Default 1. Do NOT set to 0 —
    /// all-I-frame encode starves the bitrate budget and looks blocky.
    /// </summary>
    public int StreamKeyframeIntervalSec { get; init; } = 1;
    /// <summary>
    /// Minimum measured uplink (kbps) required before publishing. Below this
    /// the client skips publish and logs — avoids silent slideshow on thin links.
    /// </summary>
    public int StreamMinUplinkKbps { get; init; } = 300;
    /// <summary>
    /// libvpx GLagInFrames (altref). Default 0 = lowest latency for live preview;
    /// set ALPHA_STREAM_VP8_LAG_FRAMES=1 after A/B on a real stream if compression wins.
    /// </summary>
    public int StreamVp8LagFrames { get; init; } = 0;
    /// <summary>
    /// Dev/QA only: artificial delay (ms) after each SendVideo so ABR degrade can be
    /// exercised without NIC throttling. 0 = off. Never leave non-zero in fleet config.enc.
    /// </summary>
    public int StreamDebugSendDelayMs { get; init; } = 0;

    // ─── Offline-first screenshots (capture local → sync upload) ───
    public bool ScreenshotEnabled { get; init; } = true;
    public int ScreenshotIntervalSec { get; init; } = 30;
    public int ScreenshotMaxWidth { get; init; } = 1280;
    public int ScreenshotWebpQuality { get; init; } = 75;
    public int ScreenshotMaxPending { get; init; } = 5000;
    public int ScreenshotMaxPendingMb { get; init; } = 2048;
    public int ScreenshotSyncBatch { get; init; } = 5;
    public int ScreenshotSyncPollSec { get; init; } = 15;

    // ─── Control WebSocket (presence / keep-alive channel) ───
    // Long-lived outbound WS to GET /api/v1/ws (DeviceAuth). Independent of
    // LiveStreamClient. Default OFF — fleet opt-in + config.enc bake required.
    public bool WsEnabled { get; init; } = false;
    public int WsPingSec { get; init; } = 30;
    public int WsReconnectBaseSec { get; init; } = 2;

    // ─── Self-update (public releases bridge on ALPHA_SERVER_URL) ───
    // GET {ServerUrl}/api/v1/releases + GET /api/v1/downloads/:assetId. No GitHub
    // token on the client. (REPO= in .env is only for publish/release.sh / gh CLI.)
    public bool UpdateEnabled { get; init; } = true;          // master switch for all update behaviour
    public int UpdateCheckIntervalSec { get; init; } = 60;    // seconds between background checks against the server
    public bool UpdateAutoInstall { get; init; } = true;      // auto-download+install when a check finds a newer version

    public static AppConfig FromEnv()
    {
        return new AppConfig
        {
            ClientId = GetEnv("ALPHA_CLIENT_ID") ?? GenerateMachineId(),
            DbPath = GetEnv("ALPHA_DB_PATH") ?? "data/alpha_tracker.db",
            DbEncryptionKey = GetEnv("ALPHA_DB_ENCRYPTION_KEY"),
            CollectIntervalSec = int.TryParse(GetEnv("ALPHA_COLLECT_INTERVAL_SEC"), out var sec) ? sec : 30,
            LogLevel = GetEnv("ALPHA_LOG_LEVEL") ?? "Info",
            ServerUrl = GetEnv("ALPHA_SERVER_URL") ?? GetDefaultServerUrl(),
            ApiKey = GetEnv("ALPHA_API_KEY"),
            BrowserTrackingEnabled = GetEnv("ALPHA_BROWSER_TRACKING_ENABLED") is not ("0" or "false" or "False"),
            BrowserAccessibilityPollSec = int.TryParse(GetEnv("ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS"), out var poll) ? poll : 3,
            BrowserJourneyIdleMinutes = int.TryParse(GetEnv("ALPHA_BROWSER_JOURNEY_IDLE_MINUTES"), out var idle) ? idle : 15,
            BrowserCaptureIncognito = GetEnv("ALPHA_BROWSER_CAPTURE_INCOGNITO") is ("1" or "true" or "True"),
            BrowserHistoryEnabled = GetEnv("ALPHA_BROWSER_HISTORY_ENABLED") is not ("0" or "false" or "False"),
            BrowserHistoryPollSec = int.TryParse(GetEnv("ALPHA_BROWSER_HISTORY_POLL_SECONDS"), out var histPoll) ? histPoll : 10,
            FileJourneyEnabled = GetEnv("ALPHA_FILE_JOURNEY_ENABLED") is not ("0" or "false" or "False"),
            HardwareDevicesEnabled = GetEnv("ALPHA_HARDWARE_DEVICES_ENABLED") is not ("0" or "false" or "False"),
            InventoryWatchEnabled = GetEnv("ALPHA_INVENTORY_WATCH_ENABLED") is not ("0" or "false" or "False"),
            SyncIntervalSec = Math.Max(1, int.TryParse(GetEnv("ALPHA_SYNC_INTERVAL_SEC"), out var syncInt) ? syncInt : 60),
            SyncMaxRows = Math.Max(1, int.TryParse(GetEnv("ALPHA_SYNC_MAX_ROWS"), out var syncRows) ? syncRows : 1000),
            SyncMaxBytes = Math.Max(16 * 1024, int.TryParse(GetEnv("ALPHA_SYNC_MAX_BYTES"), out var syncBytes) ? syncBytes : 1_000_000),
            SyncChunkDelayMs = Math.Max(0, int.TryParse(GetEnv("ALPHA_SYNC_CHUNK_DELAY_MS"), out var syncDelay) ? syncDelay : 150),
            SyncMaxDurationSec = Math.Max(10, int.TryParse(GetEnv("ALPHA_SYNC_MAX_DURATION_SEC"), out var syncDur) ? syncDur : 300),
            SyncBackoffMaxSec = Math.Max(5, int.TryParse(GetEnv("ALPHA_SYNC_BACKOFF_MAX_SEC"), out var syncBack) ? syncBack : 300),
            SyncCompression = GetEnv("ALPHA_SYNC_COMPRESSION") is not ("0" or "false" or "False"),
            SyncRetentionHours = Math.Max(1, int.TryParse(GetEnv("ALPHA_SYNC_RETENTION_HOURS"), out var syncRet) ? syncRet : 24),
            SyncLoginJitterSec = Math.Max(0, int.TryParse(GetEnv("ALPHA_SYNC_LOGIN_JITTER_SEC"), out var syncJitter) ? syncJitter : 120),
            RemoveAccountSyncTimeoutSec = Math.Max(5, int.TryParse(GetEnv("ALPHA_REMOVE_ACCOUNT_SYNC_TIMEOUT_SEC"), out var remTmo) ? remTmo : 120),
            UpdateEnabled = GetEnv("ALPHA_UPDATE_ENABLED") is not ("0" or "false" or "False"),
            UpdateCheckIntervalSec = Math.Max(15, int.TryParse(GetEnv("ALPHA_UPDATE_CHECK_INTERVAL_SEC"), out var updSec) ? updSec : 60),
            UpdateAutoInstall = GetEnv("ALPHA_UPDATE_AUTO_INSTALL") is not ("0" or "false" or "False"),
            TaEnabled = GetEnv("ALPHA_TA_ENABLED") is not ("0" or "false" or "False"),
            IdleThresholdSeconds = Math.Max(10, int.TryParse(GetEnv("ALPHA_IDLE_THRESHOLD_SEC"), out var idleTh) ? idleTh : 120),
            IdleAwayThresholdSeconds = Math.Max(30, int.TryParse(GetEnv("ALPHA_IDLE_AWAY_THRESHOLD_SEC"), out var idleAway) ? idleAway : 600),
            IdlePollSeconds = Math.Max(5, int.TryParse(GetEnv("ALPHA_IDLE_POLL_SEC"), out var idlePoll) ? idlePoll : 30),
            LockHysteresisSeconds = Math.Max(5, int.TryParse(GetEnv("ALPHA_TA_LOCK_HYSTERESIS_SEC"), out var lockHys) ? lockHys : 30),
            EventAggregationWindowSec = Math.Max(60, int.TryParse(GetEnv("ALPHA_EVENT_AGGREGATION_WINDOW_SEC"), out var aggWin) ? aggWin : 300),
            TaMaxLocalRows = Math.Max(1000, int.TryParse(GetEnv("ALPHA_TA_MAX_LOCAL_ROWS"), out var taMax) ? taMax : 50_000),
            TimeDateWarnEnabled = GetEnv("ALPHA_TA_TIMEDATE_WARN_ENABLED") is not ("0" or "false" or "False"),
            TimeDateCheckHours = Math.Max(1, int.TryParse(GetEnv("ALPHA_TA_TIMEDATE_CHECK_HOURS"), out var tdCheck) ? tdCheck : 1),
            TimeDateSkewSec = Math.Max(5, int.TryParse(GetEnv("ALPHA_TA_TIMEDATE_SKEW_SEC"), out var tdSkew) ? tdSkew : 60),
            TimeDateCooldownHours = Math.Max(1, int.TryParse(GetEnv("ALPHA_TA_TIMEDATE_COOLDOWN_HOURS"), out var tdCool) ? tdCool : 4),
            LocationEnabled = GetEnv("ALPHA_LOCATION_ENABLED") is ("1" or "true" or "True"),
            LocationPollSec = Math.Max(60, int.TryParse(GetEnv("ALPHA_LOCATION_POLL_SEC"), out var locPoll) ? locPoll : 300),
            LocationIpFallback = GetEnv("ALPHA_LOCATION_IP_FALLBACK") is ("1" or "true" or "True"),
            StreamEnabled = IsEnvTruthy(GetEnv("ALPHA_STREAM_ENABLED")),
            StreamFps = Math.Clamp(int.TryParse(GetEnv("ALPHA_STREAM_FPS"), out var streamFps) ? streamFps : 12, 1, 30),
            StreamMaxWidth = Math.Clamp(int.TryParse(GetEnv("ALPHA_STREAM_MAX_WIDTH"), out var streamW) ? streamW : 1920, 320, 1920),
            StreamMaxBitrateKbps = Math.Clamp(int.TryParse(GetEnv("ALPHA_STREAM_MAX_BITRATE_KBPS"), out var streamBr) ? streamBr : 12000, 500, 15000),
            StreamKeyframeIntervalSec = Math.Clamp(int.TryParse(GetEnv("ALPHA_STREAM_KEYFRAME_INTERVAL_SEC"), out var streamKf) ? streamKf : 1, 1, 10),
            StreamMinUplinkKbps = Math.Clamp(int.TryParse(GetEnv("ALPHA_STREAM_MIN_UPLINK_KBPS"), out var streamMinUp) ? streamMinUp : 300, 100, 15000),
            StreamVp8LagFrames = Math.Clamp(int.TryParse(GetEnv("ALPHA_STREAM_VP8_LAG_FRAMES"), out var streamLag) ? streamLag : 0, 0, 5),
            StreamDebugSendDelayMs = Math.Clamp(int.TryParse(GetEnv("ALPHA_STREAM_DEBUG_SEND_DELAY_MS"), out var streamDbgDelay) ? streamDbgDelay : 0, 0, 500),
            ScreenshotEnabled = GetEnv("ALPHA_SCREENSHOT_ENABLED") is not ("0" or "false" or "False"),
            ScreenshotIntervalSec = Math.Clamp(int.TryParse(GetEnv("ALPHA_SCREENSHOT_INTERVAL_SEC"), out var ssInt) ? ssInt : 30, 10, 600),
            ScreenshotMaxWidth = Math.Clamp(int.TryParse(GetEnv("ALPHA_SCREENSHOT_MAX_WIDTH"), out var ssW) ? ssW : 1280, 320, 1920),
            ScreenshotWebpQuality = Math.Clamp(int.TryParse(GetEnv("ALPHA_SCREENSHOT_WEBP_QUALITY"), out var ssQ) ? ssQ : 75, 1, 100),
            ScreenshotMaxPending = Math.Max(100, int.TryParse(GetEnv("ALPHA_SCREENSHOT_MAX_PENDING"), out var ssPend) ? ssPend : 5000),
            ScreenshotMaxPendingMb = Math.Max(64, int.TryParse(GetEnv("ALPHA_SCREENSHOT_MAX_PENDING_MB"), out var ssMb) ? ssMb : 2048),
            ScreenshotSyncBatch = Math.Clamp(int.TryParse(GetEnv("ALPHA_SCREENSHOT_SYNC_BATCH"), out var ssBatch) ? ssBatch : 5, 1, 20),
            ScreenshotSyncPollSec = Math.Clamp(int.TryParse(GetEnv("ALPHA_SCREENSHOT_SYNC_POLL_SEC"), out var ssPoll) ? ssPoll : 15, 5, 120),
            WsEnabled = IsEnvTruthy(GetEnv("ALPHA_WS_ENABLED")),
            WsPingSec = Math.Clamp(int.TryParse(GetEnv("ALPHA_WS_PING_SEC"), out var wsPing) ? wsPing : 30, 5, 300),
            WsReconnectBaseSec = Math.Clamp(int.TryParse(GetEnv("ALPHA_WS_RECONNECT_BASE_SEC"), out var wsRecon) ? wsRecon : 2, 1, 60),
        };
    }

    private static bool IsEnvTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase)
         || value.Equals("true", StringComparison.OrdinalIgnoreCase)
         || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
         || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static string? GetDefaultServerUrl()
    {
        var attrs = typeof(AppConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        return attrs.FirstOrDefault(a => a.Key == "DefaultServerUrl")?.Value;
    }

    private static string? GetEnv(string key)
    {
        var val = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(val) ? null : val.Trim();
    }

    private static string GenerateMachineId()
    {
        var id = Environment.GetEnvironmentVariable("ALPHA_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(id)) return id.Trim();

        // Write to the user-writable data dir (Installer-Parity Rule §6): the install
        // dir is root-owned and unwritable. Using the app dir caused .machine-id to
        // never persist, generating a NEW machine_id on every restart — breaking auth
        // and producing orphaned sessions.
        var dataDir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlphaAITracker")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "alpha-ai-tracker");
        try { Directory.CreateDirectory(dataDir); } catch { }
        var existing = Path.Combine(dataDir, ".machine-id");
        if (File.Exists(existing))
        {
            return File.ReadAllText(existing).Trim();
        }

        var newId = Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(existing, newId);
        }
        catch { }
        return newId;
    }

    /// <summary>
    /// Resets the in-memory ClientId so that subsequent accesses generate a fresh identifier
    /// after account removal.
    /// </summary>
    public void ResetMachineId()
    {
        _clientId = string.Empty;
    }

    /// <summary>
    /// Removes the .machine-id and device_id files from all known runtime directories
    /// (data directory, user config directory, and application directory).
    /// Used by --remove-account to cleanly disassociate the machine from its prior registration
    /// without breaking fleet configuration (config.enc), database files, or logging.
    /// </summary>
    public static void RemoveMachineAndDeviceIdentifiers()
    {
        var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. User-writable data directory (%LOCALAPPDATA%\AlphaAITracker or ~/.local/share/alpha-ai-tracker)
        try
        {
            var dataDir = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlphaAITracker")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "alpha-ai-tracker");
            if (Directory.Exists(dataDir))
            {
                candidateDirs.Add(dataDir);
                var subData = Path.Combine(dataDir, "data");
                if (Directory.Exists(subData)) candidateDirs.Add(subData);
            }
        }
        catch { }

        // 2. User config directory (%APPDATA%\AlphaAITracker or ~/.config/alpha-ai-tracker)
        try
        {
            var configDir = EnvLoader.UserConfigDir;
            if (!string.IsNullOrEmpty(configDir) && Directory.Exists(configDir))
            {
                candidateDirs.Add(configDir);
            }
        }
        catch { }

        // 3. Application base directory (next to executable)
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(appDir) && Directory.Exists(appDir))
            {
                candidateDirs.Add(appDir);
            }
        }
        catch { }

        var targetFileNames = new[]
        {
            ".machine-id",
            "machine-id",
            "device_id",
            ".device_id",
            "device-id",
            ".device-id",
            "deviceId",
            ".deviceId"
        };

        foreach (var dir in candidateDirs)
        {
            foreach (var name in targetFileNames)
            {
                try
                {
                    var filePath = Path.Combine(dir, name);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        Console.WriteLine($"[remove-account] Removed identifier file: {filePath}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[remove-account] Could not delete {name} in {dir}: {ex.Message}");
                }
            }
        }
    }
}
