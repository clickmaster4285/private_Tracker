using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;

namespace client.Services;

/// <summary>
/// The single implementation of the account-removal routine (R4/R5/R6, 2026-10-19).
///
/// Before this service existed the "stop tracking → drain → wipe → kill siblings"
/// sequence was duplicated in four places in <c>Program.cs</c> / <c>MainViewModel.cs</c>
/// and none of them actually waited for the drain or told the server. This class owns
/// the canonical order:
///
///   1. <b>Stop collection</b> so nothing new is written while we drain / wipe
///      (R-WipeRace mitigation in the plan).
///   2. <b>Drain</b> every unsynced row to the server, bounded by
///      <see cref="AppConfig.RemoveAccountSyncTimeoutSec"/> so a huge backlog or an
///      offline machine can never block removal forever.
///   3. <b>Self-remove</b> — <c>POST /api/v1/devices/self-remove</c> under DeviceAuth,
///      which makes the server mark the employee <c>untracked</c>, this machine
///      <c>old</c>, and advance <c>last_track_at</c> (R5).
///   4. <b>Wipe local state</b> — empty the SQLite DB and delete <c>.machine-id</c> /
///      device id files, then reset the in-memory client id so the next login mints a
///      brand-new machine id (R7).
///
/// The same routine serves the server-initiated path: when a sync response carries
/// <c>{"action":"wipe_local"}</c> (an admin removed this device — R6), SyncService raises
/// <see cref="SyncService.OnServerRequestedWipe"/> and the listener calls
/// <see cref="RunAsync"/>. Idempotent: a second call after a completed wipe is a no-op.
/// </summary>
public class AccountRemovalService
{
    private readonly ILogStore _store;
    private readonly SyncService _sync;
    private readonly AppConfig _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<AccountRemovalService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _wiped;

    /// <summary>True once the server acknowledged the self-remove call in this process.</summary>
    public bool ServerNotified { get; private set; }

    /// <summary>
    /// Raised after the local DB + identifiers have been wiped. Hosts listen to switch
    /// the UI back to the sign-in page (GUI) or to surface the sign-in window
    /// (background/headless mode).
    /// </summary>
    public event Action? WipeCompleted;

    public AccountRemovalService(
        ILogStore store,
        SyncService sync,
        AppConfig config,
        IServiceProvider services,
        ILogger<AccountRemovalService> logger)
    {
        _store = store;
        _sync = sync;
        _config = config;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full removal routine. Returns true when the server acknowledged the
    /// self-remove; the local wipe happens regardless (the operator asked for it).
    /// <paramref name="progress"/> receives phase keys: <c>stop</c>, <c>sync</c>, <c>status</c>, <c>wipe</c>.
    /// Always mirrors phases to <see cref="RemovalProgress"/> so a CLI can poll.
    /// </summary>
    /// <param name="fixedModeCodeVerified">
    /// Unused (kept for call-site compatibility). Plain <c>--remove-account</c> always
    /// drains (bounded) then wipes — Fixed mode no longer blocks local account removal.
    /// Product uninstall still requires <c>--protected-uninstall --code</c>.
    /// </param>
    /// <param name="allowServerInitiated">
    /// Unused for gating (kept for call-site compatibility with admin <c>wipe_local</c>).
    /// </param>
    public async Task<bool> RunAsync(
        CancellationToken ct = default,
        Action<string>? progress = null,
        bool fixedModeCodeVerified = false,
        bool allowServerInitiated = false)
    {
        _ = fixedModeCodeVerified;
        _ = allowServerInitiated;

        await _gate.WaitAsync(ct);
        try
        {
            if (_wiped)
                return ServerNotified;

            void Report(string phase)
            {
                var label = phase switch
                {
                    "stop" => "Stopping collectors",
                    "sync" => "Syncing unsynced data",
                    "status" => "Updating server status",
                    "wipe" => "Wiping local account",
                    _ => phase,
                };
                RemovalProgress.Write(phase, label);
                progress?.Invoke(phase);
            }

            Report("stop");
            StopCollection();

            // Online: push unsynced rows first. Offline / dead server: bounded wait then
            // wipe anyway — local remove must never hang forever.
            Report("sync");
            var timeout = TimeSpan.FromSeconds(
                Math.Clamp(_config.RemoveAccountSyncTimeoutSec, 5, 45));
            var drained = await _sync.WaitForDrainAsync(timeout, ct);
            _logger.LogInformation(
                "Account removal: final sync drain {State} (timeout {Timeout}s)",
                drained ? "completed" : "timed out / offline — proceeding with local wipe",
                timeout.TotalSeconds);

            Report("status");
            ServerNotified = await _sync.SelfRemoveAsync(ct);

            Report("wipe");
            await WipeLocalAsync();
            _wiped = true;

            // Do NOT write phase=done here — protected-uninstall still needs DLP teardown.
            // Callers (--remove-account) write done after RunAsync returns.
            _logger.LogInformation("Account removal complete (serverNotified={Notified})", ServerNotified);

            try
            {
                WipeCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WipeCompleted handler threw");
            }

            return ServerNotified;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the collectors so no new rows are written during the wipe. Resolved lazily:
    /// the terms-only host (agent mode) does not register <see cref="LogCollectorService"/>.
    /// </summary>
    private void StopCollection()
    {
        try
        {
            _services.GetService<LogCollectorService>()?.StopTracking();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StopTracking during account removal failed");
        }
    }

    /// <summary>
    /// Empties the local database (every table, read from sqlite_master so future
    /// tables are covered automatically) and deletes the machine / device identifiers
    /// so the next login generates a fresh machine id (R7).
    ///
    /// Awaited (never blocked on): the GUI path runs on the Avalonia UI thread, and a
    /// blocking `.GetAwaiter().GetResult()` around the store's async command would
    /// deadlock if any continuation needed that thread.
    /// </summary>
    private async Task WipeLocalAsync()
    {
        try
        {
            await _store.WipeLocalAccountDataAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local database wipe failed");
        }

        try
        {
            AppConfig.RemoveMachineAndDeviceIdentifiers();
            _config.ResetMachineId();
            EncryptedConfigService.ResetCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing machine / device identifiers failed");
        }
    }
}
