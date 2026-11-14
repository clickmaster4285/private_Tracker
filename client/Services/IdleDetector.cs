using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Time and Attendance (Phase 1, finalplan section 2.4 / BUG-10 fix): cross-platform
/// idle / AFK detection. Polls the OS's OWN idle-time source every
/// <see cref="AppConfig.IdlePollSeconds"/> seconds and, when the idle time crosses
/// the configured threshold, emits an idle_start / idle_end marker via
/// <see cref="IEventRecorder"/>.
///
/// Per-platform idle sources (R2 - OS metadata only, no name heuristics):
///   Linux:   org.gnome.Mutter.IdleMonitor.GetIdletime D-Bus method FIRST (works on
///            X11 AND Wayland GNOME; NOT /dev/input/* - root-required, planv1 gap #1
///            / BUG-10), then X11 XScreenSaverQueryInfo as the non-GNOME fallback.
///   Windows: GetLastInputInfo P/Invoke (same pattern as GlobalMemoryStatusEx in
///            LogCollectorService).
///   macOS:   placeholder until the maccatalyst TFM split (see SystemEventWatcher.MacOS.cs).
///
/// The detector emits ONLY threshold-crossing markers: an employee idle for 3
/// hours produces two rows, not 360. The AttendanceAggregator (A.8) accumulates
/// idle_seconds from the crossing markers + app_sessions focus accounting.
/// </summary>
public sealed class IdleDetector : BackgroundService
{
    private readonly IEventRecorder _eventRecorder;
    private readonly AppConfig _config;
    private readonly ILogger<IdleDetector> _logger;

    // State machine: active <-> idle. We emit an event on each crossing only.
    private bool _currentlyIdle;

    public IdleDetector(IEventRecorder eventRecorder, AppConfig config, ILogger<IdleDetector> logger)
    {
        _eventRecorder = eventRecorder;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("IdleDetector parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!_config.TaEnabled)
        {
            _logger.LogInformation("IdleDetector: ALPHA_TA_ENABLED=false - parked");
            return;
        }

        _logger.LogInformation(
            "IdleDetector starting (threshold={IdleSec}s, poll={PollSec}s)",
            _config.IdleThresholdSeconds, _config.IdlePollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (client.Core.UpdatePriority.ShouldYield || client.Core.FaceVerifyYield.ShouldYield)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var idleSeconds = await GetIdleSecondsAsync(stoppingToken);
                if (idleSeconds.HasValue)
                {
                    var isIdle = idleSeconds.Value >= _config.IdleThresholdSeconds;
                    if (isIdle && !_currentlyIdle)
                    {
                        _currentlyIdle = true;
                        _logger.LogDebug("IdleDetector: idle started ({Idle}s)", idleSeconds.Value);
                        await _eventRecorder.RecordAsync(SessionEventTypes.IdleStart, ct: stoppingToken);
                    }
                    else if (!isIdle && _currentlyIdle)
                    {
                        _currentlyIdle = false;
                        _logger.LogDebug("IdleDetector: idle ended (last idle={Idle}s)", idleSeconds.Value);
                        await _eventRecorder.RecordAsync(SessionEventTypes.IdleEnd, ct: stoppingToken);
                    }
                }
                // null = no idle source on this platform; nothing to do.
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One failed poll must never kill the detector loop.
                _logger.LogDebug(ex, "IdleDetector: poll failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.IdlePollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Returns seconds since the last user input, or null when the platform has
    /// no idle source wired (e.g. macOS until the TFM split).
    /// </summary>
    private async Task<double?> GetIdleSecondsAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return IdleDetectorPlatform.GetIdleSecondsWindows();
        }
        if (OperatingSystem.IsLinux())
        {
            // GNOME Mutter IdleMonitor works on X11 AND Wayland GNOME. Primary;
            // the raw X11 fallback covers non-GNOME X11 sessions.
            var dbusIdle = await GetIdleSecondsGnomeAsync(ct);
            if (dbusIdle.HasValue) return dbusIdle;
            return IdleDetectorPlatform.GetIdleSecondsX11(_logger);
        }
        return null;
    }

    // ── Linux (GNOME): org.gnome.Mutter.IdleMonitor.GetIdletime ──
    // Works on X11 AND Wayland GNOME sessions. Lazy D-Bus session connection;
    // failures (KDE, wlroots, headless) are expected and retry each poll cheaply.

    private Tmds.DBus.Protocol.DBusConnection? _dbusConnection;
    private DateTime _lastGnomeFailureLog = DateTime.MinValue;

    private async Task<double?> GetIdleSecondsGnomeAsync(CancellationToken ct)
    {
        try
        {
            if (_dbusConnection == null)
            {
                _dbusConnection = new Tmds.DBus.Protocol.DBusConnection(Tmds.DBus.Protocol.DBusAddress.Session!);
                await _dbusConnection.ConnectAsync();
            }

            var writer = _dbusConnection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.gnome.Mutter.IdleMonitor",
                path: "/org/gnome/Mutter/IdleMonitor/Core",
                @interface: "org.gnome.Mutter.IdleMonitor",
                member: "GetIdletime",
                signature: null,
                flags: Tmds.DBus.Protocol.MessageFlags.None);
            var msg = writer.CreateMessage();

            // Typed reply overload: the reader extracts GetIdletime's uint (ms).
            var idleMs = await _dbusConnection.CallMethodAsync<uint>(
                msg,
                (Tmds.DBus.Protocol.Message m, object? s) => m.GetBodyReader().ReadUInt32(),
                null).WaitAsync(ct);
            return idleMs / 1000.0;
        }
        catch (Exception ex)
        {
            // Non-GNOME session: expected. Rate-limit the debug log to once/5min
            // so a KDE machine doesn't spam the log every 30s poll.
            try { _dbusConnection?.Dispose(); } catch { }
            _dbusConnection = null;
            if ((DateTime.UtcNow - _lastGnomeFailureLog) > TimeSpan.FromMinutes(5))
            {
                _lastGnomeFailureLog = DateTime.UtcNow;
                _logger.LogDebug(ex, "IdleDetector: Mutter.IdleMonitor unavailable (non-GNOME session?)");
            }
            return null;
        }
    }
}

/// <summary>
/// Platform idle-source implementations for <see cref="IdleDetector"/>. Split into
/// a static nested container to keep the P/Invoke declarations out of the loop.
/// </summary>
public static class IdleDetectorPlatform
{
    // ── Windows: GetLastInputInfo ──

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static double? GetIdleSecondsWindows()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        if (!GetLastInputInfo(ref info)) return null;
        // Environment.TickCount (int, wraps ~24.9d) and dwTime (uint) share the
        // same tick base; unchecked arithmetic on the same width handles wraps.
        var delta = unchecked((int)info.dwTime - Environment.TickCount);
        var idleMs = -delta;
        return idleMs / 1000.0;
    }

