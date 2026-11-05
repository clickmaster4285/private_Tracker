using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services.Watchers;

/// <summary>
/// Time and Attendance (Phase 1, finalplan section 2.6 + R7): guarantees a
/// <c>power_off</c> session_event is written BEFORE the host stops, on ALL of:
///   - IHostApplicationLifetime.ApplicationStopping (background mode SIGTERM)
///   - Avalonia IClassicDesktopStyleApplicationLifetime.Exit (GUI shutdown)
///   - Console.CancelKeyPress (Ctrl+C in a terminal)
///   - window.Closing (handled in App.axaml.cs - emits <c>ui_hidden</c> instead)
///   - Singleton disposal (defense-in-depth)
///
/// R7 design rationale: in --background mode, Program.cs waits through
/// <c>WaitForShutdownAsync</c>, which is tied to the host lifetime. On SIGTERM,
/// ApplicationStopping fires and this sentinel synchronously completes its
/// bounded write before hosted-service shutdown continues.
///
/// Hard 2-second timeout: the IEventRecorder already enforces a 2s write
/// timeout internally, so the sentinel can never block shutdown longer than
/// ~2s even if the SQLite is wedged.
///
/// DI ordering (finalplan section 3): this service is registered FIRST, before
/// IEventRecorder / LogCollectorService / SyncService, so it is StartAsync'd
/// first and StopAsync'd last by the .NET host.
/// </summary>
public sealed class ShutdownSentinel : IHostedService, IDisposable
{
    private readonly IEventRecorder _eventRecorder;
    private readonly ILogger<ShutdownSentinel> _logger;
    private readonly ManualResetEventSlim _powerOffWritten = new(false);

    /// <summary>
    /// Program.cs waits on this MRE after <c>host.StopAsync()</c> to ensure the
    /// sentinel's write of the power_off event has completed (or timed out)
    /// BEFORE the SQLite store is disposed. Without this, the host disposes the
    /// store mid-write and the row is lost.
    /// </summary>
    public ManualResetEventSlim PowerOffWritten => _powerOffWritten;

    private IHostApplicationLifetime? _lifetime;
    private IDisposable? _stoppingTokenRegistration;
    private IDisposable? _startedRegistration;
    private bool _powerOffRecorded;

