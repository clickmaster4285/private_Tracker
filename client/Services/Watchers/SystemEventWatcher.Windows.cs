using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using client.Core.Models;

namespace client.Services.Watchers;

#pragma warning disable CA1416 // Every entry point below returns unless OperatingSystem.IsWindows().

/// <summary>
/// Windows half of <see cref="SystemEventWatcher"/>. Uses the .NET
/// <see cref="SystemEvents"/> wrapper around the Win32 message pump for:
///   - <c>PowerModeChanged</c>  - power status changes (Resume / Suspend)
///   - <c>SessionSwitch</c>     - logon / logoff / lock / unlock / remote connect
///   - <c>SessionEnding</c>     - system shutdown / restart (writes power_off)
///
/// The SystemEvents class is a managed wrapper around the Win32 hidden message
/// window that the framework creates on first use. It works in both GUI and
/// (post-2026-08-18) background mode because .NET's host wires the pump on a
/// dedicated thread.
///
/// TODO A.3.2: a future commit will add WTSRegisterSessionNotification on a
/// custom HWND window for fast Remote Desktop session-change detection. For
/// Phase 1 the SystemEvents path is sufficient.
/// </summary>
public sealed partial class SystemEventWatcher
{
    private PowerModeChangedEventHandler? _winPowerModeChanged;

    private SessionSwitchEventHandler? _winSessionSwitch;

    private SessionEndingEventHandler? _winSessionEnding;

    private void SubscribeWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        _logger.LogInformation("SystemEventWatcher: subscribing to SystemEvents (PowerModeChanged, SessionSwitch, SessionEnding)");

        _winPowerModeChanged = (sender, e) =>
        {
            try
            {
                switch (e.Mode)
                {
                    case PowerModes.Resume:
                        _ = SafeRecordAsync(SessionEventTypes.Resume, "systemevents_power_resume", default);
                        break;
                    case PowerModes.Suspend:
                        _ = SafeRecordAsync(SessionEventTypes.PowerOff, "systemevents_power_suspend", default);
                        break;
                    // StatusChange (battery/AC) intentionally ignored.
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SystemEventWatcher: PowerModeChanged handler failed");
            }
        };
        SystemEvents.PowerModeChanged += _winPowerModeChanged;

        _winSessionSwitch = (sender, e) =>
        {
            try
            {
                switch (e.Reason)
                {
                    case SessionSwitchReason.ConsoleConnect:
                    case SessionSwitchReason.RemoteConnect:
                    case SessionSwitchReason.SessionLogon:
                        _ = SafeRecordAsync(SessionEventTypes.OsLogin, "systemevents_session_logon", default);
                        break;
                    case SessionSwitchReason.ConsoleDisconnect:
                    case SessionSwitchReason.RemoteDisconnect:
                    case SessionSwitchReason.SessionLogoff:
                        _ = SafeRecordAsync(SessionEventTypes.OsLogout, "systemevents_session_logoff", default);
                        break;
                    case SessionSwitchReason.SessionLock:
                        _ = SafeRecordAsync(SessionEventTypes.ScreenLock, "systemevents_session_lock", default);
                        break;
                    case SessionSwitchReason.SessionUnlock:
                        _ = SafeRecordAsync(SessionEventTypes.ScreenUnlock, "systemevents_session_unlock", default);
                        break;
                    // SessionRemoteControl and SessionUnknown intentionally ignored.
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SystemEventWatcher: SessionSwitch handler failed");
            }
        };
        SystemEvents.SessionSwitch += _winSessionSwitch;

        _winSessionEnding = (sender, e) =>
        {
            try
            {
                if (e.Reason == SessionEndReasons.SystemShutdown)
                {
                    PersistPowerOffAndCloseSessionsSynchronously("systemevents_session_ending");
                }
                // SessionEndReasons.Logoff is already handled by SessionSwitch
                // (SessionLogoff), so we skip it here to avoid duplicate os_logout rows.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SystemEventWatcher: SessionEnding handler failed");
            }
        };
        SystemEvents.SessionEnding += _winSessionEnding;
    }

    /// <summary>
    /// Windows-only: unsubscribe from the .NET SystemEvents. Called from
    /// the base class's StopAsync. Implementation here (rather than the
    /// base) because the delegate fields are private to this partial.
    /// </summary>
    internal void UnsubscribeWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (_winPowerModeChanged != null)
                SystemEvents.PowerModeChanged -= _winPowerModeChanged;
            if (_winSessionSwitch != null)
                SystemEvents.SessionSwitch -= _winSessionSwitch;
            if (_winSessionEnding != null)
                SystemEvents.SessionEnding -= _winSessionEnding;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SystemEventWatcher: Windows unsubscribe failed");
        }
    }
}

#pragma warning restore CA1416
