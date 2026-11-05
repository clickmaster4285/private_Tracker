using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Dedicated sync engine — runs on its OWN background loop so the collection loop in
/// LogCollectorService never blocks on the network. Drains unsent SQLite rows in adaptive
/// chunks bounded by BOTH row count (ALPHA_SYNC_MAX_ROWS) and serialized payload bytes
/// (ALPHA_SYNC_MAX_BYTES), gzips request bodies (server side: middleware.Decompress),
/// pauses politely between chunks (ALPHA_SYNC_CHUNK_DELAY_MS) so a backlog never spikes
/// CPU or network, and backs off exponentially on failure.
///
/// Large-backlog behavior (e.g. 50,000+ queued rows after a long offline period): instead
/// of the old inline sync that fetched a fixed 500 rows/table per 5-minute cycle (≈8 hours
/// to drain 50k, while BLOCKING collection), this service loops per table — fetch → send →
/// mark → short pause → next chunk — until drained or the per-pass time budget
/// (ALPHA_SYNC_MAX_DURATION_SEC) is spent, then the next pass continues. The client stays
/// fully responsive the whole time.
///
/// Idempotency is preserved: the server upserts by client GUID (ON CONFLICT (id) …), so a
/// chunk that fails after a partial send is retried safely next pass.
/// </summary>
public class SyncService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly ILogger<SyncService> _logger;
    private readonly HttpClient _httpClient;

    // Wake-up signal for an IMMEDIATE sync — released by RequestImmediateSync()
    // right after a successful login (or session restore), so all unsent tables
    // (device_hardware_info, employee profile data, installed apps/packages,
    // network, storage, hardware devices, permissions, …) land on the server
    // instantly instead of waiting for the next idle poll.
    private readonly SemaphoreSlim _syncSignal = new(0, 1);

    // Sticky "server is reachable" signal for WsClient (and any future always-on
    // channel). Set on the first HTTP 2xx from a real sync POST — never on an
    // empty drain pass (no network call). Cleared when credentials die so the
    // control channel does not keep hammering a server that rejects auth.
    private volatile bool _serverReachable;
    private TaskCompletionSource _serverReachableTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string? _employeeId;
    private string? _employeeName;
    private string? _token;
    private string? _deviceToken;

    // When a sync returns 401, we clear _deviceToken so the NEXT pass falls back to
    // the long-lived Bearer JWT (JWT_EMPLOYEE_ACCESS_EXPIRY=360h on the server). A
    // single 401 is not yet conclusive — the device record may simply have been
    // rotated by another login. So we only "give up" after the JWT fallback ALSO
    // fails within a short window. The flag is cleared the moment any sync
    // succeeds, so a transient server hiccup doesn't lock us out of the device path.
    private bool _authLooksDead;
    private DateTime _lastAuthFailureAt = DateTime.MinValue;
    private static readonly TimeSpan AuthGiveUpWindow = TimeSpan.FromSeconds(30);

    // ── Poison-row quarantine (in-memory only, app_items) ──
    // Rows the server explicitly REFUSES (rejectedIds from the app-items sync
    // response — their parent session was missing at insert time) stay is_synced=0
    // and re-send next pass, where the parent has usually landed (sessions drain
    // before items). A row refused QUARANTINE_RETRIES consecutive passes is
    // genuinely unparentable (empty/GONE app_session_id) — retrying forever would
    // grow the retry queue unboundedly. Quarantined rows are SKIPPED by the drain
    // until the process restarts: the row stays is_synced=0 (a rejected row must
    // never look synced), retention never deletes it (retention purges synced rows
    // only), and the counter reset on restart gives self-heal a fresh chance.
    private const int QuarantineRetries = 3;
    private readonly Dictionary<string, int> _appItemRejectCounts = new();

    private sealed class SyncBatchResponse
    {
        public int Synced { get; set; }
        public string? Message { get; set; }
        public List<string>? RejectedIds { get; set; }
        /// <summary>Parent session IDs the server does NOT have — the client must re-queue them.</summary>
        public List<string>? MissingSessionIds { get; set; }
        /// <summary>
        /// Server-side instruction for THIS device (R6, 2026-10-19). Currently the only
        /// value is "wipe_local": an admin removed this device, the server already
        /// persisted the batch that carried this response, and now wants the client to
        /// wipe its local DB + machine id and return to the sign-in page.
        /// </summary>
        public string? Action { get; set; }
    }

    private static readonly JsonSerializerOptions SyncResponseJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Raised when SyncService has concluded that the stored credentials are no longer
    /// accepted by the server (both the device token AND the Bearer JWT rejected).
    /// MainViewModel listens to surface a clear "please re-login" message instead of
    /// letting the warning loop silently retry.
    /// </summary>
    public event Action? OnAuthCredentialsDead;

    /// <summary>
    /// Raised ONCE per process when the server answers a sync with
    /// <c>{"action":"wipe_local"}</c> — the admin removed this device
    /// (R6, 2026-10-19) and the batch that carried the response has already
    /// been persisted server-side. The listener (AccountRemovalService) stops
    /// collection, wipes the local DB + machine id and shows the sign-in page.
    /// </summary>
    public event Action? OnServerRequestedWipe;

    private bool _wipeRequestedFromServer;

    // ── Drain-pass completion signal (R4 / R6) ──────────────────────────────
    // --remove-account (and the self-remove flow) must WAIT for the pending rows
    // to reach the server before wiping the local DB, otherwise unsynced data is
    // destroyed. Each pass that finishes its drain loop publishes here; a pass is
    // "fully drained" only when it neither failed nor hit the per-pass budget.
    private TaskCompletionSource<bool> _passCompletedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _lastPassDrainedAll;

    /// <summary>
    /// Requests an immediate drain and waits (bounded by <paramref name="timeout"/>)
    /// until a pass transfers everything without failure or budget exhaustion.
    /// Returns true when the queue is empty; false on timeout — callers proceed
    /// either way (a huge backlog must never block account removal forever).
    /// </summary>
    public async Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var passTcs = _passCompletedTcs;
                RequestImmediateSync();
                await passTcs.Task.WaitAsync(linked.Token);
                if (_lastPassDrainedAll)
                    return true;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — bounded wait, caller proceeds with the wipe.
        }
        return _lastPassDrainedAll;
    }

    /// <summary>Publishes the outcome of a completed drain pass to <see cref="WaitForDrainAsync"/>.</summary>
    private void CompletePass(bool drainedAll)
    {
        _lastPassDrainedAll = drainedAll;
        var tcs = _passCompletedTcs;
        _passCompletedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult(true);
    }

    /// <summary>
    /// Inspects a sync response body for a server-side device action. Raises
    /// <see cref="OnServerRequestedWipe"/> at most once per process.
    /// </summary>
    private void CheckServerAction(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return;

        SyncBatchResponse? resp;
        try
        {
            resp = JsonSerializer.Deserialize<SyncBatchResponse>(body, SyncResponseJsonOpts);
        }
        catch (JsonException)
        {
            return;
        }

        if (_wipeRequestedFromServer)
            return;

        if (!string.Equals(resp?.Action, "wipe_local", StringComparison.OrdinalIgnoreCase))
            return;

        _wipeRequestedFromServer = true;
        _logger.LogWarning(
            "Server requested a local wipe (device removed) — notifying listeners to wipe local DB + machine id");
        OnServerRequestedWipe?.Invoke();
    }

    /// <summary>
    /// Tells the server this machine's account is being removed (R4/R5, 2026-10-19):
    /// <c>POST /api/v1/devices/self-remove</c> under DeviceAuth. The server marks the
    /// employee <c>untracked</c>, the machine <c>old</c> and advances <c>last_track_at</c>.
    /// Runs AFTER the final drain so the batch that carried the last rows is already
    /// persisted. Returns true when the server acknowledged; false when it could not be
    /// reached (callers proceed with the local wipe regardless — the local wipe is what
    /// the operator asked for).
    /// </summary>
    public async Task<bool> SelfRemoveAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_deviceToken) && string.IsNullOrEmpty(_token))
            return false;

        var serverUrl = _config.ServerUrl ?? "http://localhost:8080";
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{serverUrl}/api/v1/devices/self-remove");
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_deviceToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Device", _deviceToken);
            else
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            ClientIdentityHeaders.Apply(request);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await _httpClient.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Server acknowledged self-remove for this machine");
                return true;
            }

            var errBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Self-remove failed (status {Status}): {Body}",
                (int)response.StatusCode, errBody);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self-remove request failed (server unreachable?)");
            return false;
        }
    }

    // Exponential backoff on failed passes: 5s → 10s → 20s → … → SyncBackoffMaxSec.
    private TimeSpan _backoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LoginPollDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Requests an immediate drain pass. Called by the login flow the moment employee
    /// credentials are persisted — the next pass starts right away instead of waiting
    /// out the idle interval. Safe to call before the service loop is running and from
    /// any thread (the semaphore is 0/1, so a second request while one is pending is a
    /// no-op — one pass covers both).
    /// </summary>
    public void RequestImmediateSync()
    {
        try
        {
            if (_syncSignal.CurrentCount == 0)
                _syncSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown race — nothing to signal.
        }
        catch (SemaphoreFullException)
        {
            // A pass is already pending — one drain covers it.
        }
    }

    /// <summary>
    /// True after at least one sync POST returned HTTP 2xx in this process lifetime
    /// (cleared if credentials are declared dead). Used by <c>WsClient</c> to avoid
    /// opening the control socket while the server is known-unreachable.
    /// </summary>
    public bool IsServerReachable => _serverReachable;

    /// <summary>
    /// Waits until <see cref="IsServerReachable"/> is true, or <paramref name="timeout"/>
    /// elapses (returns without throwing on timeout — callers may still attempt connect
    /// as the idle-employee fallback when nothing was synced).
    /// </summary>
    public async Task WaitUntilServerReachableAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_serverReachable)
            return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            await _serverReachableTcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — caller proceeds (WsClient reconnect fallback).
        }
    }

    private void MarkServerReachable()
    {
        _serverReachable = true;
        _serverReachableTcs.TrySetResult();
    }

    private void ClearServerReachable()
    {
        if (!_serverReachable && !_serverReachableTcs.Task.IsCompleted)
            return;

        _serverReachable = false;
        // Replace the TCS so subsequent waits block until the next 2xx.
        var previous = _serverReachableTcs;
        _serverReachableTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Leave a completed previous TCS alone — waiters already released.
        if (!previous.Task.IsCompleted)
            previous.TrySetCanceled();
    }

    // Built once — the per-table closures capture `this` (fields are read at call time,
    // so employee identity and config stay live across passes).
    private readonly List<Func<Stopwatch, TimeSpan, CancellationToken, Task<bool>>> _drainPass;

    public SyncService(AppConfig config, ILogStore store, ILogger<SyncService> logger, HttpClient httpClient)
    {
        _config = config;
        _store = store;
        _logger = logger;
        _httpClient = httpClient;
        _drainPass = BuildDrainPass();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkSyncWork)
        {
            _logger.LogInformation(
                "SyncService parked (GUI agent — tracker owns sync)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        _logger.LogInformation(
            "SyncService starting (interval={Interval}s, maxRows={MaxRows}, maxBytes={MaxBytes}, compression={Compression})",
            _config.SyncIntervalSec, _config.SyncMaxRows, _config.SyncMaxBytes, _config.SyncCompression);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RefreshEmployeeInfoAsync(stoppingToken))
                {
                    // Not logged in yet — poll until employee credentials persist (Login flow).
                    // Publish a trivially-complete pass: with no credentials there is nothing
                    // to send, so `--remove-account` must not sit out its whole drain timeout
                    // waiting for a pass that can never run (R4).
                    CompletePass(drainedAll: true);
                    await Task.Delay(LoginPollDelay, stoppingToken);
                    continue;
                }

                var passSw = Stopwatch.StartNew();
                var passBudget = TimeSpan.FromSeconds(Math.Max(10, _config.SyncMaxDurationSec));

                // Stamp last_heartbeat_at at the start of every authenticated pass so
                // attendance Last Seen keeps moving even when LogCollector is mid-reboot
                // gap or the collect loop is slow — SyncService only runs when credentials
                // exist, and app-status drains early in the pass (see BuildDrainPass).
                try
                {
                    await _store.SetStatusAsync(
                        "last_heartbeat_at",
                        DateTime.UtcNow.ToString("O"),
                        stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to stamp sync-pass heartbeat");
                }

                bool failed = false;
                bool budgetExhausted = false;
                foreach (var drain in _drainPass)
                {
                    if (passSw.Elapsed >= passBudget) { budgetExhausted = true; break; }
                    if (await drain(passSw, passBudget, stoppingToken))
                        failed = true;
                }

                // Retention cleanup (2026-08-11): after a clean pass, prune rows the server
                // already has and that are no longer needed locally — app_items/app_sessions
                // older than ALPHA_SYNC_RETENTION_HOURS, uninstalled inventory cycles, and
                // superseded network rows. Never runs while a pass had failures (so nothing
                // unsent is ever deleted).
                if (!failed)
                {
                    var cutoff = DateTime.UtcNow.AddHours(-_config.SyncRetentionHours);
                    var deleted = await _store.DeleteSyncedDataOlderThanAsync(cutoff, stoppingToken);
                    if (deleted.Total > 0)
                    {
                        _logger.LogDebug(
                            "Retention cleanup deleted {Total} rows (items={Items}, sessions={Sessions}, apps={Apps}, packages={Packages}, network={Network})",
                            deleted.Total, deleted.AppItems, deleted.AppSessions,
                            deleted.InstalledApps, deleted.InstalledPackages, deleted.NetworkRows);
                    }
                }

                if (failed)
                {
                    _backoff = TimeSpan.FromSeconds(
                        Math.Min(_config.SyncBackoffMaxSec, Math.Max(5, _backoff.TotalSeconds * 2)));
                    _logger.LogDebug("Sync pass had failures — backing off {Backoff}s", _backoff.TotalSeconds);
                }
                else
                {
                    _backoff = TimeSpan.FromSeconds(5);
                }

                // Publish pass completion for --remove-account / self-remove (R4/R6):
                // a pass that neither failed nor ran out of budget drained every table.
                CompletePass(drainedAll: !failed && !budgetExhausted);

                // Between passes: grow the wait on failure (backoff), otherwise the idle interval.
                // The wait is interruptible — RequestImmediateSync() (login) releases the
                // semaphore and this returns at once for an instant drain pass.
                // User rule 2026-08-18: even on repeated failures a drain pass runs at least
                // every 60s — the backoff only stretches a single retry gap, never the
                // guaranteed cadence — so is_synced=0 rows always reach the server within a
                // minute, not after a 5-min backoff.
                var wait = failed
                    ? TimeSpan.FromSeconds(Math.Min(60, _backoff.TotalSeconds))
                    : TimeSpan.FromSeconds(Math.Max(1, _config.SyncIntervalSec));
                await _syncSignal.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sync pass failed unexpectedly");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("SyncService stopped");
    }

    /// <summary>
    /// The per-pass table order — small/inventory tables first, then session_events,
    /// then app_status/permissions (heartbeat must not starve), then inventory +
    /// sessions/items (parents before children).
    /// </summary>
    private List<Func<Stopwatch, TimeSpan, CancellationToken, Task<bool>>> BuildDrainPass()
    {
        var tasks = new List<Func<Stopwatch, TimeSpan, CancellationToken, Task<bool>>>();

        tasks.Add((sw, budget, ct) => DrainTableAsync<DeviceHardwareInfo>(
            "/api/v1/device-hardware/sync",
            (limit, token) => _store.GetUnsentDeviceHardwareInfoAsync(limit, token),
            e => new
            {
                id = e.Id,
                macAddress = e.MacAddress,
                hostname = e.Hostname,
                osName = e.OsName,
                osVersion = e.OsVersion,
                cpuModel = e.CpuModel,
                cpuCores = e.CpuCores,
                ramTotalMb = e.RamTotalMb,
                gpuModel = e.GpuModel,
                gpuVramMb = e.GpuVramMb,
                storageDevices = e.StorageDevices,
                collectedAt = e.CollectedAt.ToString("O"),
            },
            (ids, token) => _store.MarkDeviceHardwareInfoSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<NetworkInfo>(
            "/api/v1/network-info/sync",
            (limit, token) => _store.GetUnsentNetworkInfoAsync(limit, token),
            e => new
            {
                id = e.Id,
                publicIp = e.PublicIp,
                privateIp = e.PrivateIp,
                networkInterfaceName = e.NetworkInterfaceName,
                collectedAt = e.CollectedAt.ToString("O"),
            },
            (ids, token) => _store.MarkNetworkInfoSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        // Storage devices (children of device_hardware_info) — sent to server, never deleted client-side.
        tasks.Add((sw, budget, ct) => DrainTableAsync<StorageDevice>(
            "/api/v1/storage-devices/sync",
            (limit, token) => _store.GetUnsentStorageDevicesAsync(limit, token),
            e => new
            {
                id = e.Id,
                deviceHardwareId = e.DeviceHardwareId,
                deviceType = e.DeviceType,
                model = e.Model,
                capacityMb = e.CapacityMb,
            },
            (ids, token) => _store.MarkStorageDevicesSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        // Hardware devices (USB/peripheral hotplug) — sent to server, never deleted client-side.
        tasks.Add((sw, budget, ct) => DrainTableAsync<HardwareDevice>(
            "/api/v1/hardware-devices/sync",
            (limit, token) => _store.GetUnsentHardwareDevicesAsync(limit, token),
            e => new
            {
                id = e.Id,
                deviceClass = e.DeviceClass,
                vendor = e.Vendor,
                product = e.Product,
                serial = e.Serial,
                busPath = e.BusPath,
                deviceNode = e.DeviceNode,
                pluggedAt = e.PluggedAt.ToString("O"),
                unpluggedAt = e.UnpluggedAt?.ToString("O"),
            },
            (ids, token) => _store.MarkHardwareDevicesSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<LocationSample>(
            "/api/v1/location-samples/sync",
            (limit, token) => _store.GetUnsentLocationSamplesAsync(limit, token),
            e => new
            {
                id = e.Id,
                latitude = e.Latitude,
                longitude = e.Longitude,
                accuracyM = e.AccuracyM,
                altitudeM = e.AltitudeM,
                source = e.Source,
                address = e.Address,
                capturedAt = e.CapturedAt.ToString("O"),
            },
            (ids, token) => _store.MarkLocationSamplesSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainSessionEventsAsync(sw, budget, ct));

        // App status (key/value) — drain EARLY so last_heartbeat_at (stamped every
        // pass) is not starved behind large app_sessions / app_items backlogs.
        // Attendance Last Seen depends on this row reaching the server.
        tasks.Add((sw, budget, ct) => DrainTableAsync<AppStatus>(
            "/api/v1/app-status/sync",
            (limit, token) => _store.GetUnsentAppStatusAsync(limit, token),
            e => new
            {
                key = e.Key,
                value = e.Value,
                updatedAt = e.UpdatedAt,
            },
            (ids, token) => _store.MarkAppStatusSentAsync(ids, token),
            e => e.Key,
            sw, budget, ct));

        // Permission status — one row per permission method (deduped 2026-08-11); sent to server, never deleted client-side.
        tasks.Add((sw, budget, ct) => DrainTableAsync<PermissionStatus>(
            "/api/v1/permission-status/sync",
            (limit, token) => _store.GetUnsentPermissionStatusAsync(limit, token),
            e => new
            {
                checkId = e.CheckId,
                sessionId = e.SessionId,
                sessionType = e.SessionType,
                platform = e.Platform,
                checkedAt = e.CheckedAt,
                method = e.Method,
                works = e.Works,
                details = e.Details,
            },
            (ids, token) => _store.MarkPermissionStatusSentAsync(ids, token),
            e => e.CheckId,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<InstalledApplication>(
            "/api/v1/installed-apps/sync",
            (limit, token) => _store.GetUnsentInstalledApplicationsAsync(limit, token),
            e => new
            {
                id = e.Id,
                appName = e.AppName,
                appVersion = e.AppVersion,
                publisher = e.Publisher,
                installPath = e.InstallPath,
                installDate = e.InstallDate?.ToString("O"),
                uninstallString = e.UninstallString,
                changeType = e.ChangeType,
                detectedAt = e.DetectedAt.ToString("O"),
                binaryName = e.BinaryName,
                isBrowser = e.IsBrowser,
                desktopId = e.DesktopId,
                categories = e.Categories,
            },
            (ids, token) => _store.MarkInstalledApplicationsSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<InstalledPackage>(
            "/api/v1/installed-packages/sync",
            (limit, token) => _store.GetUnsentInstalledPackagesAsync(limit, token),
            e => new
            {
                id = e.Id,
                packageName = e.PackageName,
                version = e.Version,
                category = e.Category,
                sourceManager = e.SourceManager,
                installPath = e.InstallPath,
                publisher = e.Publisher,
                description = e.Description,
                detectedAt = e.DetectedAt.ToString("O"),
            },
            (ids, token) => _store.MarkInstalledPackagesSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<AppSession>(
            "/api/v1/app-sessions/sync",
            (limit, token) => _store.GetUnsentAppSessionsAsync(limit, token),
            e => new
            {
                id = e.Id,
                processName = e.ProcessName,
                appDisplayName = e.AppDisplayName,
                startedAt = e.StartedAt.ToString("O"),
                endedAt = e.EndedAt?.ToString("O"),
                machineId = e.MachineId,
                employeeId = _employeeId,
                employeeName = _employeeName,
                sessionId = e.SessionId,
                platform = e.Platform,
                installedAppId = e.InstalledAppId,
                installedPackageId = e.InstalledPackageId,
                processId = e.ProcessId,
                parentProcessId = e.ParentProcessId,
                groupedBy = e.GroupedBy,
                cgroupScope = e.CgroupScope,
                contextLabel = e.ContextLabel,
                foregroundSeconds = e.ForegroundSeconds ?? 0,
                backgroundSeconds = e.BackgroundSeconds ?? 0,
                // 3-state lifecycle (2026-09-02): the server sweeper
                // uses this to flip ACTIVE → STALE → CLOSED. Default to
                // the sync moment when the client row predates the
                // migration so older builds still produce a valid row.
                lastActivityAt = (e.LastActivityAt ?? e.StartedAt).ToString("O"),
            },
            (ids, token) => _store.MarkAppSessionsSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<AppItem>(
            "/api/v1/app-items/sync",
            (limit, token) => _store.GetUnsentAppItemsAsync(limit, token),
            e => new
            {
                id = e.Id,
                appSessionId = e.AppSessionId,
                parentItemId = e.ParentItemId,
                itemType = e.ItemType,
                title = e.Title,
                identifier = e.Identifier,
                url = e.Url,
                domain = e.Domain,
                openedAt = e.OpenedAt.ToString("O"),
                closedAt = e.ClosedAt?.ToString("O"),
                processId = e.ProcessId,
                objectType = e.ObjectType,
                action = e.Action,
                journeyId = e.JourneyId,
                sequence = e.Sequence,
                previousPath = e.PreviousPath,
                currentPath = e.CurrentPath,
                windowId = e.WindowId,
                tabId = e.TabId,
                metadataJson = e.MetadataJson,
            },
            (ids, token) => _store.MarkAppItemsSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct,
            filterIdsFn: QuarantinedAppItemIds,
            onRejection: OnAppItemsRejected,
            onMissingSessions: missing => _ = ResetSessionsForRequeueAsync(missing)));

        return tasks;
    }

    /// <summary>
    /// Drains session_events with 5-minute bucket aggregation (S1 / A.9). Raw rows stay in
    /// SQLite until their bucket window closes; the sync payload carries count/firstAt/lastAt.
    /// Applies the S6 local row ceiling before each fetch.
    /// </summary>
    private async Task<bool> DrainSessionEventsAsync(
        Stopwatch passSw,
        TimeSpan passBudget,
        CancellationToken ct)
    {
        const string endpoint = "/api/v1/session-events/sync";
        var synced = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (passSw.Elapsed >= passBudget) break;

                var rolled = await _store.RollupExcessUnsentSessionEventsAsync(_config.TaMaxLocalRows, ct);
                if (rolled > 0)
                {
                    _logger.LogWarning(
                        "Rolled up {Count} excess unsynced session_events rows into old_data_dropped sentinel",
                        rolled);
                }

                var entries = await _store.GetUnsentSessionEventsAsync(_config.SyncMaxRows, ct);
                if (entries.Count == 0) break;

                var sourceById = entries.ToDictionary(e => e.Id);
                var aggregates = SessionEventSyncAggregator.BuildAggregates(
                    entries,
                    _config.EventAggregationWindowSec,
                    DateTime.UtcNow);

                if (aggregates.Count == 0)
                    break;

                var remaining = aggregates;
                while (remaining.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (passSw.Elapsed >= passBudget) break;

                    var slice = remaining;
                    byte[] json;
                    while (true)
                    {
                        var payload = new
                        {
                            employeeId = _employeeId,
                            token = _token,
                            entries = slice.Select(agg => MapSessionEventAggregate(agg, sourceById)).ToList()
                        };
                        json = JsonSerializer.SerializeToUtf8Bytes(payload);
                        if (json.Length <= _config.SyncMaxBytes || slice.Count <= 1) break;
                        slice = slice.Take(slice.Count / 2).ToList();
                    }

                    var (sentOk, body) = await SendAsync(endpoint, json, ct);
                    if (!sentOk)
                        return true;

                    CheckServerAction(body);

                    var ids = slice.SelectMany(a => a.SourceIds).Distinct().ToList();
                    if (ids.Count > 0)
                        await _store.MarkSessionEventsSentAsync(ids, ct);
                    synced += slice.Count;

                    remaining = slice.Count < remaining.Count
                        ? remaining.Skip(slice.Count).ToList()
                        : Array.Empty<SessionEventAggregate>();

                    if (_config.SyncChunkDelayMs > 0)
                        await Task.Delay(_config.SyncChunkDelayMs, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error draining {Endpoint}", endpoint);
            return true;
        }

        if (synced > 0)
            _logger.LogDebug("Synced {Count} session_event aggregates to {Endpoint}", synced, endpoint);
        return false;
    }

    private static object MapSessionEventAggregate(
        SessionEventAggregate agg,
        IReadOnlyDictionary<string, SessionEvent> sourceById)
    {
        if (sourceById.TryGetValue(agg.SyncId, out var source)
            && source.EventType == SessionEventTypes.OldDataDropped
            && source.EventCount is > 0)
        {
            var firstAt = source.FirstAt ?? source.EventAt;
            var lastAt = source.LastAt ?? source.EventAt;
            return new
            {
                id = agg.SyncId,
                eventType = agg.EventType,
                osUsername = agg.OsUsername,
                eventAt = firstAt.ToString("O"),
                count = source.EventCount.Value,
                firstAt = firstAt.ToString("O"),
                lastAt = lastAt.ToString("O"),
            };
        }

        return new
        {
            id = agg.SyncId,
            eventType = agg.EventType,
            osUsername = agg.OsUsername,
            eventAt = agg.EventAt.ToString("O"),
            count = agg.Count,
            firstAt = agg.FirstAt.ToString("O"),
            lastAt = agg.LastAt.ToString("O"),
        };
    }

    /// <summary>
    /// Drains ONE table: fetches a row-capped chunk, splits it into byte-bounded slices,
    /// sends each slice, marks it sent, pauses politely, and repeats until the table is
    /// drained or the pass budget expires. Returns true if any send failed (triggers
    /// exponential backoff — the next pass resumes where the failed chunk left off).
    ///
    /// Optional hooks (used by the app-items drain):
    ///  - <paramref name="filterIdsFn"/> returns ids to SKIP before sending (quarantined rows).
    ///  - <paramref name="onRejection"/> is called when the server's response carries
    ///    rejectedIds — only ACCEPTED rows are marked sent; refused rows stay is_synced=0
    ///    and re-send on the next pass. A null/absent rejectedIds field (older server)
    ///    preserves the mark-all behavior.
    /// </summary>
    private async Task<bool> DrainTableAsync<T>(
        string endpoint,
        Func<int, CancellationToken, Task<IReadOnlyList<T>>> fetchFn,
        Func<T, object> mapFn,
        Func<IReadOnlyList<string>, CancellationToken, Task> markSentFn,
        Func<T, string?> idOf,
        Stopwatch passSw,
        TimeSpan passBudget,
        CancellationToken ct,
        Func<IReadOnlyList<string>>? filterIdsFn = null,
        Action<IReadOnlyList<string>, IReadOnlyList<string>>? onRejection = null,
        Action<IReadOnlyList<string>>? onMissingSessions = null)
    {
        var synced = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (passSw.Elapsed >= passBudget) break;

                var entries = await fetchFn(_config.SyncMaxRows, ct);
                if (entries.Count == 0) break;

                // Quarantine skip (app-items): rows the server keeps refusing never enter
                // the payload. They stay is_synced=0, so every fetch re-returns them at the
                // head — filtering here (not in SQL) keeps fresh rows behind them draining;
                // when ONLY quarantined rows remain, break until the next pass.
                if (filterIdsFn != null)
                {
                    var skip = filterIdsFn();
                    if (skip.Count > 0)
                    {
                        var skipSet = new HashSet<string>(skip);
                        entries = entries.Where(e => !skipSet.Contains(idOf(e) ?? "")).ToList();
                        if (entries.Count == 0) break;
                    }
                }

                var remaining = entries;
                while (remaining.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (passSw.Elapsed >= passBudget) break;

                    // Byte-bound the slice: serialize, and if it exceeds the cap, halve until
                    // it fits (rare — only fires when individual rows are very large).
                    var slice = remaining;
                    byte[] json;
                    while (true)
                    {
                        var payload = new
                        {
                            employeeId = _employeeId,
                            token = _token,
                            entries = slice.Select(mapFn).ToList()
                        };
                        json = JsonSerializer.SerializeToUtf8Bytes(payload);
                        if (json.Length <= _config.SyncMaxBytes || slice.Count <= 1) break;
                        slice = slice.Take(slice.Count / 2).ToList();
                    }

                    var (ok, body) = await SendAsync(endpoint, json, ct);
                    if (!ok)
                        return true; // failed — stop this table, back off, resume next pass

                    // R6: the server may piggyback a device action (wipe_local) on any
                    // successful sync — the rows in this slice are already persisted.
                    CheckServerAction(body);

                    var ids = slice.Select(idOf).Where(id => !string.IsNullOrEmpty(id)).Cast<string>().ToList();

                    // Partial acceptance: the server may explicitly REFUSE rows (rejectedIds
                    // — orphan app_session_id preflight). Mark only the accepted rows so the
                    // refused ones stay is_synced=0 and re-send next pass (their parent
                    // session lands in between — sessions drain before items). An absent or
                    // unparseable rejectedIds field (older server, non-JSON body) keeps the
                    // mark-all behavior — the upserts are idempotent either way.
                    List<string>? rejectedIds = null;
                    if (onRejection != null && ids.Count > 0 &&
                        TryParseRejectedIds(body, out var parsed) &&
                        (rejectedIds = parsed.Where(ids.Contains).ToList()).Count > 0)
                    {
                        var rejectedSet = new HashSet<string>(rejectedIds);
                        var accepted = ids.Where(id => !rejectedSet.Contains(id)).ToList();
                        if (accepted.Count > 0)
                            await markSentFn(accepted, ct);
                        synced += accepted.Count;

                        // Bug #9 follow-up: parse missing session IDs BEFORE calling
                        // onRejection so OnAppItemsRejected can skip quarantine for
                        // items whose parent is being re-queued.
                        List<string>? missingSessions = null;
                        if (onMissingSessions != null &&
                            TryParseMissingSessionIds(body, out var missing) &&
                            missing.Count > 0)
                        {
                            missingSessions = missing;
                            _lastMissingSessionIds = missing;
                        }
                        else
                        {
                            _lastMissingSessionIds = new List<string>();
                        }

                        onRejection(rejectedIds, accepted);
                        _logger.LogWarning(
                            "Server rejected {Rejected} of {Sent} rows for {Endpoint} (orphan preflight) — kept unsent for re-send next pass",
                            rejectedIds.Count, ids.Count, endpoint);

                        // Fire the missing-sessions callback after rejection handling
                        // so the client resets parent sessions for re-queue.
                        if (missingSessions != null)
                        {
                            onMissingSessions!(missingSessions);
                        }
                    }
                    else if (ids.Count > 0)
                    {
                        await markSentFn(ids, ct);
                        synced += ids.Count;
                    }

                    remaining = slice.Count < remaining.Count
                        ? remaining.Skip(slice.Count).ToList()
                        : Array.Empty<T>();

                    if (_config.SyncChunkDelayMs > 0)
                        await Task.Delay(_config.SyncChunkDelayMs, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error draining {Endpoint}", endpoint);
            return true;
        }

        if (synced > 0)
            _logger.LogDebug("Synced {Count} rows to {Endpoint}", synced, endpoint);
        return false;
    }

    /// <summary>
    /// Parses the sync response body for rejectedIds. Returns false when the field is
    /// absent or the body is not the expected JSON — both mean "mark everything sent"
    /// (older-server compatibility; idempotent upserts make that safe).
    /// </summary>
    private static bool TryParseRejectedIds(string? body, out List<string> rejectedIds)
    {
        rejectedIds = new List<string>();
        if (string.IsNullOrEmpty(body)) return false;
        try
        {
            var resp = JsonSerializer.Deserialize<SyncBatchResponse>(body, SyncResponseJsonOpts);
            if (resp?.RejectedIds is { Count: > 0 })
            {
                rejectedIds = resp.RejectedIds;
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseMissingSessionIds(string? body, out List<string> missingSessionIds)
    {
        missingSessionIds = new List<string>();
        if (string.IsNullOrEmpty(body)) return false;
        try
        {
            var resp = JsonSerializer.Deserialize<SyncBatchResponse>(body, SyncResponseJsonOpts);
            if (resp?.MissingSessionIds is { Count: > 0 })
            {
                missingSessionIds = resp.MissingSessionIds;
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Ids currently quarantined (refused ≥ QuarantineRetries consecutive passes).</summary>
    private IReadOnlyList<string> QuarantinedAppItemIds()
        => _appItemRejectCounts
            .Where(kv => kv.Value >= QuarantineRetries)
            .Select(kv => kv.Key)
            .ToList();

    /// <summary>
    /// Quarantine bookkeeping for the app-items drain: refused ids advance their
    /// consecutive-refusal counter (≥ threshold ⇒ skipped by future drains until
    /// restart); accepted ids are FORGIVEN so a self-healed row starts fresh if it
    /// is ever orphaned again. All refusals count — even when the server reports
    /// missingSessionIds, a repeated refusal after multiple re-queue attempts means
    /// the parent session is genuinely gone (deleted by retention, never synced, etc.)
    /// and retrying forever wastes bandwidth and fills server logs.
    /// </summary>
    private void OnAppItemsRejected(IReadOnlyList<string> rejectedIds, IReadOnlyList<string> acceptedIds)
    {
        foreach (var id in acceptedIds)
            _appItemRejectCounts.Remove(id);
        foreach (var id in rejectedIds)
        {
            _appItemRejectCounts[id] = _appItemRejectCounts.GetValueOrDefault(id) + 1;
            if (_appItemRejectCounts[id] == QuarantineRetries)
                _logger.LogWarning(
                    "app_item {Id} refused {Retries} consecutive passes — quarantined in-memory (row stays is_synced=0; retried after restart)",
                    id, QuarantineRetries);
        }
    }

    /// <summary>
    /// Tracks the most recently reported missing session IDs so that OnAppItemsRejected
    /// can skip quarantine for items whose parent is being re-queued.
    /// </summary>
    private List<string> _lastMissingSessionIds = new();

    /// <summary>
    /// Reset specific sessions to is_synced=0 so they are re-sent on the next sync pass.
    /// Called when the server reports missing session IDs during the orphan preflight
    /// (Bug #9 follow-up — breaks the permanent orphan deadlock).
    /// </summary>
    private async Task ResetSessionsForRequeueAsync(IReadOnlyList<string> missingSessionIds)
    {
        if (missingSessionIds.Count == 0) return;
        _lastMissingSessionIds = missingSessionIds.ToList();
        try
        {
            await _store.MarkAppSessionsUnsyncedByIdsAsync(missingSessionIds, CancellationToken.None);
            _logger.LogWarning(
                "Server reported {Count} missing session IDs — resetting to is_synced=0 for re-queue (deadlock fix)",
                missingSessionIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset missing sessions for re-queue");
        }
    }

    /// <summary>
    /// POSTs one serialized slice. Request bodies are gzip-compressed when enabled (tiny
    /// payloads are skipped — compression overhead isn't worth it below ~512 bytes).
    /// Per-request timeout is bound via a linked CTS (the shared HttpClient.Timeout is the
    /// hard ceiling). Returns (true, body) on 2xx — the body carries the server's
    /// SyncBatchResponse (synced count + optional rejectedIds). 2xx = the rows in this
    /// slice are durably handled server-side (idempotent upserts by client GUID), minus
    /// any rows the server explicitly refused via rejectedIds.
    /// </summary>
    private async Task<(bool Success, string? Body)> SendAsync(string endpoint, byte[] json, CancellationToken ct)
    {
        var serverUrl = _config.ServerUrl ?? "http://localhost:8080";
        try
        {
            HttpContent content;
            if (_config.SyncCompression && json.Length > 512)
            {
                await using var ms = new MemoryStream();
                await using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    await gz.WriteAsync(json, ct);
                content = new ByteArrayContent(ms.ToArray());
                content.Headers.ContentEncoding.Add("gzip");
            }
            else
            {
                content = new ByteArrayContent(json);
            }
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}{endpoint}")
            {
                Content = content
            };

            if (!string.IsNullOrEmpty(_deviceToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Device", _deviceToken);
            }
            else if (!string.IsNullOrEmpty(_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            }
            ClientIdentityHeaders.Apply(request);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await _httpClient.SendAsync(request, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                // A sync that succeeded with the device token proves the device record
                // is still live on the server — clear the "auth looks dead" state if a
                // previous 401 trip had set it.
                _authLooksDead = false;
                MarkServerReachable();
                var body = await response.Content.ReadAsStringAsync(ct);
                return (true, body);
            }

            if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
            {
                _logger.LogWarning("Auth failed (status {Status}) for {Endpoint}", (int)response.StatusCode, endpoint);
                await HandleAuthFailureAsync(endpoint, ct);
            }
            else
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Sync failed for {Endpoint} (status {Status}): {Body}",
                    endpoint, (int)response.StatusCode, errBody);
            }
            return (false, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Sync failed for {Endpoint} (server unreachable)", endpoint);
            return (false, null);
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Sync timed out for {Endpoint}", endpoint);
            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync {Endpoint}", endpoint);
            return (false, null);
        }
    }

    private async Task<bool> RefreshEmployeeInfoAsync(CancellationToken ct)
    {
        try
        {
            var info = await _store.GetEmployeeInfoAsync(ct);
            if (info == null || string.IsNullOrEmpty(info.Token))
            {
                _employeeId = _employeeName = _token = _deviceToken = null;
                _authLooksDead = false;
                return false;
            }
            _employeeId = info.EmployeeId;
            _employeeName = info.Name;
            _token = info.Token;
            _deviceToken = info.DeviceToken;
            // Login writes device.auth. A later start only restored the SQLite token, so
            // protected uninstall failed when that file was missing. Never publish empty:
            // Publish("") deletes a good file.
            if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(info.DeviceToken))
                DlpDeviceCredentialBridge.Publish(info.DeviceToken, info.EmployeeId, _config.ServerUrl);
            // Fresh credentials from SQLite — clear the dead state so the next
            // 401 (if any) is treated as a fresh first failure, not a continuation.
            _authLooksDead = false;
            _lastAuthFailureAt = DateTime.MinValue;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Called when a sync endpoint returns 401/403. The server's DeviceAuth middleware
    /// rejects the credential for one of three reasons: (1) the device record was
    /// revoked by a re-login from another client, (2) the JWT is malformed or expired,
    /// (3) the server was restarted and lost in-memory state. The first two are
    /// recoverable on the NEXT pass by falling back from Device to Bearer (or vice
    /// versa); only persistent rejections trigger the "please re-login" callback.
    /// </summary>
    private async Task HandleAuthFailureAsync(string endpoint, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // If we were using the device token and just got 401, drop it so the next
        // request tries the Bearer JWT (which is 360h long-lived and only dies on a
        // server restart with the in-memory signing key gone). One Device→Bearer
        // rotation per failure window.
        if (!string.IsNullOrEmpty(_deviceToken))
        {
            _logger.LogInformation(
                "Device token rejected for {Endpoint} — falling back to Bearer JWT on next pass",
                endpoint);
            _deviceToken = null;
            _lastAuthFailureAt = now;
            return;
        }

        // We were already on the Bearer JWT and STILL got 401. This is the
        // conclusive "credentials are dead" state — only reached when both the
        // device record AND the JWT are invalid (e.g. server restart wiped
        // in-memory state, or the employee was deleted). Mark dead and fire the
        // event so the UI can prompt for re-login. Only fire the event ONCE per
        // dead-credential episode — otherwise every subsequent 401 logs and pings
        // the UI again.
        if (!string.IsNullOrEmpty(_token) && (now - _lastAuthFailureAt) < AuthGiveUpWindow)
        {
            if (!_authLooksDead)
            {
                _authLooksDead = true;
                ClearServerReachable();
                _logger.LogWarning(
                    "Both Device token and Bearer JWT rejected for {Endpoint} within {Window}s — credentials are dead. " +
                    "Open the GUI and log in again.",
                    endpoint, (int)AuthGiveUpWindow.TotalSeconds);
                try { OnAuthCredentialsDead?.Invoke(); }
                catch (Exception ex) { _logger.LogDebug(ex, "OnAuthCredentialsDead handler threw"); }
            }
            return;
        }

        // Reset the failure window so a single isolated 401 doesn't accumulate into
        // the "dead" verdict.
        _lastAuthFailureAt = now;
        await Task.CompletedTask;
    }
}
