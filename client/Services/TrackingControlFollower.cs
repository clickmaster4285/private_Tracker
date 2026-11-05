using client.Core;
using client.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Offline Roaming Start/Stop follower. Honors <see cref="LocalRoamingControl"/> and
/// single-instance TRACKING_START / TRACKING_STOP. Does not require the DLP service
/// or the server for Pause/Start. Optional best-effort sync on Stop.
/// </summary>
public sealed class TrackingControlFollower : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogStore _store;
    private readonly SyncService _sync;
    private readonly LogCollectorService _collector;
    private readonly TrackingEventReporter _events;
    private readonly WsClient _ws;
    private readonly LiveStreamClient _liveStream;
    private readonly ILogger<TrackingControlFollower> _logger;
    private int _stopInFlight;
    private volatile bool _paused;

    public TrackingControlFollower(
        IServiceProvider services,
        ILogStore store,
        SyncService sync,
        LogCollectorService collector,
        TrackingEventReporter events,
        WsClient ws,
        LiveStreamClient liveStream,
        ILogger<TrackingControlFollower> logger)
    {
        _services = services;
        _store = store;
        _sync = sync;
        _collector = collector;
        _events = events;
        _ws = ws;
        _liveStream = liveStream;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ClientProcessMode.GuiAgent || ClientProcessMode.RunMode != TrackingRunMode.Full)
            return;

        // Instant Start/Stop from the offline modal (same machine, no DLP pipe).
        SingleInstanceService.OnTrackingStartRequested = () =>
        {
            try { HandleStart(); }
            catch (Exception ex) { _logger.LogWarning(ex, "TRACKING_START failed"); }
        };
        SingleInstanceService.OnTrackingStopRequested = () =>
        {
            _ = Task.Run(async () =>
            {
                try { await HandleStopAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "TRACKING_STOP failed"); }
            });
        };

        // Boot: if local desired is stopped, park collectors immediately.
        try
        {
            var local = LocalRoamingControl.ReadOrDefault();
            if (IsRoaming() &&
                string.Equals(local.DesiredState, "stopped", StringComparison.OrdinalIgnoreCase))
            {
                _collector.PauseTracking();
                _ws.ForceDisconnect();
                _liveStream.ForceDisconnect();
                _paused = true;
                _logger.LogInformation("Roaming boot: local desired=stopped — collectors/WS/WebRTC paused");
            }
            else if (IsRoaming() &&
                     string.Equals(local.DesiredState, "running", StringComparison.OrdinalIgnoreCase))
            {
                // Resume collectors after reboot — not a user Start click; skip audit row.
                HandleStart(reportEvent: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Roaming boot local state apply failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                if (!IsRoaming())
                    continue;

                var local = LocalRoamingControl.ReadOrDefault();
                var desired = (local.DesiredState ?? "stopped").Trim().ToLowerInvariant();

                if (desired == "stopped" && !_paused &&
                    Interlocked.CompareExchange(ref _stopInFlight, 1, 0) == 0)
                {
                    await HandleStopAsync(stoppingToken).ConfigureAwait(false);
                }
                else if (desired == "running" && _paused)
                {
                    HandleStart();
                }

                // Retry any Start/Stop rows that failed while offline.
                _ = _events.FlushPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "TrackingControlFollower tick failed");
            }
        }
    }

    private void HandleStart(bool reportEvent = true)
    {
        if (!IsRoaming())
            return;

        var state = LocalRoamingControl.ReadOrDefault();
        if (!string.Equals(state.DesiredState, "running", StringComparison.OrdinalIgnoreCase))
            LocalRoamingControl.WriteStart();

        _collector.StartTracking();
        _ws.ResumeConnections();
        _liveStream.ResumeConnections();
        _paused = false;
        LocalRoamingControl.WritePhase("collecting");
        _logger.LogInformation("Roaming Start — collectors + WS/WebRTC running (offline)");

        if (reportEvent)
            _ = _events.ReportAsync("start"); // queued locally; flushed when DeviceAuth is available
    }

    private async Task HandleStopAsync(CancellationToken ct)
    {
        try
        {
            _collector.PauseTracking();
            _ws.ForceDisconnect();
            _liveStream.ForceDisconnect();
            _paused = true;
            LocalRoamingControl.WritePhase("idle");

            try
            {
                var watcher = _services.GetService<Watchers.SystemEventWatcher>();
                if (watcher is not null)
                    await watcher.CloseAllOpenSessionsOnShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Close open sessions during Roaming Stop failed");
            }

            // Best-effort drain — must not block Stop on server/service.
            var flushResult = "partial";
            try
            {
                using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                drainCts.CancelAfter(TimeSpan.FromSeconds(20));
                var drained = await _sync.WaitForDrainAsync(TimeSpan.FromSeconds(15), drainCts.Token)
                    .ConfigureAwait(false);
                flushResult = drained ? "complete" : "partial";
                _logger.LogInformation("Roaming Stop drain {State}", drained ? "ok" : "skipped/timeout");
                try
                {
                    await _store.PurgeSyncedAsync(ct).ConfigureAwait(false);
                }
                catch { /* keep unsynced */ }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Optional stop drain failed — tracking already paused");
                flushResult = "partial";
            }

            // Always record the Stop click (even when drain failed / offline).
            _ = _events.ReportAsync("stop", reason: "user", flushResult: flushResult, ct: ct);

            _logger.LogInformation("Roaming Stop — collectors paused (offline)");
        }
        finally
        {
            Interlocked.Exchange(ref _stopInFlight, 0);
        }
    }

    private static bool IsRoaming()
    {
        try
        {
            var path = TrackingControlPaths.ControlJsonPath;
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var fallback = OperatingSystem.IsWindows() ? "fixed" : "roaming";
                var mode = (root.TryGetProperty("mode", out var m) || root.TryGetProperty("trackingMode", out m)
                    ? m.GetString() : fallback) ?? fallback;
                return string.Equals(mode, "roaming", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* fall through */ }

        // No server control file yet: offline local-roaming.json still enables Start/Stop.
        return File.Exists(LocalRoamingControl.FilePath);
    }
}
