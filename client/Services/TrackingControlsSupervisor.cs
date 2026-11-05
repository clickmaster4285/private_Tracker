using System.Diagnostics;
using client.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Linux: keep the roaming Start/Stop modal (<c>--tracking-controls</c>) alive.
/// Windows uses LocalSystem <c>TrackerWatchdogWorker</c>; the root DLP daemon must
/// not spawn Wayland UI, so the user-session <c>--background</c> tracker owns this.
/// </summary>
public sealed class TrackingControlsSupervisor : BackgroundService
{
    private readonly ILogger<TrackingControlsSupervisor> _logger;
    private DateTimeOffset _lastLaunchAttempt = DateTimeOffset.MinValue;

    public TrackingControlsSupervisor(ILogger<TrackingControlsSupervisor> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogInformation("TrackingControlsSupervisor parked (Linux-only)");
            return;
        }

        if (ClientProcessMode.GuiAgent || ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("TrackingControlsSupervisor parked (not full tracker)");
            return;
        }

        // Let login / control.json settle (mirrors Windows watchdog 20s delay).
        try { await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "TrackingControlsSupervisor running (roaming → launch/relaunch --tracking-controls)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TrackingControlsSupervisor tick failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (BackgroundGuardService.SuppressWatchdog)
            return;

        var snap = await TrackingModeResolver.ResolveAsync(ct).ConfigureAwait(false);
        if (TrackingModeResolver.IsFixed(snap))
            return;

        if (!TrackingControlsActivation.HasGraphicalSession())
            return;

        // Mutex held ⇒ process alive (may be hidden in tray) — do not force-show.
        if (TrackingControlsActivation.IsRunning())
            return;

        if (!CooldownOk(ref _lastLaunchAttempt, TimeSpan.FromSeconds(12)))
            return;

        _logger.LogInformation(
            "Roaming Start/Stop modal missing — launching --tracking-controls");
        TrackingControlsActivation.ShowOrLaunch(refreshDesktopEnv: true);
    }

    private static bool CooldownOk(ref DateTimeOffset last, TimeSpan min)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - last < min) return false;
        last = now;
        return true;
    }
}
