using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Services.Streaming;

namespace client.Services;

/// <summary>One physical/virtual display the capture service can target.</summary>
public sealed class StreamMonitorInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsPrimary { get; init; }
}

/// <summary>One BGRA screen frame for WebRTC encode (latest-wins channel).</summary>
public sealed class ScreenFrame
{
    public required byte[] Bgra { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long TimestampMs { get; init; }
}

/// <summary>
/// Captures a selected monitor to BGRA while streaming is active.
/// Windows: multi-path GDI BitBlt. Linux: xdg-desktop-portal ScreenCast + PipeWire
/// (Wayland) via an embedded python3/GStreamer probe.
/// Frames are latest-wins via a capacity-1 channel — never blocks tracker loops.
/// Encoding to VP8 is done by <see cref="LiveStreamClient"/> for WebRTC.
/// </summary>
public sealed class ScreenCaptureService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogger<ScreenCaptureService> _logger;
    private readonly Channel<ScreenFrame> _frames = Channel.CreateBounded<ScreenFrame>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly object _monitorGate = new();
    private List<StreamMonitorInfo> _monitors = new();
    private List<Rectangle> _boundsByIndex = new();
    private Rectangle _captureBounds = Rectangle.Empty;
    private int _selectedIndex;

    private volatile bool _streamActive;
    private bool _loggedUnavailable;
    private MonitorEnumProc? _enumProc;
    /// <summary>Runtime max width override from adaptive degradation (0 = use config).</summary>
    private volatile int _maxWidthOverride;
    private Bitmap? _srcBitmap;
    private Bitmap? _scaledBitmap;
    private byte[]? _bgraScratch;
    private long _framesProduced;
    private int _consecutiveCaptureFails;
    private string? _lastCaptureError;
    private LinuxScreenCapturer? _linuxCapturer;

    public ScreenCaptureService(AppConfig config, ILogger<ScreenCaptureService> logger)
    {
        _config = config;
        _logger = logger;
        StreamAvailable = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            RefreshMonitors();
    }

    public bool StreamAvailable { get; private set; }

    /// <summary>Total frames written to the channel since process start (diagnostics).</summary>
    public long FramesProduced => Interlocked.Read(ref _framesProduced);

    /// <summary>Last capture failure message (empty when healthy).</summary>
    public string LastCaptureError =>
        !string.IsNullOrEmpty(_lastCaptureError)
            ? _lastCaptureError
            : (_linuxCapturer?.LastError ?? "");

    public ChannelReader<ScreenFrame> Frames => _frames.Reader;

    /// <summary>
    /// One-shot still for offline screenshots — independent of live-stream
    /// <see cref="_streamActive"/>. Uses the same GDI / Linux capture path with a
    /// temporary max-width override. Returns null when capture is unavailable.
    /// </summary>
    public ScreenFrame? CaptureStill(int maxWidth)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return null;

