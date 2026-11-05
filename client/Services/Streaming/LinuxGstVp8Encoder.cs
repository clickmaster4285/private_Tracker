using System.Diagnostics;
using System.Buffers.Binary;
using System.Text;

namespace client.Services.Streaming;

/// <summary>
/// Linux VP8 encoder via GStreamer <c>vp8enc</c> (system libgstvpx).
/// SIPSorceryMedia.Encoders only ships Windows <c>vpxmd.dll</c>; linking system
/// libvpx as libvpxmd.so segfaults on struct/ABI mismatch. GStreamer is already
/// required for Wayland ScreenCast capture on this platform.
/// </summary>
internal sealed class LinuxGstVp8Encoder : IDisposable
{
    private static readonly byte[] FrameMagic = "AATF"u8.ToArray();
    private static readonly byte[] EncodedMagic = "AATE"u8.ToArray();

    private readonly object _gate = new();
    private Process? _proc;
    private int _width;
    private int _height;
    private uint _targetKbps = 1500;
    private uint _fps = 10;
    private uint _kfMaxDist = 10;
    private bool _forceKeyFrame = true;
    private bool _disposed;
    private string? _lastError;

    public string LastError => _lastError ?? "";

    public uint TargetKbps
    {
        get => _targetKbps;
        set
        {
            lock (_gate)
            {
                var next = Math.Max(500, value);
                if (_targetKbps == next) return;
                _targetKbps = next;
                StopUnlocked();
            }
        }
    }

    public uint Fps
    {
        get => _fps;
        set
        {
            lock (_gate)
            {
                var next = Math.Clamp(value, 1, 30);
                if (_fps == next) return;
                _fps = next;
                StopUnlocked();
            }
        }
    }

    public uint KeyframeMaxDistance
    {
        get => _kfMaxDist;
        set => _kfMaxDist = Math.Max(1, value);
    }

    public void ForceKeyFrame() => _forceKeyFrame = true;

    public byte[]? EncodeBgra(int width, int height, byte[] bgra)
    {
        if (width < 2 || height < 2 || bgra.Length < width * height * 4)
            return null;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureProcessUnlocked(width, height);

            var proc = _proc;
            if (proc is null || proc.HasExited)
            {
                _lastError = "encoder_process_dead";
                return null;
            }

            try
            {
                var stdin = proc.StandardInput.BaseStream;
                var stdout = proc.StandardOutput.BaseStream;

                // Optional keyframe nudge
                if (_forceKeyFrame)
                {
                    var kf = Encoding.ASCII.GetBytes("KEYFRAME\n");
                    stdin.Write(kf);
                    _forceKeyFrame = false;
                }

                Span<byte> hdr = stackalloc byte[16];
                FrameMagic.CopyTo(hdr);
                BinaryPrimitives.WriteInt32LittleEndian(hdr[4..], width);
                BinaryPrimitives.WriteInt32LittleEndian(hdr[8..], height);
                BinaryPrimitives.WriteInt32LittleEndian(hdr[12..], width * height * 4);
                stdin.Write(hdr);
                stdin.Write(bgra.AsSpan(0, width * height * 4));
                stdin.Flush();

                Span<byte> outHdr = stackalloc byte[8];
                if (!ReadExact(stdout, outHdr))
                {
                    _lastError = "encoder_eof";
                    StopUnlocked();
                    return null;
                }
                if (outHdr[0] != EncodedMagic[0] || outHdr[1] != EncodedMagic[1] ||
                    outHdr[2] != EncodedMagic[2] || outHdr[3] != EncodedMagic[3])
                {
                    _lastError = "encoder_bad_magic";
                    StopUnlocked();
                    return null;
                }
                var nbytes = BinaryPrimitives.ReadInt32LittleEndian(outHdr[4..]);
                if (nbytes <= 0)
                    return null;
                if (nbytes > 8 * 1024 * 1024)
                {
                    _lastError = $"encoder_oversize n={nbytes}";
                    StopUnlocked();
                    return null;
                }
                var encoded = new byte[nbytes];
                if (!ReadExact(stdout, encoded))
                {
                    _lastError = "encoder_short_read";
                    StopUnlocked();
                    return null;
                }
                _lastError = null;
                return encoded;
            }
            catch (Exception ex)
            {
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
                StopUnlocked();
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopUnlocked();
        }
    }

