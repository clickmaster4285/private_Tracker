using client.Core;
using client.Core.Models;

namespace client.Core.Abstractions;

public interface ILogStore
{
    Task InitializeAsync(CancellationToken ct);

    // ── Device & System Info ──

    Task StoreDeviceHardwareInfoAsync(IReadOnlyList<DeviceHardwareInfo> entries, CancellationToken ct);
    Task<IReadOnlyList<DeviceHardwareInfo>> GetUnsentDeviceHardwareInfoAsync(int limit, CancellationToken ct);
    Task MarkDeviceHardwareInfoSentAsync(IReadOnlyList<string> ids, CancellationToken ct);
    /// <summary>Most recently collected device hardware row (for dedup across restarts).</summary>
    Task<DeviceHardwareInfo?> GetLastDeviceHardwareInfoAsync(CancellationToken ct);

    Task StoreInstalledApplicationsAsync(IReadOnlyList<InstalledApplication> entries, CancellationToken ct);
    Task<IReadOnlyList<InstalledApplication>> GetUnsentInstalledApplicationsAsync(int limit, CancellationToken ct);
    Task MarkInstalledApplicationsSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>
    /// Inventory lifecycle reconciliation, called after every completed OS scan (rows are
    /// NEVER deleted — one row per install cycle). OPEN cycles (uninstall_date IS NULL)
    /// missing from the scan are CLOSED: is_installed=0 + uninstall_date=now (the uninstall
    /// event). A reinstall is NOT handled here — the next Store* pass opens a brand-new
    /// cycle row with the new install_date, so install→uninstall→reinstall yields one record
    /// per cycle. Each pass is skipped when its seen-set is empty, and a 50% confidence guard
    /// skips closing when a scan looks partial (failed/partial scan must not uninstall
    /// everything).
    /// </summary>
    Task ApplyInventoryLifecycleAsync(
        IReadOnlySet<string> seenAppNames,
        IReadOnlySet<string> seenPackageKeys,
        DateTime now,
        CancellationToken ct);

    Task StoreInstalledPackagesAsync(IReadOnlyList<InstalledPackage> entries, CancellationToken ct);
    Task<IReadOnlyList<InstalledPackage>> GetUnsentInstalledPackagesAsync(int limit, CancellationToken ct);
    Task MarkInstalledPackagesSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    Task StoreNetworkInfoAsync(IReadOnlyList<NetworkInfo> entries, CancellationToken ct);
    Task<IReadOnlyList<NetworkInfo>> GetUnsentNetworkInfoAsync(int limit, CancellationToken ct);
    Task MarkNetworkInfoSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    Task StoreSessionEventsAsync(IReadOnlyList<SessionEvent> entries, CancellationToken ct);
    Task<IReadOnlyList<SessionEvent>> GetUnsentSessionEventsAsync(int limit, CancellationToken ct);
    Task MarkSessionEventsSentAsync(IReadOnlyList<string> ids, CancellationToken ct);
    /// <summary>Count of unsynced session_events rows (S6 ceiling check).</summary>
    Task<int> CountUnsentSessionEventsAsync(CancellationToken ct);
    /// <summary>
    /// When unsynced session_events exceed <paramref name="maxRows"/>, delete the oldest
    /// excess rows and insert a single <see cref="SessionEventTypes.OldDataDropped"/> sentinel
    /// carrying the rolled-up count + time span. Returns the number of rows removed.
    /// </summary>
    Task<int> RollupExcessUnsentSessionEventsAsync(int maxRows, CancellationToken ct);
    /// <summary>Most recent session event (for login/logout state machine — avoids duplicate login rows).</summary>
    Task<SessionEvent?> GetLastSessionEventAsync(CancellationToken ct);

    // ── Storage Devices (relational child of device_hardware_info) ──

    Task StoreStorageDevicesAsync(IReadOnlyList<StorageDevice> entries, CancellationToken ct);
    Task<IReadOnlyList<StorageDevice>> GetUnsentStorageDevicesAsync(int limit, CancellationToken ct);
    Task MarkStorageDevicesSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>
    /// Storage devices belonging to the most recent device_hardware_info row, for display.
    /// Unlike GetUnsentStorageDevicesAsync this ignores is_synced — the System Specifications
    /// page needs the current inventory, not the sync backlog.
    /// </summary>
    Task<IReadOnlyList<StorageDevice>> GetLatestStorageDevicesAsync(CancellationToken ct);

