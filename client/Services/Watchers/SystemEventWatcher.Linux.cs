using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;
using client.Core.Models;

namespace client.Services.Watchers;

/// <summary>
/// Linux half of <see cref="SystemEventWatcher"/>. Subscribes to the OS's own
/// D-Bus signals for power, sleep, lock, and login events. NEVER reads
/// /dev/input/* (root-required, planv1 gap #1).
///
/// Signal sources (two buses — GNOME ScreenSaver lives on the SESSION bus, not
/// the system bus; UPower + login1 are on the system bus):
///
///   SYSTEM BUS:
///     1. org.freedesktop.UPower / PrepareForSleep(bool)       — power/sleep.
///     2. org.freedesktop.login1.Manager / PrepareForShutdown  — reboot/shutdown.
///     3. org.freedesktop.login1.Session / Lock + Unlock       — lock/unlock
///        (works when the screen locker calls Lock(); GNOME Shell 46+ uses
///        SetLockedHint instead, so this only fires on some desktops).
///
///   SESSION BUS:
///     4. org.gnome.ScreenSaver / ActiveChanged(bool)          — GNOME lock.
///        Primary source for GNOME screen lock/unlock detection.
///
///   SESSION BUS (polling fallback):
///     5. org.gnome.ScreenSaver.GetActive() polled every 30 s. Catches lock
///        state changes that ActiveChanged misses (GNOME 46 sometimes skips
///        the signal for Super+L but always reflects the state in GetActive).
///
/// Tmds.DBus.Protocol API notes (0.94.2):
///   - sender is a nullable string (service name), NOT a Sender object.
///   - bool values read via Reader.ReadBool().
///   - Signals with no body use the non-generic WatchSignalAsync.
/// </summary>
public sealed partial class SystemEventWatcher
{
    private DBusConnection? _dbusConnection;
    private DBusConnection? _dbusSessionConnection;
    private readonly List<IDisposable> _dbusSubscriptions = new();

    private async Task SubscribeLinuxAsync(CancellationToken ct)
    {
        _logger.LogInformation("SystemEventWatcher: subscribing to UPower, login1, ScreenSaver");

        // ── SYSTEM BUS: UPower + login1 ──
        DBusConnection conn;
        try
        {
            conn = new DBusConnection(DBusAddress.System!);
            await conn.ConnectAsync();
            _dbusConnection = conn;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SystemEventWatcher: D-Bus system bus connection failed");
            return;
        }

        // ── UPower: PrepareForSleep(true)=sleeping, (false)=resumed ──
        try
        {
            var sub = await conn.WatchSignalAsync<bool>(
                sender: "org.freedesktop.UPower",
                path: "/org/freedesktop/UPower",
                @interface: "org.freedesktop.UPower",
                signal: "PrepareForSleep",
                reader: (Message m, object? s) => m.GetBodyReader().ReadBool(),
                handler: (Notification<bool> n) =>
                {
                    if (n.IsCompletion) return;
                    try
                    {
                        _ = SafeRecordAsync(
                            n.Value ? SessionEventTypes.PowerOff : SessionEventTypes.Resume,
                            "upower", default);
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "UPower handler failed"); }
                },
                null,
                ObserverFlags.None,
                state: null);
            _dbusSubscriptions.Add(sub);
            _logger.LogDebug("SystemEventWatcher: UPower OK");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UPower subscription failed");
        }