    // ── Linux (GNOME Mutter IdleMonitor): lives in IdleDetector (instance method
    //    needs the D-Bus connection) — see GetIdleSecondsGnomeAsync there. ──

    // ── Linux (X11 fallback): XScreenSaverQueryInfo ──

    [DllImport("libX11.so.6", SetLastError = false)]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6", SetLastError = false)]
    private static extern void XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6", SetLastError = false)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6", SetLastError = false)]
    private static extern void XFree(IntPtr data);

    [DllImport("libXss.so.1", SetLastError = false)]
    private static extern IntPtr XScreenSaverAllocInfo();

    [DllImport("libXss.so.1", SetLastError = false)]
    private static extern bool XScreenSaverQueryInfo(IntPtr display, IntPtr drawable, IntPtr info);

    [StructLayout(LayoutKind.Sequential)]
    private struct XScreenSaverInfo
    {
        public IntPtr window;
        public int state;
        public int kind;
        public ulong til_or_since;
        public ulong idle;      // ms since last input — what we want
        public IntPtr eventMask;
    }

    public static double? GetIdleSecondsX11(ILogger logger)
    {
        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return null; // Wayland-only session
            try
            {
                var info = XScreenSaverAllocInfo();
                if (info == IntPtr.Zero) return null;
                try
                {
                    var root = XDefaultRootWindow(display);
                    if (!XScreenSaverQueryInfo(display, root, info)) return null;
                    var saved = Marshal.PtrToStructure<XScreenSaverInfo>(info);
                    return saved.idle / 1000.0;
                }
                finally
                {
                    XFree(info);
                }
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch (DllNotFoundException)
        {
            // Wayland-only machine without libXss: expected. Silent.
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "IdleDetector: X11 idle query failed");
            return null;
        }
    }
}