    // ── Hardware Devices (USB / peripheral hotplug tracking) ──

    Task StoreHardwareDevicesAsync(IReadOnlyList<HardwareDevice> entries, CancellationToken ct);
    Task<IReadOnlyList<HardwareDevice>> GetOpenHardwareDevicesAsync(CancellationToken ct);
    Task<HardwareDevice?> GetOpenHardwareDeviceByBusPathAsync(string busPath, CancellationToken ct);
    Task CloseHardwareDeviceAsync(string id, DateTime unpluggedAt, CancellationToken ct);

    // ── Sync (2026-08-11): hardware devices are sent to the server; never deleted client-side ──
    Task<IReadOnlyList<HardwareDevice>> GetUnsentHardwareDevicesAsync(int limit, CancellationToken ct);
    Task MarkHardwareDevicesSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    // ── Location samples (Phase 3 GPS) ──
    Task StoreLocationSamplesAsync(IReadOnlyList<LocationSample> entries, CancellationToken ct);
    Task<IReadOnlyList<LocationSample>> GetUnsentLocationSamplesAsync(int limit, CancellationToken ct);
    Task MarkLocationSamplesSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    // ── Sync (2026-08-11): app_status + permission_status are sent to the server; never
    //    deleted client-side. app_status rows re-sync on change (is_synced reset by upsert).
    Task<IReadOnlyList<AppStatus>> GetUnsentAppStatusAsync(int limit, CancellationToken ct);
    Task MarkAppStatusSentAsync(IReadOnlyList<string> ids, CancellationToken ct);
    Task<IReadOnlyList<PermissionStatus>> GetUnsentPermissionStatusAsync(int limit, CancellationToken ct);
    Task MarkPermissionStatusSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>
    /// Retention cleanup (2026-08-11, default 24h): deletes rows that were already synced to
    /// the server and are no longer needed locally:
    ///   • app_items — synced + data older than the cutoff
    ///   • app_sessions — synced + CLOSED + started before the cutoff (open sessions are
    ///     NEVER deleted; a session is only deleted once all its app_items are gone)
    ///   • installed_applications / installed_packages — synced rows with is_installed = 0
    ///     (closed/uninstalled install cycles; the GUI hides them anyway)
    ///   • network_info — synced rows that were superseded (is_current = 0)
    /// Everything else is retained forever. Returns per-table deleted counts.
    /// </summary>
    Task<SyncedDataDeletionCounts> DeleteSyncedDataOlderThanAsync(DateTime cutoff, CancellationToken ct);

    // ── Installed App/Package Lookup (binary name → display name mapping) ──

    /// <summary>Look up an installed app by its executable binary name (e.g., \"code\" → Visual Studio Code)</summary>
    Task<InstalledApplication?> GetInstalledAppByBinaryNameAsync(string binaryName, CancellationToken ct);

    /// <summary>Fuzzy lookup by binary_name or app_name using LIKE (e.g., \"chrome\" matches \"google-chrome-stable\").
    /// Returns null if no fuzzy match found. Used to prevent duplicate entries when runtime process name
    /// differs from the .desktop binary name (e.g., process=\"chrome\", binary_name=\"google-chrome-stable\").</summary>
    Task<InstalledApplication?> GetInstalledAppByBinaryNameFuzzyAsync(string processName, CancellationToken ct);

    /// <summary>Look up an installed package by its package name</summary>
    Task<InstalledPackage?> GetInstalledPackageByNameAsync(string packageName, CancellationToken ct);

    /// <summary>Get all installed app binary names for fast in-memory filtering</summary>
    Task<HashSet<string>> GetAllInstalledAppBinaryNamesAsync(CancellationToken ct);

    /// <summary>Get all installed package names for fast in-memory filtering</summary>
    Task<HashSet<string>> GetAllInstalledPackageNamesAsync(CancellationToken ct);

    /// <summary>ALL installed_applications rows (the full inventory, for the Installed Applications page).</summary>
    Task<IReadOnlyList<InstalledApplication>> GetAllInstalledAppsAsync(CancellationToken ct);

    /// <summary>ALL installed_packages rows (the full inventory, for the Installed Applications page).</summary>
    Task<IReadOnlyList<InstalledPackage>> GetAllInstalledPackagesAsync(CancellationToken ct);

