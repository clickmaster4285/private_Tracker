using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;

namespace client.Services.Screenshots;

/// <summary>
/// Offline capture worker — never talks to the network. Every
/// <see cref="AppConfig.ScreenshotIntervalSec"/> (default 30s) grabs a still,
/// encodes WebP, and writes pending/ under LocalAppData.
/// </summary>
public sealed class ScreenshotCaptureWorker : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly ScreenCaptureService _capture;
    private readonly ILogger<ScreenshotCaptureWorker> _logger;
    private bool _loggedPressure;

    public ScreenshotCaptureWorker(
        AppConfig config,
        ILogStore store,
        ScreenCaptureService capture,
        ILogger<ScreenshotCaptureWorker> logger)
    {
        _config = config;
        _store = store;
        _capture = capture;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("ScreenshotCaptureWorker parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!_config.ScreenshotEnabled)
        {
            _logger.LogInformation("ScreenshotCaptureWorker: ALPHA_SCREENSHOT_ENABLED=false — parked");
            return;
        }

        if (!_capture.StreamAvailable)
        {
            _logger.LogInformation("ScreenshotCaptureWorker parked — screen capture unavailable on this OS");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        ScreenshotLocalStore.EnsureDirs();
        var rng = new Random();
        var jitterMs = rng.Next(0, Math.Max(1, _config.ScreenshotIntervalSec * 1000 / 3));
        _logger.LogInformation(
            "ScreenshotCaptureWorker starting (interval={Sec}s maxWidth={W} quality={Q} jitter={Jitter}ms)",
            _config.ScreenshotIntervalSec, _config.ScreenshotMaxWidth, _config.ScreenshotWebpQuality, jitterMs);

        try { await Task.Delay(jitterMs, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (client.Core.UpdatePriority.ShouldYield || client.Core.FaceVerifyYield.ShouldYield)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await CaptureOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ScreenshotCaptureWorker tick failed");
            }

            var wait = _config.ScreenshotIntervalSec * 1000
                       + rng.Next(0, Math.Max(1, _config.ScreenshotIntervalSec * 1000 / 6));
            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task CaptureOnceAsync(CancellationToken ct)
    {
        var info = await _store.GetEmployeeInfoAsync(ct).ConfigureAwait(false);
        if (info is null || string.IsNullOrWhiteSpace(info.EmployeeId) ||
            string.IsNullOrWhiteSpace(info.DeviceToken))
            return;

        var (count, bytes) = ScreenshotLocalStore.PendingStats();
        var maxBytes = (long)_config.ScreenshotMaxPendingMb * 1024L * 1024L;
        if (count >= _config.ScreenshotMaxPending || bytes >= maxBytes)
        {
            if (!_loggedPressure)
            {
                _loggedPressure = true;
                _logger.LogWarning(
                    "ScreenshotCaptureWorker: pending pressure count={Count} bytes={Bytes} — skipping capture (never delete unsynced)",
                    count, bytes);
            }
            return;
        }
        _loggedPressure = false;

        var frame = _capture.CaptureStill(_config.ScreenshotMaxWidth);
        if (frame is null)
            return;

        var webp = ScreenshotWebpEncoder.EncodeBgra(
            frame.Bgra, frame.Width, frame.Height, _config.ScreenshotWebpQuality);
        if (webp is null || webp.Length == 0)
        {
            _logger.LogDebug("ScreenshotCaptureWorker: WebP encode returned empty");
            return;
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var machineId = _config.ClientId;
        var screenshotId = Guid.NewGuid().ToString("D");
        var baseName = ScreenshotLocalStore.BuildBaseName(
            info.EmployeeId, capturedAt, machineId, screenshotId);
        var meta = new ScreenshotMeta
        {
            ScreenshotId = screenshotId,
            EmployeeId = info.EmployeeId,
            MachineId = machineId,
            CapturedAtUtc = capturedAt.UtcDateTime,
            Width = frame.Width,
            Height = frame.Height,
            ByteSize = webp.Length,
            ContentType = "image/webp",
        };
        await ScreenshotLocalStore.WritePendingAsync(baseName, webp, meta, ct).ConfigureAwait(false);
        _logger.LogDebug(
            "Screenshot saved pending {Name} {W}x{H} {Bytes}b",
            baseName, frame.Width, frame.Height, webp.Length);
    }
}