    public ShutdownSentinel(IEventRecorder eventRecorder, ILogger<ShutdownSentinel> logger)
    {
        _eventRecorder = eventRecorder;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Separate --gui process must NEVER write power_off — closing the shell
        // is not a machine shutdown; the --background tracker owns that event.
        if (ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("ShutdownSentinel parked (GUI agent)");
            _powerOffRecorded = true; // skip StopAsync/Dispose fallbacks
            _powerOffWritten.Set();
            return Task.CompletedTask;
        }

        _logger.LogDebug("ShutdownSentinel: StartAsync - wiring shutdown hooks");

        // Avalonia Exit (GUI mode shutdown). We resolve the lifetime via the static
        // Application.Current so we don't have to depend on Avalonia in this file's
        // surface (it stays a plain IHostedService). The handler is best-effort:
        // if Avalonia never started (--background mode), Current is null and we
        // skip silently. The ApplicationStopping hook below covers --background.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) =>
            {
                _logger.LogDebug("ShutdownSentinel: Avalonia desktop.Exit fired");
                _ = RecordPowerOffAsync("avalania_exit");
            };
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Late-wire the IHostApplicationLifetime hooks after host.StartAsync.
    /// This split keeps the sentinel DI-safe while registering the stopping
    /// callback before Program.cs begins waiting for shutdown.
    /// </summary>
    public void HookLifetime(IHostApplicationLifetime lifetime)
    {
        if (ClientProcessMode.ParkTrackingWork) return;
        if (_lifetime != null) return; // idempotent
        _lifetime = lifetime;
        _stoppingTokenRegistration = lifetime.ApplicationStopping.Register(() =>
        {
            _logger.LogDebug("ShutdownSentinel: ApplicationStopping fired");
            // Sync wait - the IEventRecorder is async but it enforces its own
            // 2s timeout. We block here because the host will dispose us
            // moments later; blocking is the only way to guarantee the row
            // reaches SQLite.
            RecordPowerOffAsync("application_stopping").GetAwaiter().GetResult();
        });
        _startedRegistration = lifetime.ApplicationStarted.Register(() =>
        {
            _logger.LogDebug("ShutdownSentinel: ApplicationStarted - sentinel ready");
        });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Defensive: if StopAsync runs without ApplicationStopping ever firing
        // (rare: explicit host.StopAsync outside the shutdown path), record here.
        if (!_powerOffRecorded)
        {
            _logger.LogDebug("ShutdownSentinel: StopAsync - recording fallback power_off");
            RecordPowerOffAsync("host_stop_async").GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }
    public void Dispose()
    {
        if (!_powerOffRecorded)
        {
            // Last-chance: in --background mode the host can hit Dispose during
            // a fast SIGTERM before ApplicationStopping fires. Synchronous write
            // here ensures the event is in SQLite before the connection closes.
            _logger.LogDebug("ShutdownSentinel: Dispose - final-chance power_off");
            try
            {
                RecordPowerOffAsync("dispose").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ShutdownSentinel: Dispose-time power_off write failed");
            }
        }
        _stoppingTokenRegistration?.Dispose();
        _startedRegistration?.Dispose();
        _powerOffWritten.Dispose();
    }

    /// <summary>
    /// Hook Console.CancelKeyPress from Program.cs. Ctrl+C in a terminal fires
    /// this handler BEFORE the host's normal shutdown sequence, so the sentinel
    /// can record the event before the SIGTERM-equivalent cancel cascades.
    /// </summary>
    public void HookConsoleCancelKeyPress()
    {
        try
        {
            Console.CancelKeyPress += (_, e) =>
            {
                _logger.LogDebug("ShutdownSentinel: Console.CancelKeyPress fired (cancel={Cancel})", e.Cancel);
                // Don't cancel the host's normal stop path; just record the event
                // alongside it. e.Cancel = false lets SIGINT propagate.
                RecordPowerOffAsync("ctrl_c").GetAwaiter().GetResult();
            };
        }
        catch (Exception ex)
        {
            // Console.CancelKeyPress can throw on hosts that don't own a console
            // (e.g. service-mode Windows). Log and continue.
            _logger.LogDebug(ex, "ShutdownSentinel: Console.CancelKeyPress hook failed (no console?)");
        }
    }

    /// <summary>
    /// Record a <c>ui_hidden</c> event when the main window is closing to the
    /// tray (NOT a real shutdown). Called by App.axaml.cs when AllowShutdown is
    /// false. The dashboard can show "in tray" vs "active" based on this row.
    /// </summary>
    public void RecordUiHidden()
    {
        _ = _eventRecorder.RecordAsync(SessionEventTypes.UiHidden);
    }

    /// <summary>
    /// Core write of the power_off event. Always sets the MRE on the way out
    /// so Program.cs can stop waiting. Idempotent: only the first call actually
    /// records; subsequent calls are no-ops so we never write duplicate power_off
    /// rows on multi-source shutdown (e.g. Avalonia Exit AND SIGTERM race).
    /// </summary>
    private async Task RecordPowerOffAsync(string source)
    {
        if (_powerOffRecorded) return;
        _powerOffRecorded = true;
        try
        {
            _logger.LogDebug("ShutdownSentinel: writing power_off (source={Source})", source);
            var meta = new Dictionary<string, string> { ["source"] = source };
            await _eventRecorder.RecordAsync(SessionEventTypes.PowerOff, meta: meta);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ShutdownSentinel: power_off write failed");
        }
        finally
        {
            // Signal Program.cs so it can stop waiting and let the host dispose.
            try { _powerOffWritten.Set(); } catch { /* already disposed */ }
        }
    }
}