    /// <summary>Store a single auto-detected installed app (called when a running process is not yet in the DB).
    /// Returns the actual stored ID (may differ from entry.Id if app_name already existed via ON CONFLICT).</summary>
    Task<string> StoreInstalledAppAsync(InstalledApplication entry, CancellationToken ct);

    /// <summary>Delete a non-GUI installed_applications entry that was incorrectly auto-registered.
    /// Used by Phase 0a cleanup to remove shell/system entries (sh, snap) from the DB.</summary>
    Task DeleteInstalledAppAsync(string id, CancellationToken ct);

    /// <summary>Rows with an EMPTY desktop_id — the structural signature of runtime auto-registered
    /// junk (svchost, dllhost, conhost, Video.UI…). Real inventory rows always carry a Start Menu
    /// shortcut name / registry key / .desktop filename as their desktop_id.</summary>
    Task<IReadOnlyList<InstalledApplication>> GetInstalledAppsWithEmptyDesktopIdAsync(CancellationToken ct);

    /// <summary>Store a single auto-detected installed package (called when a running process is not yet in the DB).
    /// Returns the actual stored ID.</summary>
    Task<string> StoreInstalledPackageAsync(InstalledPackage entry, CancellationToken ct);

    // ── Application Logs ──

    Task StoreAppSessionsAsync(IReadOnlyList<AppSession> entries, CancellationToken ct);
    Task<IReadOnlyList<AppSession>> GetUnsentAppSessionsAsync(int limit, CancellationToken ct);
    Task MarkAppSessionsSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>
    /// Reset specific sessions to is_synced=0 so they are re-sent on the next sync pass.
    /// Called when the server reports missing session IDs during the orphan preflight
    /// (Bug #9 follow-up — breaks the permanent orphan deadlock).
    /// </summary>
    Task MarkAppSessionsUnsyncedByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct);

    // ── Generic App Items (child of app_sessions) ──

    Task StoreAppItemsAsync(IReadOnlyList<AppItem> entries, CancellationToken ct);
    Task<IReadOnlyList<AppItem>> GetUnsentAppItemsAsync(int limit, CancellationToken ct);
    Task MarkAppItemsSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>Update an app item's parent_item_id (for post-creation parent-child linking)</summary>
    Task UpdateAppItemParentAsync(string itemId, string parentItemId, CancellationToken ct);

    /// <summary>Open sessions with PID for hierarchy resolution (ended_at IS NULL).</summary>
    Task<IReadOnlyList<OpenSessionRecord>> GetOpenSessionRecordsAsync(CancellationToken ct);

    /// <summary>
    /// ALL open sessions regardless of process_id (for crash recovery).
    /// Unlike GetOpenSessionRecordsAsync, this does NOT filter by process_id IS NOT NULL,
    /// so it catches sessions that never had a PID assigned.
    /// </summary>
    Task<IReadOnlyList<OpenSessionRecord>> GetAllOpenSessionRecordsAsync(CancellationToken ct);

    /// <summary>Batch-close all app_items for a set of session IDs (for crash recovery).</summary>
    Task CloseAppItemsBySessionIdsAsync(IReadOnlyList<string> sessionIds, DateTime closedAt, CancellationToken ct);

    /// <summary>
    /// Close ONE app_item by id and mark it for re-sync (is_synced = 0), so the server
    /// learns about the close even when the row was already synced. Used by the browser
    /// journey tracker when a page navigation rotates the browser_tab root record.
    /// </summary>
    Task CloseAppItemAsync(string itemId, DateTime closedAt, CancellationToken ct);

    /// <summary>
    /// Atomically close a set of sessions AND their still-open app_items in ONE transaction.
    /// Acquires the connection gate once, so it is safe to call without nesting gated public methods.
    /// </summary>
    Task CloseSessionsAndAppItemsAsync(IReadOnlyList<AppSession> closeSessions, DateTime closedAt, CancellationToken ct);

    /// <summary>
    /// Persist accumulated foreground/background focus durations for open sessions and
    /// re-queue each row (is_synced = 0) so the server learns the growing totals.
    /// </summary>
    Task UpdateAppSessionFocusAsync(
        IReadOnlyList<(string Id, double ForegroundSeconds, double BackgroundSeconds)> updates,
        CancellationToken ct);

    /// <summary>Find an open context child item by type+identifier under a session.</summary>
    Task<AppItem?> GetOpenAppItemAsync(string appSessionId, string itemType, string identifier, CancellationToken ct);

    /// <summary>Find an open ROOT item of a given type (parent_item_id IS NULL) for a session — used to reuse journey roots.</summary>
    Task<AppItem?> GetOpenRootItemAsync(string appSessionId, string itemType, CancellationToken ct);

    /// <summary>Find an open journey event by journey_id + object_type + action + current_path.</summary>
    Task<AppItem?> GetOpenJourneyEventAsync(string journeyId, string objectType, string action, string currentPath, CancellationToken ct);

    /// <summary>Get the next sequence number for a journey (MAX(sequence) + 1).</summary>
    Task<int> GetNextSequenceAsync(string journeyId, CancellationToken ct);

    /// <summary>Update title/identifier on an existing open app item (URL/path change).</summary>
    Task UpdateAppItemContextAsync(string itemId, string title, string identifier, CancellationToken ct);

    /// <summary>Begin a transaction for atomic multi-write operations.</summary>
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct);

    /// <summary>Whether any storage device rows exist for the latest hardware record.</summary>
    Task<bool> HasStorageDevicesAsync(CancellationToken ct);

    // ── Network dedup helper ──

    Task<NetworkInfo?> GetLastNetworkInfoAsync(CancellationToken ct);
    /// <summary>Flip every is_current=1 row to 0 (before recording a network change).</summary>
    Task MarkAllNetworkInfoNotCurrentAsync(CancellationToken ct);
    /// <summary>Refresh last_seen_at on all is_current=1 rows (same IP still active).</summary>
    Task TouchCurrentNetworkInfoAsync(DateTime lastSeenAt, CancellationToken ct);

    // ── Status & Employee Info ──

    Task SetStatusAsync(string key, string value, CancellationToken ct);
    Task<string?> GetStatusAsync(string key, CancellationToken ct);
    Task SetPermissionStatusAsync(IReadOnlyDictionary<string, bool> permissions, string sessionType, CancellationToken ct);
    Task SaveEmployeeInfoAsync(EmployeeInfo employee, CancellationToken ct);
    Task<EmployeeInfo?> GetEmployeeInfoAsync(CancellationToken ct);
    Task ClearEmployeeInfoAsync(CancellationToken ct);

    /// <summary>
    /// Delete EVERY locally stored row for this account — the login credentials /
    /// device token (employee_info), all collected activity (sessions, items,
    /// events), the inventory + hardware/network caches and the mirrored
    /// schedules/holidays/terms — leaving only the schema itself.
    ///
    /// Used by the <c>--remove-account</c> client command (surfaced in the web
    /// "Remove Current Device" modal): the machine must end up with no trace of the
    /// removed employee and land back on the sign-in page. The machine identity (.machine-id)
    /// and device_id files are removed so the device cleanly regenerates identifiers on
    /// subsequent login, while the encrypted fleet config (config.enc) is preserved.
    /// </summary>
    Task WipeLocalAccountDataAsync(CancellationToken ct);

    /// <summary>
    /// Roaming Stop cleanup: delete telemetry rows with <c>is_synced=1</c> that have no
    /// unsynced dependents. Never touches identity / schedule / terms tables or
    /// <c>is_synced=0</c> / quarantined rows. Opposite of <see cref="WipeLocalAccountDataAsync"/>.
    /// </summary>
    Task<int> PurgeSyncedAsync(CancellationToken ct);

    // ════════════════════════════════════════════════════════════════════════
    // Time and Attendance (Phase 1, finalplan section 2.2)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Range query over session_events in a [from, to) UTC window.
    /// Used by AttendanceAggregator for the daily window read. Uses the read-only
    /// connection (no writer serialization).</summary>
    Task<IReadOnlyList<SessionEvent>> GetSessionEventsInRangeAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>Mirror-write the employee's schedule from GET /api/v1/schedules/me.
    /// Idempotent: ON CONFLICT(employee_id) updates in place.</summary>
    Task UpsertEmployeeScheduleAsync(
        string employeeId, string timezone, string weeklyPatternJson,
        int graceMinutes, string? validFrom, string? validTo, string? serverId,
        CancellationToken ct);

    /// <summary>Read all mirrored schedules. Used by AttendanceAggregator (A.8) to
    /// compute the local "arrival" status without re-fetching the server.</summary>
    Task<IReadOnlyList<(string EmployeeId, string Timezone, string WeeklyPattern, int GraceMinutes)>>
        ListEmployeeSchedulesAsync(CancellationToken ct);

    /// <summary>Mirror-write a single company holiday. Idempotent on holiday_date.</summary>
    Task UpsertCompanyHolidayAsync(string date, string label, string? serverId, CancellationToken ct);

    /// <summary>Read all mirrored holidays. Read-only connection.</summary>
    Task<IReadOnlyList<(string Date, string Label)>> ListCompanyHolidaysAsync(CancellationToken ct);

    /// <summary>Upsert the daily attendance rollup for one (employee, date). Idempotent
    /// on the composite primary key. first_active_at is preserved across updates (the
    /// "arrival" timestamp is set once and never overwritten for the same day).</summary>
    Task UpsertDailyAttendanceAsync(
        string employeeId, string workDate, DateTime? firstActiveAt, DateTime? lastActiveAt,
        int activeSeconds, int idleSeconds, int offShiftSeconds, string status, int lateMinutes,
        CancellationToken ct);

    /// <summary>Read the daily attendance rollup for one (employee, date). Returns null
    /// when no aggregator pass has ever run for that day. Read-only connection.</summary>
    Task<(int ActiveSeconds, int IdleSeconds, int OffShiftSeconds, DateTime? FirstActiveAt)?>
        GetDailyAttendanceAsync(string employeeId, string workDate, CancellationToken ct);

    /// <summary>Upsert the most recent clock-skew measurement for a server URL. One row
    /// per server (so lab/staging/prod measurements don't overwrite each other).</summary>
    Task UpsertTimeSkewAsync(string serverUrl, DateTime measuredAt, double skewSeconds, CancellationToken ct);

    /// <summary>Read the most recent clock-skew measurement for a server URL. Read-only
    /// connection. Returns null when never measured (first-run case).</summary>
    Task<(DateTime MeasuredAt, double SkewSeconds)?> GetLatestTimeSkewAsync(string serverUrl, CancellationToken ct);

    // ════════════════════════════════════════════════════════════════════════
    // Terms & Conditions acceptance gate (2026-09-16)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Upsert one server term for an employee. A row is PENDING when it is new
    /// (no row for this employee+id) or CHANGED (terms_version or content_hash differ from    /// the stored row) — changed terms reset is_accepted to 0 so the employee re-accepts.
    /// Unchanged terms keep their acceptance state untouched. Sort order rides along so    /// the pending queue preserves the server's presentation order while offline.</summary>
    Task UpsertClientTermAsync(ClientTerm term, CancellationToken ct);

    /// <summary>Pending (is_accepted = 0) terms for one employee in presentation order
    /// (sort_order, created_at, id) — the acceptance-gate queue.</summary>
    Task<IReadOnlyList<ClientTerm>> GetPendingClientTermsAsync(string employeeId, CancellationToken ct);
    /// <summary>Count of pending terms for one employee — the cheap gate check.</summary>
    Task<int> CountPendingClientTermsAsync(string employeeId, CancellationToken ct);
    /// <summary>Mark one term as agreed by the USER (clicked "I agree"). The row stays
    /// pending (is_accepted=0) until the server acknowledges the consent.</summary>
    Task MarkClientTermUserAcceptedAsync(string termId, string employeeId, CancellationToken ct);

    /// <summary>Mark one term accepted (server acknowledged via 2xx). Called ONLY after
    /// the consent POST succeeds — an unacknowledged acceptance must never masquerade
    /// as accepted (2026-09-09 sync-fix principle).</summary>
    Task MarkClientTermAcceptedAsync(string termId, string employeeId, DateTime acceptedAt, CancellationToken ct);
    /// <summary>Purge pending rows whose terms the server no longer requires (deactivated or
    /// deleted server-side — an admin "un-require" signal). Accepted rows are retained as the
    /// local audit mirror. Returns the number of pending rows removed.</summary>
    Task<int> PurgeStalePendingClientTermsAsync(string employeeId, IReadOnlySet<string> activeTermIds, CancellationToken ct);
}
