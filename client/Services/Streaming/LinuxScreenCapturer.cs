using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using client.Configuration;

namespace client.Services.Streaming;

/// <summary>
/// Linux screen capture for live stream: Wayland via xdg-desktop-portal ScreenCast +
/// PipeWire/GStreamer; X11 via a best-effort XGetImage path inside the same probe.
/// Frames are framed BGRA on the probe's stdout (latest frame kept by the caller).
/// </summary>
internal sealed class LinuxScreenCapturer : IDisposable
{
    internal const uint FrameMagic = 0x52544641; // 'AFTR' LE — Alpha Frame

    private readonly AppConfig _config;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private Process? _proc;
    private Thread? _reader;
    private volatile bool _running;
    private volatile int _selectedIndex;
    private volatile int _maxWidth;
    private ScreenFrame? _latest;
    private string? _lastError;
    private long _framesProduced;
    private readonly string _tokenPath;

    public LinuxScreenCapturer(AppConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
        _maxWidth = Math.Max(320, config.StreamMaxWidth);
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "alpha-ai-tracker");
        Directory.CreateDirectory(dataDir);
        _tokenPath = Path.Combine(dataDir, "stream-restore-token");
    }

    public long FramesProduced => Interlocked.Read(ref _framesProduced);
    public string LastError => _lastError ?? "";

    public void SetSelectedMonitor(int index) => _selectedIndex = Math.Max(0, index);

    public void SetMaxWidth(int maxWidth) =>
        _maxWidth = Math.Clamp(maxWidth, 320, Math.Max(320, _config.StreamMaxWidth));

    public bool TryDequeueLatest(out ScreenFrame? frame)
    {
        lock (_gate)
        {
            frame = _latest;
            _latest = null;
            return frame is not null;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
                return;
            _running = true;
            _lastError = null;
            _latest = null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = "-u -",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Inherit session bus / Wayland from the tracker process (systemd user unit
            // already carries these after the 2026-08-31 unit migration).
            psi.Environment["ALPHA_STREAM_TOKEN_PATH"] = _tokenPath;
            psi.Environment["ALPHA_STREAM_MONITOR_INDEX"] = _selectedIndex.ToString();
            psi.Environment["ALPHA_STREAM_MAX_WIDTH"] = _maxWidth.ToString();
            psi.Environment["ALPHA_STREAM_FPS"] = Math.Clamp(_config.StreamFps, 1, 30).ToString();

            _proc = Process.Start(psi);
            if (_proc is null)
            {
                _lastError = "python3_start_failed";
                _running = false;
                return;
            }

            _proc.StandardInput.Write(ProbeScript);
            _proc.StandardInput.Close();

            _reader = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "LinuxScreenCapturer",
            };
            _reader.Start();

            // Drain stderr asynchronously for diagnostics + restore-token notices.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (_proc is { HasExited: false })
                    {
                        var line = await _proc.StandardError.ReadLineAsync().ConfigureAwait(false);
                        if (line is null)
                            break;
                        if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                        {
                            _lastError = line[6..].Trim();
                            _logger.LogWarning("Linux screen capture: {Err}", _lastError);
                        }
                        else
                            _logger.LogDebug("Linux screen capture: {Line}", line);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Linux screen capture stderr reader ended");
                }
            });

            _logger.LogInformation(
                "Linux screen capture started (portal/PipeWire probe, monitor={Idx}, maxWidth={W})",
                _selectedIndex, _maxWidth);
        }
        catch (Exception ex)
        {
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogWarning(ex, "Linux screen capture failed to start");
            Stop();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _latest = null;
        }

        try
        {
            if (_proc is { HasExited: false })
            {
                try { _proc.Kill(entireProcessTree: true); }
                catch { /* already exiting */ }
            }
        }
        catch { /* ignore */ }

        try { _proc?.Dispose(); } catch { /* ignore */ }
        _proc = null;

        if (_reader is { IsAlive: true })
        {
            try { _reader.Join(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
        _reader = null;
    }

    public void Dispose() => Stop();

    private void ReadLoop()
    {
        var proc = _proc;
        if (proc is null)
            return;

        var stdout = proc.StandardOutput.BaseStream;
        var header = new byte[16];
        try
        {
            while (_running && !proc.HasExited)
            {
                if (!ReadExact(stdout, header, 16))
                    break;

                var magic = BitConverter.ToUInt32(header, 0);
                if (magic != FrameMagic)
                {
                    _lastError = $"bad_frame_magic=0x{magic:X8}";
                    // Resync is hard — abort and let the outer loop restart on next SetStreamActive.
                    break;
                }

                var w = BitConverter.ToInt32(header, 4);
                var h = BitConverter.ToInt32(header, 8);
                var nbytes = BitConverter.ToInt32(header, 12);
                if (w < 2 || h < 2 || nbytes <= 0 || nbytes > 64 * 1024 * 1024 || nbytes != w * h * 4)
                {
                    _lastError = $"bad_frame_header w={w} h={h} n={nbytes}";
                    break;
                }

                var bgra = new byte[nbytes];
                if (!ReadExact(stdout, bgra, nbytes))
                    break;

                // Even dimensions for VP8.
                var ew = w & ~1;
                var eh = h & ~1;
                if (ew != w || eh != h)
                {
                    var trimmed = new byte[ew * eh * 4];
                    for (var y = 0; y < eh; y++)
                        Buffer.BlockCopy(bgra, y * w * 4, trimmed, y * ew * 4, ew * 4);
                    bgra = trimmed;
                    w = ew;
                    h = eh;
                }

                var frame = new ScreenFrame
                {
                    Bgra = bgra,
                    Width = w,
                    Height = h,
                    TimestampMs = Environment.TickCount64,
                };
                lock (_gate)
                    _latest = frame;
                Interlocked.Increment(ref _framesProduced);
                _lastError = null;
            }
        }
        catch (Exception ex)
        {
            if (_running)
            {
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
                _logger.LogWarning(ex, "Linux screen capture reader failed");
            }
        }
        finally
        {
            if (_running && string.IsNullOrEmpty(_lastError))
                _lastError = "probe_exited";
        }
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        var off = 0;
        while (off < count)
        {
            var n = stream.Read(buffer, off, count - off);
            if (n <= 0)
                return false;
            off += n;
        }
        return true;
    }

    /// <summary>
    /// Enumerate logical monitors via Mutter DisplayConfig (GNOME Wayland/X11).
    /// Falls back to a single "Primary" entry when Mutter is unavailable.
    /// </summary>
    public static List<StreamMonitorInfo> EnumerateMonitors(ILogger logger)
    {
        var list = new List<StreamMonitorInfo>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python3",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(EnumerateMonitorsSnippet);
            using var p = Process.Start(psi);
            if (p is null)
                return FallbackMonitor();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // name|width|height|primary(0/1)
                var parts = line.Split('|');
                if (parts.Length < 4)
                    continue;
                if (!int.TryParse(parts[1], out var w) || !int.TryParse(parts[2], out var h))
                    continue;
                var primary = parts[3] == "1";
                list.Add(new StreamMonitorInfo
                {
                    Index = list.Count,
                    Name = primary ? $"{parts[0]} (Primary)" : parts[0],
                    Width = w,
                    Height = h,
                    IsPrimary = primary,
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Linux monitor enumeration failed");
        }

        if (list.Count == 0)
            return FallbackMonitor();

        // Ensure exactly one primary.
        if (!list.Exists(m => m.IsPrimary))
        {
            list[0] = new StreamMonitorInfo
            {
                Index = 0,
                Name = list[0].Name.Contains("Primary", StringComparison.Ordinal)
                    ? list[0].Name
                    : $"{list[0].Name} (Primary)",
                Width = list[0].Width,
                Height = list[0].Height,
                IsPrimary = true,
            };
        }
        return list;
    }

    private static List<StreamMonitorInfo> FallbackMonitor() =>
    [
        new StreamMonitorInfo
        {
            Index = 0,
            Name = "Primary",
            Width = 1920,
            Height = 1080,
            IsPrimary = true,
        }
    ];

    private const string EnumerateMonitorsSnippet =
        """
import os,sys
try:
    import dbus
    bus=dbus.SessionBus()
    obj=bus.get_object('org.gnome.Mutter.DisplayConfig','/org/gnome/Mutter/DisplayConfig')
    iface=dbus.Interface(obj,'org.gnome.Mutter.DisplayConfig')
    serial,monitors,logical,_props=iface.GetCurrentState()
    for i,lm in enumerate(logical):
        # (x, y, scale, transform, primary, monitors[], props) — GNOME Mutter
        x,y,scale,transform,primary,mons,props=lm
        name='Display %d'%(i+1)
        w=h=0
        if mons:
            connector=str(mons[0][0])
            name=connector
            for m in monitors:
                if str(m[0][0])==connector:
                    for mode in m[1]:
                        mid,mw,mh,refresh,scale,supported,mode_props=mode
                        if bool(mode_props.get('is-current',False)):
                            w,h=int(mw),int(mh)
                            break
                    if w==0 and m[1]:
                        w,h=int(m[1][0][1]),int(m[1][0][2])
                    break
        if w<=0 or h<=0:
            w,h=1920,1080
        print('%s|%d|%d|%d'%(name,w,h,1 if primary else 0))
except Exception as e:
    sys.stderr.write('mutter_enum_failed: %s\n'%e)
""";

    // Portal ScreenCast → PipeWire → GStreamer appsink → framed BGRA on stdout.
    // First Share dialog may require a click; persist_mode=2 + restore token makes
    // later starts silent until the user revokes in Settings → Privacy.
    private const string ProbeScript =
        """
import os, sys, struct, re, time, signal

os.environ.setdefault("GI_TYPELIB_PATH", "/usr/lib/x86_64-linux-gnu/girepository-1.0")

def err(msg):
    sys.stderr.write("ERROR: %s\n" % msg)
    sys.stderr.flush()

try:
    import dbus
    from dbus.mainloop.glib import DBusGMainLoop
    import gi
    gi.require_version("Gst", "1.0")
    gi.require_version("GLib", "2.0")
    from gi.repository import Gst, GLib
except Exception as e:
    err("deps: %s" % e)
    sys.exit(2)

DBusGMainLoop(set_as_default=True)
Gst.init(None)

TOKEN_PATH = os.environ.get("ALPHA_STREAM_TOKEN_PATH", "")
MONITOR_INDEX = int(os.environ.get("ALPHA_STREAM_MONITOR_INDEX", "0") or "0")
MAX_WIDTH = int(os.environ.get("ALPHA_STREAM_MAX_WIDTH", "1920") or "1920")
FPS = max(1, min(30, int(os.environ.get("ALPHA_STREAM_FPS", "10") or "10")))
MAGIC = 0x52544641  # AFTR

session = None
pipeline = None
loop = GLib.MainLoop()
bus = dbus.SessionBus()
portal = bus.get_object("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop")
request_iface = "org.freedesktop.portal.Request"
screen_cast_iface = "org.freedesktop.portal.ScreenCast"
sender_name = re.sub(r"\.", r"_", bus.get_unique_name()[1:])
request_token_counter = 0
session_token_counter = 0
streams = []

def terminate(code=0):
    global pipeline
    try:
        if pipeline is not None:
            pipeline.set_state(Gst.State.NULL)
    except Exception:
        pass
    try:
        if session is not None:
            bus.get_object("org.freedesktop.portal.Desktop", session).Close(
                dbus_interface="org.freedesktop.portal.Session"
            )
    except Exception:
        pass
    try:
        loop.quit()
    except Exception:
        pass
    sys.exit(code)

signal.signal(signal.SIGTERM, lambda *_: terminate(0))
signal.signal(signal.SIGINT, lambda *_: terminate(0))

def new_request_path():
    global request_token_counter
    request_token_counter += 1
    token = "u%d" % request_token_counter
    path = "/org/freedesktop/portal/desktop/request/%s/%s" % (sender_name, token)
    return path, token

def new_session_path():
    global session_token_counter
    session_token_counter += 1
    token = "u%d" % session_token_counter
    path = "/org/freedesktop/portal/desktop/session/%s/%s" % (sender_name, token)
    return path, token

def screen_cast_call(method, callback, *args, options=None):
    if options is None:
        options = {}
    request_path, request_token = new_request_path()
    options = dict(options)
    options["handle_token"] = request_token

    def on_response(response, results):
        try:
            bus.remove_signal_receiver(on_response, signal_name="Response")
        except Exception:
            pass
        callback(response, results)

    bus.add_signal_receiver(
        on_response,
        signal_name="Response",
        dbus_interface=request_iface,
        path=request_path,
    )
    method(*(args + (options,)), dbus_interface=screen_cast_iface)

def load_token():
    if not TOKEN_PATH:
        return ""
    try:
        with open(TOKEN_PATH, "r", encoding="utf-8") as f:
            return f.read().strip()
    except Exception:
        return ""

def save_token(token):
    if not TOKEN_PATH or not token:
        return
    try:
        d = os.path.dirname(TOKEN_PATH)
        if d:
            os.makedirs(d, mode=0o700, exist_ok=True)
        with open(TOKEN_PATH, "w", encoding="utf-8") as f:
            f.write(token)
        os.chmod(TOKEN_PATH, 0o600)
        sys.stderr.write("restore_token_saved\n")
        sys.stderr.flush()
    except Exception as e:
        err("token_save: %s" % e)

def on_bus_message(bus_obj, message):
    t = message.type
    if t == Gst.MessageType.ERROR:
        err_obj, debug = message.parse_error()
        err("gst: %s (%s)" % (err_obj, debug))
        terminate(1)
    elif t == Gst.MessageType.EOS:
        terminate(0)

def scale_bgra(data, w, h, max_w):
    # Nearest-neighbor downscale to max_w (even dims). Avoid GStreamer videoscale —
    # that path hit integer-overflow negotiate failures on some Mutter sizes.
    if max_w <= 0 or w <= max_w:
        ew, eh = w & ~1, h & ~1
        if ew == w and eh == h:
            return data, w, h
        out = bytearray(ew * eh * 4)
        for y in range(eh):
            src = y * w * 4
            dst = y * ew * 4
            out[dst:dst + ew * 4] = data[src:src + ew * 4]
        return bytes(out), ew, eh
    nw = max_w & ~1
    nh = max(2, int(round(h * (nw / float(w)))) & ~1)
    out = bytearray(nw * nh * 4)
    for y in range(nh):
        sy = min(h - 1, int(y * h / nh))
        for x in range(nw):
            sx = min(w - 1, int(x * w / nw))
            si = (sy * w + sx) * 4
            di = (y * nw + x) * 4
            out[di:di + 4] = data[si:si + 4]
    return bytes(out), nw, nh

def on_new_sample(sink):
    sample = sink.emit("pull-sample")
    if sample is None:
        return Gst.FlowReturn.OK
    buf = sample.get_buffer()
    caps = sample.get_caps()
    st = caps.get_structure(0)
    w = int(st.get_value("width"))
    h = int(st.get_value("height"))
    ok, mapinfo = buf.map(Gst.MapFlags.READ)
    if not ok:
        return Gst.FlowReturn.OK
    try:
        data = bytes(mapinfo.data)
        need = w * h * 4
        if len(data) < need:
            return Gst.FlowReturn.OK
        if len(data) > need:
            data = data[:need]
        data, w, h = scale_bgra(data, w, h, MAX_WIDTH)
        need = w * h * 4
        header = struct.pack("<IIII", MAGIC, w, h, need)
        try:
            sys.stdout.buffer.write(header)
            sys.stdout.buffer.write(data)
            sys.stdout.buffer.flush()
        except BrokenPipeError:
            terminate(0)
            return Gst.FlowReturn.EOS
    finally:
        buf.unmap(mapinfo)
    return Gst.FlowReturn.OK

def play_stream(node_id, fd):
    global pipeline
    # Native PipeWire size → BGRx. Do NOT use videorate here — on Mutter/PipeWire it
    # starves appsink (smoke test: 2 frames / 6s). DropOldest appsink + C# capture
    # interval already bound FPS; scale happens in on_new_sample.
    desc = (
        "pipewiresrc fd=%d path=%u do-timestamp=true ! videoconvert ! video/x-raw,format=BGRx"
        " ! appsink name=sink emit-signals=true max-buffers=1 drop=true sync=false"
        % (fd, node_id)
    )
    sys.stderr.write("pipeline: %s\n" % desc)
    sys.stderr.flush()
    pipeline = Gst.parse_launch(desc)
    sink = pipeline.get_by_name("sink")
    sink.connect("new-sample", on_new_sample)
    pipeline.get_bus().add_signal_watch()
    pipeline.get_bus().connect("message", on_bus_message)
    pipeline.set_state(Gst.State.PLAYING)

def on_start_response(response, results):
    global streams
    if response != 0:
        err("start_denied_or_failed response=%s" % response)
        terminate(1)
        return
    token = results.get("restore_token")
    if token:
        save_token(str(token))
    streams = list(results.get("streams", []))
    if not streams:
        err("no_streams")
        terminate(1)
        return
    idx = MONITOR_INDEX if 0 <= MONITOR_INDEX < len(streams) else 0
    node_id = int(streams[idx][0])
    sys.stderr.write("using_stream index=%d node=%u total=%d\n" % (idx, node_id, len(streams)))
    sys.stderr.flush()
    empty = dbus.Dictionary(signature="sv")
    fd_obj = portal.OpenPipeWireRemote(session, empty, dbus_interface=screen_cast_iface)
    fd = fd_obj.take()
    play_stream(node_id, fd)

def on_select_sources_response(response, results):
    if response != 0:
        err("select_sources_failed response=%s" % response)
        terminate(1)
        return
    screen_cast_call(portal.Start, on_start_response, session, "")

def on_create_session_response(response, results):
    global session
    if response != 0:
        err("create_session_failed response=%s" % response)
        terminate(1)
        return
    session = results["session_handle"]
    opts = {
        "multiple": True,
        "types": dbus.UInt32(1),  # MONITOR
        "cursor_mode": dbus.UInt32(2),  # Embedded
        "persist_mode": dbus.UInt32(2),  # until revoked
    }
    tok = load_token()
    if tok:
        opts["restore_token"] = tok
        sys.stderr.write("restore_token_present\n")
        sys.stderr.flush()
    screen_cast_call(portal.SelectSources, on_select_sources_response, session, options=opts)

# Session type hint for logs
sys.stderr.write("session_type=%s wayland=%s display=%s\n" % (
    os.environ.get("XDG_SESSION_TYPE", ""),
    os.environ.get("WAYLAND_DISPLAY", ""),
    os.environ.get("DISPLAY", ""),
))
sys.stderr.flush()

_session_path, session_token = new_session_path()
screen_cast_call(
    portal.CreateSession,
    on_create_session_response,
    options={"session_handle_token": session_token},
)

# Fail the probe if the Share dialog is ignored for too long.
GLib.timeout_add_seconds(120, lambda: (err("portal_timeout_120s"), terminate(1), False)[2])

try:
    loop.run()
except Exception as e:
    err("loop: %s" % e)
    terminate(1)
""";
}