    private void EnsureProcessUnlocked(int width, int height)
    {
        if (_proc is { HasExited: false } && _width == width && _height == height)
            return;

        StopUnlocked();
        _width = width;
        _height = height;

        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(HelperScript);
        psi.ArgumentList.Add("--width");
        psi.ArgumentList.Add(width.ToString());
        psi.ArgumentList.Add("--height");
        psi.ArgumentList.Add(height.ToString());
        psi.ArgumentList.Add("--fps");
        psi.ArgumentList.Add(_fps.ToString());
        psi.ArgumentList.Add("--kbps");
        psi.ArgumentList.Add(Math.Max(500, _targetKbps).ToString());
        psi.ArgumentList.Add("--kf");
        psi.ArgumentList.Add(Math.Max(1, _kfMaxDist).ToString());

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("python3 failed to start for VP8 encode");

        // Drain stderr for diagnostics (non-blocking background).
        var errProc = _proc;
        _ = Task.Run(async () =>
        {
            try
            {
                while (errProc is { HasExited: false })
                {
                    var line = await errProc.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) break;
                    if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                        _lastError = line;
                }
            }
            catch { /* ignore */ }
        });
    }

    private void StopUnlocked()
    {
        var proc = _proc;
        _proc = null;
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
            {
                try
                {
                    proc.StandardInput.WriteLine("QUIT");
                    proc.StandardInput.Flush();
                }
                catch { /* ignore */ }
                if (!proc.WaitForExit(800))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    proc.WaitForExit(500);
                }
            }
        }
        catch { /* ignore */ }
        finally
        {
            try { proc.Dispose(); } catch { /* ignore */ }
        }
        _width = 0;
        _height = 0;
    }

    private static bool ReadExact(Stream stream, Span<byte> buffer)
    {
        var off = 0;
        while (off < buffer.Length)
        {
            var n = stream.Read(buffer[off..]);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }

    private static bool ReadExact(Stream stream, byte[] buffer)
        => ReadExact(stream, buffer.AsSpan());

    private const string HelperScript =
        """
import os, sys, struct, argparse, select

parser = argparse.ArgumentParser()
parser.add_argument('--width', type=int, required=True)
parser.add_argument('--height', type=int, required=True)
parser.add_argument('--fps', type=int, default=10)
parser.add_argument('--kbps', type=int, default=1500)
parser.add_argument('--kf', type=int, default=10)
args = parser.parse_args()

def err(msg):
    sys.stderr.write('ERROR: ' + msg + '\n')
    sys.stderr.flush()

try:
    import gi
    gi.require_version('Gst', '1.0')
    gi.require_version('GstApp', '1.0')
    from gi.repository import Gst, GstApp
except Exception as e:
    err('deps: ' + str(e))
    sys.exit(2)

Gst.init(None)
w, h = args.width & ~1, args.height & ~1
fps = max(1, min(30, args.fps))
kbps = max(500, args.kbps)
kf = max(1, args.kf)
bitrate = kbps * 1000

desc = (
    'appsrc name=src is-live=true do-timestamp=true format=time block=true ! '
    'video/x-raw,format=BGRA,width=%d,height=%d,framerate=%d/1 ! '
    'videoconvert ! video/x-raw,format=I420 ! '
    'vp8enc name=enc target-bitrate=%d deadline=1 cpu-used=8 '
    'keyframe-max-dist=%d lag-in-frames=0 threads=2 ! '
    'appsink name=sink sync=false max-buffers=2 drop=false'
) % (w, h, fps, bitrate, kf)

sys.stderr.write('pipeline: %s\n' % desc)
sys.stderr.flush()
pipeline = Gst.parse_launch(desc)
src = pipeline.get_by_name('src')
enc = pipeline.get_by_name('enc')
sink = pipeline.get_by_name('sink')
ret = pipeline.set_state(Gst.State.PLAYING)
if ret == Gst.StateChangeReturn.FAILURE:
    err('gst_playing_failed')
    sys.exit(3)

frame_dur = int(Gst.SECOND / fps)
pts = 0
inp = sys.stdin.buffer
out = sys.stdout.buffer

def read_exact(n):
    buf = bytearray(n)
    view = memoryview(buf)
    off = 0
    while off < n:
        got = inp.readinto(view[off:])
        if not got:
            return None
        off += got
    return bytes(buf)

while True:
    # Allow KEYFRAME / QUIT text commands interleaved — peek is hard on binary;
    # we only accept them as full lines when the next bytes are ASCII letters.
    # Protocol: either "QUIT\\n" / "KEYFRAME\\n" OR 16-byte AATF header.
    first = inp.read(1)
    if not first:
        break
    if first in (b'Q', b'K'):
        rest = inp.readline()
        cmd = (first + rest).decode('ascii', 'ignore').strip()
        if cmd == 'QUIT':
            break
        if cmd == 'KEYFRAME':
            try:
                enc.set_property('force-keyunit', True)
            except Exception:
                # Older gst: push force-keyunit event
                try:
                    structure = Gst.Structure.new_empty('GstForceKeyUnit')
                    event = Gst.Event.new_custom(Gst.EventType.CUSTOM_DOWNSTREAM, structure)
                    src.send_event(event)
                except Exception as e:
                    err('keyframe: ' + str(e))
        continue

    hdr = first + read_exact(15)
    if hdr is None or len(hdr) < 16:
        break
    if hdr[:4] != b'AATF':
        err('bad_magic')
        break
    fw, fh, nbytes = struct.unpack_from('<iii', hdr, 4)
    if fw != w or fh != h or nbytes != w * h * 4:
        err('size_mismatch got=%dx%d n=%d want=%dx%d' % (fw, fh, nbytes, w, h))
        break
    data = read_exact(nbytes)
    if data is None:
        break

    buf = Gst.Buffer.new_allocate(None, nbytes, None)
    buf.fill(0, data)
    buf.pts = pts
    buf.duration = frame_dur
    pts += frame_dur
    flow = src.emit('push-buffer', buf)
    if flow != Gst.FlowReturn.OK:
        err('push_flow=' + str(flow))
        break

    sample = sink.emit('try-pull-sample', 2 * Gst.SECOND)
    if sample is None:
        out.write(b'AATE' + struct.pack('<i', 0))
        out.flush()
        continue
    obuf = sample.get_buffer()
    ok, mapinfo = obuf.map(Gst.MapFlags.READ)
    if not ok:
        out.write(b'AATE' + struct.pack('<i', 0))
        out.flush()
        continue
    payload = bytes(mapinfo.data)
    obuf.unmap(mapinfo)
    out.write(b'AATE' + struct.pack('<i', len(payload)))
    out.write(payload)
    out.flush()

pipeline.set_state(Gst.State.NULL)
sys.exit(0)
""";
}