        // ── login1 Manager: shutdown/reboot before the display server dies ──
        // GNOME tears Xwayland down before the user service receives its normal
        // stop job. A process hosting a lazy Avalonia GUI can therefore exit on
        // XOpenDisplay's fatal disconnect before ApplicationStopping runs.
        // PrepareForShutdown(true) is emitted earlier and is the authoritative
        // opportunity to persist power_off.
        try
        {
            var shutdownSub = await conn.WatchSignalAsync<bool>(
                sender: "org.freedesktop.login1",
                path: "/org/freedesktop/login1",
                @interface: "org.freedesktop.login1.Manager",
                signal: "PrepareForShutdown",
                reader: (Message m, object? s) => m.GetBodyReader().ReadBool(),
                handler: (Notification<bool> n) =>
                {
                    if (n.IsCompletion || !n.Value) return;
                    try
                    {
                        // The bus and process can disappear immediately after
                        // this callback, so complete the recorder and session
                        // close transaction synchronously.
                        PersistPowerOffAndCloseSessionsSynchronously("login1_prepare_shutdown");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "login1 PrepareForShutdown handler failed");
                    }
                },
                null,
                ObserverFlags.None,
                state: null);
            _dbusSubscriptions.Add(shutdownSub);
            _logger.LogDebug("SystemEventWatcher: login1 PrepareForShutdown OK");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "login1 PrepareForShutdown subscription failed");
        }

        // ── login1: Lock / Unlock (no body → non-generic watcher) ──
        // NOTE: on GNOME 46+ Wayland, Super+L calls SetLockedHint(true) instead
        // of Lock(), so the Lock signal is NOT emitted. The Unlock signal IS
        // emitted (via UnlockSessions()). This subscription catches lock/unlock
        // on desktops that DO call Lock() (KDE, XFCE, etc.) and unlock on all.
        try
        {
            var lockSub = await conn.WatchSignalAsync(
                sender: "org.freedesktop.login1",
                path: null,
                @interface: "org.freedesktop.login1.Session",
                signal: "Lock",
                handler: (Notification n) =>
                {
                    if (n.IsCompletion) return;
                    try { _ = SafeRecordAsync(SessionEventTypes.ScreenLock, "login1", default); }
                    catch (Exception ex) { _logger.LogWarning(ex, "login1 Lock handler failed"); }
                },
                null,
                ObserverFlags.None,
                state: null);
            _dbusSubscriptions.Add(lockSub);

            var unlockSub = await conn.WatchSignalAsync(
                sender: "org.freedesktop.login1",
                path: null,
                @interface: "org.freedesktop.login1.Session",
                signal: "Unlock",
                handler: (Notification n) =>
                {
                    if (n.IsCompletion) return;
                    try { _ = SafeRecordAsync(SessionEventTypes.ScreenUnlock, "login1", default); }
                    catch (Exception ex) { _logger.LogWarning(ex, "login1 Unlock handler failed"); }
                },
                null,
                ObserverFlags.None,
                state: null);
            _dbusSubscriptions.Add(unlockSub);
            _logger.LogDebug("SystemEventWatcher: login1 OK");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "login1 subscription failed");
        }

        // ── SESSION BUS: GNOME ScreenSaver ActiveChanged(bool) ──
        // org.gnome.ScreenSaver lives on the SESSION bus, not the system bus.
        // Without a session-bus connection this subscription silently matches
        // nothing (the old code had this bug — ScreenSaver was subscribed on
        // the system bus where the service doesn't exist).
        DBusConnection? sessionConn = null;
        try
        {
            sessionConn = new DBusConnection(DBusAddress.Session!);
            await sessionConn.ConnectAsync();
            _dbusSessionConnection = sessionConn;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SystemEventWatcher: D-Bus session bus connection failed (ScreenSaver disabled)");
        }

        if (sessionConn != null)
        {
            try
            {
                var sub = await sessionConn.WatchSignalAsync<bool>(
                    sender: "org.gnome.ScreenSaver",
                    path: "/org/gnome/ScreenSaver",
                    @interface: "org.gnome.ScreenSaver",
                    signal: "ActiveChanged",
                    reader: (Message m, object? s) => m.GetBodyReader().ReadBool(),
                    handler: (Notification<bool> n) =>
                    {
                        if (n.IsCompletion) return;
                        try
                        {
                            _screenSaverActive = n.Value;
                            _ = SafeRecordAsync(
                                n.Value ? SessionEventTypes.ScreenLock : SessionEventTypes.ScreenUnlock,
                                "gnome_screensaver", default);
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "ScreenSaver handler failed"); }
                    },
                    null,
                    ObserverFlags.None,
                    state: null);
                _dbusSubscriptions.Add(sub);
                _logger.LogDebug("SystemEventWatcher: GNOME ScreenSaver OK (session bus)");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GNOME ScreenSaver subscription skipped (probably non-GNOME)");
            }
        }
    }

    internal void UnsubscribeLinux()
    {
        foreach (var sub in _dbusSubscriptions)
        {
            try { sub.Dispose(); } catch { /* best-effort */ }
        }
        _dbusSubscriptions.Clear();

        if (_dbusConnection != null)
        {
            try { _dbusConnection.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "D-Bus system dispose failed"); }
            _dbusConnection = null;
        }

        if (_dbusSessionConnection != null)
        {
            try { _dbusSessionConnection.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "D-Bus session dispose failed"); }
            _dbusSessionConnection = null;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Polling fallback for GNOME ScreenSaver.GetActive().
    // GNOME 46+ sometimes emits ActiveChanged for Super+L and sometimes
    // doesn't — but GetActive() always reflects the true state. Polled
    // every ~30s from ExecuteAsync.
    // ════════════════════════════════════════════════════════════════════════

    // Tracked by both the ActiveChanged signal handler AND the poll loop.
    // Null = unknown (first poll will emit based on state change).
    private bool? _screenSaverActive;

    internal async Task PollScreenSaverActiveAsync()
    {
        var sessionConn = _dbusSessionConnection;
        if (sessionConn == null) return;

        try
        {
            var writer = sessionConn.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.gnome.ScreenSaver",
                path: "/org/gnome/ScreenSaver",
                @interface: "org.gnome.ScreenSaver",
                member: "GetActive",
                signature: "",
                flags: MessageFlags.None);
            var msg = writer.CreateMessage();
            var isActive = await sessionConn.CallMethodAsync(
                msg,
                (Message m, object? s) => m.GetBodyReader().ReadBool(),
                null);

            var previous = _screenSaverActive;
            _screenSaverActive = isActive;

            if (previous.HasValue && previous.Value != isActive)
            {
                _logger.LogDebug("ScreenSaver poll: state changed to {Active}", isActive);
                _ = SafeRecordAsync(
                    isActive ? SessionEventTypes.ScreenLock : SessionEventTypes.ScreenUnlock,
                    "screensaver_poll", default);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ScreenSaver poll failed (non-GNOME or session bus gone)");
        }
    }
}