        var prevOverride = _maxWidthOverride;
        try
        {
            var capped = Math.Clamp(maxWidth <= 0 ? _config.StreamMaxWidth : maxWidth, 320, 1920);
            _maxWidthOverride = capped;

            if (OperatingSystem.IsWindows())
            {
                lock (_monitorGate)
                {
                    RefreshMonitorsUnlocked();
                    if (_monitors.Count == 0) return null;
                    // Prefer primary monitor for periodic screenshots.
                    var primary = _monitors.FindIndex(m => m.IsPrimary);
                    if (primary >= 0)
                    {
                        _selectedIndex = primary;
                        ApplySelectedBoundsUnlocked();
                    }
                }
                return CaptureFrame();
            }

            // Linux: reuse a short-lived portal capturer when live stream is idle.
            _linuxCapturer ??= new LinuxScreenCapturer(_config, _logger);
            lock (_monitorGate)
            {
                RefreshMonitorsUnlocked();
                var primary = _monitors.FindIndex(m => m.IsPrimary);
                if (primary >= 0) _selectedIndex = primary;
            }
            _linuxCapturer.SetSelectedMonitor(_selectedIndex);
            _linuxCapturer.SetMaxWidth(EffectiveMaxWidth);
            var startedHere = false;
            if (!_streamActive)
            {
                _linuxCapturer.Start();
                startedHere = true;
            }
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    var frame = CaptureFrameLinux();
                    if (frame is not null) return frame;
                    Thread.Sleep(50);
                }
                return null;
            }
            finally
            {
                if (startedHere && !_streamActive)
                    _linuxCapturer.Stop();
            }
        }
        catch (Exception ex)
        {
            _lastCaptureError = ex.Message;
            _logger.LogDebug(ex, "CaptureStill failed");
            return null;
        }
        finally
        {
            _maxWidthOverride = prevOverride;
        }
    }

    public IReadOnlyList<StreamMonitorInfo> GetMonitors()
    {
        lock (_monitorGate)
        {
            RefreshMonitorsUnlocked();
            return _monitors.ToList();
        }
    }

    public int SelectedMonitorIndex
    {
        get { lock (_monitorGate) return _selectedIndex; }
    }

    public void SetStreamActive(bool active)
    {
        if (active && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            RefreshMonitors();
        if (!active)
            _maxWidthOverride = 0;

        if (OperatingSystem.IsLinux())
        {
            if (active)
            {
                _linuxCapturer ??= new LinuxScreenCapturer(_config, _logger);
                _linuxCapturer.SetSelectedMonitor(_selectedIndex);
                _linuxCapturer.SetMaxWidth(EffectiveMaxWidth);
                _linuxCapturer.Start();
                StreamAvailable = true;
            }
            else
            {
                _linuxCapturer?.Stop();
            }
        }

        _streamActive = active;
    }

    /// <summary>
    /// Adaptive resolution lever (Phase 1). Pass 0 to clear and use ALPHA_STREAM_MAX_WIDTH.
    /// Values are clamped to [320, config max].
    /// </summary>
    public void SetMaxWidthOverride(int maxWidth)
    {
        if (maxWidth <= 0)
        {
            _maxWidthOverride = 0;
            return;
        }
        _maxWidthOverride = Math.Clamp(maxWidth, 320, Math.Max(320, _config.StreamMaxWidth));
        // Linux: do not restart the portal session for ABR — CaptureFrameLinux
        // downscales the latest BGRA frame when EffectiveMaxWidth shrinks.
    }

    public int EffectiveMaxWidth =>
        _maxWidthOverride > 0 ? _maxWidthOverride : Math.Max(320, _config.StreamMaxWidth);

    public int SetSelectedMonitor(int index)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return 0;

        lock (_monitorGate)
        {
            RefreshMonitorsUnlocked();
            if (_monitors.Count == 0)
                return _selectedIndex;
            if (index < 0 || index >= _monitors.Count)
                return _selectedIndex;
            _selectedIndex = index;
            ApplySelectedBoundsUnlocked();
            _linuxCapturer?.SetSelectedMonitor(_selectedIndex);
            // Wayland portal sessions bind the PipeWire node at Start — restart capture
            // so the newly selected monitor is the one published.
            if (OperatingSystem.IsLinux() && _streamActive && _linuxCapturer is not null)
            {
                _linuxCapturer.Stop();
                _linuxCapturer.SetMaxWidth(EffectiveMaxWidth);
                _linuxCapturer.Start();
            }
            var w = OperatingSystem.IsLinux() && _monitors.Count > 0
                ? _monitors[_selectedIndex].Width
                : _captureBounds.Width;
            var h = OperatingSystem.IsLinux() && _monitors.Count > 0
                ? _monitors[_selectedIndex].Height
                : _captureBounds.Height;
            _logger.LogInformation(
                "Screen capture target → monitor {Index} ({Name}) {W}x{H}",
                _selectedIndex, _monitors[_selectedIndex].Name, w, h);
            return _selectedIndex;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("ScreenCaptureService parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!_config.StreamEnabled)
        {
            _logger.LogInformation("Screen capture parked (ALPHA_STREAM_ENABLED=false)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            StreamAvailable = false;
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                _logger.LogInformation("Screen capture parked — unsupported OS for WebRTC live stream");
            }
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        StreamAvailable = true;
        RefreshMonitors();
        _logger.LogInformation(
            "Screen capture ready (os={Os}, fps={Fps}, maxWidth={MaxWidth}, monitors={Count})",
            OperatingSystem.IsWindows() ? "windows" : "linux",
            _config.StreamFps, _config.StreamMaxWidth, GetMonitors().Count);

        // Dedicated thread — GDI / portal capture must not block the thread pool (F12/D8).
        await Task.Factory.StartNew(
            () => CaptureLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _linuxCapturer?.Dispose();
        _linuxCapturer = null;
        DisposeCaptureBitmaps();
        base.Dispose();
    }

    private void CaptureLoop(CancellationToken stoppingToken)
    {
        var fps = Math.Clamp(_config.StreamFps, 1, 30);
        var interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        var adaptiveFps = fps;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_streamActive)
            {
                adaptiveFps = fps;
                interval = TimeSpan.FromMilliseconds(1000.0 / adaptiveFps);
                try { Task.Delay(200, stoppingToken).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var frame = OperatingSystem.IsLinux() ? CaptureFrameLinux() : CaptureFrame();
                if (frame is not null)
                {
                    _frames.Writer.TryWrite(frame);
                    Interlocked.Increment(ref _framesProduced);
                    _consecutiveCaptureFails = 0;
                    _lastCaptureError = null;
                }
                else
                {
                    _consecutiveCaptureFails++;
                    if (_consecutiveCaptureFails == 1 || _consecutiveCaptureFails % 30 == 0)
                    {
                        var linuxErr = _linuxCapturer?.LastError;
                        _lastCaptureError = OperatingSystem.IsLinux()
                            ? $"null_frame linux fails={_consecutiveCaptureFails} err={linuxErr}"
                            : $"null_frame bounds={_captureBounds.Width}x{_captureBounds.Height} fails={_consecutiveCaptureFails}";
                        _logger.LogWarning("Screen capture returned no frame ({Error})", _lastCaptureError);
                    }
                }
            }
            catch (Exception ex)
            {
                // Win32 ERROR_INVALID_HANDLE (6) is common after lock screen, DPI/display
                // change, or a stale GDI Bitmap HDC. Do NOT clear _streamActive here —
                // LiveStreamClient owns that flag; clearing it silently starves the
                // WebRTC media pump (PC stays "connected", SFU answers withTrack=false).
                _consecutiveCaptureFails++;
                _lastCaptureError = $"{ex.GetType().Name}: {ex.Message}";
                _logger.LogWarning(ex, "Screen capture failed — recreating surfaces and retrying");
                DisposeCaptureBitmaps();
                StreamAvailable = false;
                try { Task.Delay(1000, stoppingToken).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                {
                    RefreshMonitors();
                    if (OperatingSystem.IsLinux() && _streamActive && _linuxCapturer is not null)
                    {
                        _linuxCapturer.Stop();
                        _linuxCapturer.Start();
                    }
                    StreamAvailable = true;
                }
                continue;
            }

            sw.Stop();
            // Don't tank quality by dropping to 5 fps under brief load — keep ≥8.
            // Network-driven adaptive FPS is applied by LiveStreamClient via SetMaxWidthOverride
            // and encoder Fps — this path only reacts to capture CPU cost.
            var budget = interval.TotalMilliseconds * 0.9;
            if (sw.ElapsedMilliseconds > budget && adaptiveFps > 8)
            {
                adaptiveFps = Math.Max(8, adaptiveFps - 2);
                interval = TimeSpan.FromMilliseconds(1000.0 / adaptiveFps);
                _logger.LogDebug("Screen capture adaptive throttle → {Fps} fps", adaptiveFps);
            }
            else if (sw.ElapsedMilliseconds < budget * 0.5 && adaptiveFps < fps)
            {
                adaptiveFps = Math.Min(fps, adaptiveFps + 1);
                interval = TimeSpan.FromMilliseconds(1000.0 / adaptiveFps);
            }

            var delay = interval - sw.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                try { Task.Delay(delay, stoppingToken).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void RefreshMonitors()
    {
        lock (_monitorGate)
            RefreshMonitorsUnlocked();
    }

    private void RefreshMonitorsUnlocked()
    {
        if (OperatingSystem.IsLinux())
        {
            _monitors = LinuxScreenCapturer.EnumerateMonitors(_logger);
            _boundsByIndex = _monitors
                .Select(m => new Rectangle(0, 0, m.Width, m.Height))
                .ToList();
            if (_selectedIndex < 0 || _selectedIndex >= _monitors.Count)
                _selectedIndex = 0;
            ApplySelectedBoundsUnlocked();
            return;
        }

        if (!OperatingSystem.IsWindows())
            return;

#pragma warning disable CA1416
        var list = new List<(Rectangle Bounds, string Name, bool Primary)>();
        _enumProc = (IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr __) =>
        {
            var bounds = Rectangle.FromLTRB(lprc.Left, lprc.Top, lprc.Right, lprc.Bottom);
            var primary = false;
            var name = $"Display {list.Count + 1}";
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                if (!string.IsNullOrWhiteSpace(info.szDevice))
                    name = info.szDevice.Trim();
                var fromInfo = Rectangle.FromLTRB(
                    info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right, info.rcMonitor.Bottom);
                if (fromInfo.Width > 0 && fromInfo.Height > 0)
                    bounds = fromInfo;
            }
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return true;
            list.Add((bounds, name, primary));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, _enumProc, IntPtr.Zero);

        var reported = GetSystemMetrics(SM_CMONITORS);
        if (list.Count == 0)
        {
            var w = GetSystemMetrics(SM_CXSCREEN);
            var h = GetSystemMetrics(SM_CYSCREEN);
            if (w > 0 && h > 0)
                list.Add((new Rectangle(0, 0, w, h), "Primary", true));
        }
        else if (reported > list.Count)
        {
            _logger.LogWarning(
                "EnumDisplayMonitors returned {Found} display(s) but SM_CMONITORS={Reported}",
                list.Count, reported);
        }

        list.Sort((a, b) =>
        {
            if (a.Primary != b.Primary) return a.Primary ? -1 : 1;
            var cmp = a.Bounds.Left.CompareTo(b.Bounds.Left);
            return cmp != 0 ? cmp : a.Bounds.Top.CompareTo(b.Bounds.Top);
        });

        if (list.Count > 0 && !list.Exists(m => m.Primary))
        {
            var first = list[0];
            list[0] = (first.Bounds, first.Name, true);
        }

        _monitors = list.Select((m, i) => new StreamMonitorInfo
        {
            Index = i,
            Name = m.Primary ? $"{FriendlyMonitorName(m.Name, i)} (Primary)" : FriendlyMonitorName(m.Name, i),
            Width = m.Bounds.Width,
            Height = m.Bounds.Height,
            IsPrimary = m.Primary,
        }).ToList();

        _boundsByIndex = list.Select(m => m.Bounds).ToList();

        if (_selectedIndex < 0 || _selectedIndex >= _monitors.Count)
            _selectedIndex = 0;
        ApplySelectedBoundsUnlocked();
#pragma warning restore CA1416
    }

    private ScreenFrame? CaptureFrameLinux()
    {
        if (_linuxCapturer is null)
            return null;
        if (!_linuxCapturer.TryDequeueLatest(out var frame) || frame is null)
            return null;

        var maxW = EffectiveMaxWidth;
        if (frame.Width <= maxW)
            return frame;

        var newW = maxW & ~1;
        var newH = Math.Max(2, ((int)Math.Round(frame.Height * (newW / (double)frame.Width))) & ~1);
        return DownscaleBgraNearest(frame, newW, newH);
    }

    private static ScreenFrame DownscaleBgraNearest(ScreenFrame src, int newW, int newH)
    {
        var dst = new byte[newW * newH * 4];
        for (var y = 0; y < newH; y++)
        {
            var sy = Math.Min(src.Height - 1, y * src.Height / newH);
            var srcRow = sy * src.Width * 4;
            var dstRow = y * newW * 4;
            for (var x = 0; x < newW; x++)
            {
                var sx = Math.Min(src.Width - 1, x * src.Width / newW);
                var si = srcRow + sx * 4;
                var di = dstRow + x * 4;
                dst[di] = src.Bgra[si];
                dst[di + 1] = src.Bgra[si + 1];
                dst[di + 2] = src.Bgra[si + 2];
                dst[di + 3] = src.Bgra[si + 3];
            }
        }
        return new ScreenFrame
        {
            Bgra = dst,
            Width = newW,
            Height = newH,
            TimestampMs = src.TimestampMs,
        };
    }

    private static string FriendlyMonitorName(string raw, int index)
    {
        if (raw.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(raw.AsSpan(@"\\.\DISPLAY".Length), out var n))
            return $"Display {n}";
        if (string.IsNullOrWhiteSpace(raw))
            return $"Display {index + 1}";
        return raw;
    }

    private void ApplySelectedBoundsUnlocked()
    {
        if (_boundsByIndex.Count == 0)
        {
            _captureBounds = Rectangle.Empty;
            return;
        }
        if (_selectedIndex < 0 || _selectedIndex >= _boundsByIndex.Count)
            _selectedIndex = 0;
        _captureBounds = _boundsByIndex[_selectedIndex];
    }

    private ScreenFrame? CaptureFrame()
    {
        if (!OperatingSystem.IsWindows())
            return null;

#pragma warning disable CA1416
        Rectangle bounds;
        lock (_monitorGate)
        {
            if (_captureBounds.IsEmpty)
                RefreshMonitorsUnlocked();
            bounds = _captureBounds;
        }
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        if (_srcBitmap is null || _srcBitmap.Width != bounds.Width || _srcBitmap.Height != bounds.Height)
        {
            _srcBitmap?.Dispose();
            _srcBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        }

        try
        {
            // Multi-strategy capture. Win11 (esp. 24H2) often breaks CopyFromScreen
            // with ERROR_INVALID_HANDLE (6); CAPTUREBLT also fails on some HD 530 drivers.
            if (!TryCaptureToBitmap(_srcBitmap, bounds))
            {
                using var g = Graphics.FromImage(_srcBitmap);
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, _srcBitmap.Size, CopyPixelOperation.SourceCopy);
            }
        }
        catch (Exception)
        {
            // Stale HBITMAP / display change — force recreate on the next attempt.
            DisposeCaptureBitmaps();
            throw;
        }

        var maxW = EffectiveMaxWidth;
        Bitmap toCopy = _srcBitmap;
        if (_srcBitmap.Width > maxW)
        {
            var newH = (int)Math.Round(_srcBitmap.Height * (maxW / (double)_srcBitmap.Width));
            newH = Math.Max(2, newH & ~1);
            var newW = maxW & ~1;
            if (_scaledBitmap is null || _scaledBitmap.Width != newW || _scaledBitmap.Height != newH)
            {
                _scaledBitmap?.Dispose();
                _scaledBitmap = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
            }
            using var g = Graphics.FromImage(_scaledBitmap);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.DrawImage(_srcBitmap, 0, 0, _scaledBitmap.Width, _scaledBitmap.Height);
            toCopy = _scaledBitmap;
        }

        var w = toCopy.Width & ~1;
        var h = toCopy.Height & ~1;
        if (w < 2 || h < 2)
            return null;

        var rect = new Rectangle(0, 0, w, h);
        var data = toCopy.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var needed = stride * h;
            if (_bgraScratch is null || _bgraScratch.Length < needed)
                _bgraScratch = new byte[needed];
            Marshal.Copy(data.Scan0, _bgraScratch, 0, needed);
            // Channel ownership requires a dedicated buffer — copy packed BGRA out.
            byte[] bgra;
            if (stride != w * 4)
            {
                bgra = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                    Buffer.BlockCopy(_bgraScratch, y * stride, bgra, y * w * 4, w * 4);
            }
            else
            {
                bgra = new byte[w * h * 4];
                Buffer.BlockCopy(_bgraScratch, 0, bgra, 0, w * h * 4);
            }

            return new ScreenFrame
            {
                Bgra = bgra,
                Width = w,
                Height = h,
                TimestampMs = Environment.TickCount64,
            };
        }
        finally
        {
            toCopy.UnlockBits(data);
        }
#pragma warning restore CA1416
    }

    private void DisposeCaptureBitmaps()
    {
#pragma warning disable CA1416
        try { _srcBitmap?.Dispose(); } catch { /* stale GDI */ }
        try { _scaledBitmap?.Dispose(); } catch { /* stale GDI */ }
#pragma warning restore CA1416
        _srcBitmap = null;
        _scaledBitmap = null;
    }

    private const int RasterSrcCopy = 0x00CC0020;
    private const int RasterCaptureBlt = unchecked((int)0x40CC0020); // SRCCOPY | CAPTUREBLT

    /// <summary>
    /// Try several Win32 BitBlt sources before falling back to CopyFromScreen.
    /// Order matters: plain SRCCOPY is the most reliable on Win11 + Intel HD.
    /// </summary>
    private static bool TryCaptureToBitmap(Bitmap dest, Rectangle bounds)
    {
#pragma warning disable CA1416
        if (TryBitBltFromWindow(IntPtr.Zero, dest, bounds, RasterSrcCopy))
            return true;
        if (TryBitBltFromWindow(IntPtr.Zero, dest, bounds, RasterCaptureBlt))
            return true;

        var desktop = GetDesktopWindow();
        if (desktop != IntPtr.Zero)
        {
            if (TryBitBltFromWindow(desktop, dest, bounds, RasterSrcCopy))
                return true;
            if (TryBitBltFromWindow(desktop, dest, bounds, RasterCaptureBlt))
                return true;
        }

        var hdcDisplay = CreateDC("DISPLAY", null, null, IntPtr.Zero);
        if (hdcDisplay != IntPtr.Zero)
        {
            try
            {
                if (TryBitBltFromHdc(hdcDisplay, dest, bounds, RasterSrcCopy))
                    return true;
            }
            finally
            {
                DeleteDC(hdcDisplay);
            }
        }

        return false;
#pragma warning restore CA1416
    }

    private static bool TryBitBltFromWindow(IntPtr hwnd, Bitmap dest, Rectangle bounds, int raster)
    {
#pragma warning disable CA1416
        var hdcScreen = GetDC(hwnd);
        if (hdcScreen == IntPtr.Zero)
            return false;
        try
        {
            return TryBitBltFromHdc(hdcScreen, dest, bounds, raster);
        }
        finally
        {
            ReleaseDC(hwnd, hdcScreen);
        }
#pragma warning restore CA1416
    }

    private static bool TryBitBltFromHdc(IntPtr hdcScreen, Bitmap dest, Rectangle bounds, int raster)
    {
#pragma warning disable CA1416
        try
        {
            using var g = Graphics.FromImage(dest);
            var hdcDest = g.GetHdc();
            try
            {
                return BitBlt(hdcDest, 0, 0, dest.Width, dest.Height, hdcScreen, bounds.Left, bounds.Top, raster);
            }
            finally
            {
                g.ReleaseHdc(hdcDest);
            }
        }
        catch
        {
            return false;
        }
#pragma warning restore CA1416
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_CMONITORS = 80;
    private const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);
}
