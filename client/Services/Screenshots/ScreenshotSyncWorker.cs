using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;

namespace client.Services.Screenshots;

/// <summary>
/// Offline sync worker — scans pending/ and uploads via DeviceAuth multipart.
/// Deletes local files only after an explicit 2xx accepted response.
/// Honors Retry-After on 429/503 (never delete).
/// </summary>
public sealed class ScreenshotSyncWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly HttpClient _http;
    private readonly ILogger<ScreenshotSyncWorker> _logger;
    private readonly Dictionary<string, int> _failCounts = new(StringComparer.OrdinalIgnoreCase);

    public ScreenshotSyncWorker(
        AppConfig config,
        ILogStore store,
        HttpClient http,
        ILogger<ScreenshotSyncWorker> logger)
    {
        _config = config;
        _store = store;
        _http = http;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("ScreenshotSyncWorker parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!_config.ScreenshotEnabled)
        {
            _logger.LogInformation("ScreenshotSyncWorker: ALPHA_SCREENSHOT_ENABLED=false — parked");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.ServerUrl))
        {
            _logger.LogInformation("ScreenshotSyncWorker parked — ALPHA_SERVER_URL empty");
            return;
        }

        ScreenshotLocalStore.EnsureDirs();
        var rng = new Random();
        _logger.LogInformation(
            "ScreenshotSyncWorker starting (poll={Sec}s batch={Batch})",
            _config.ScreenshotSyncPollSec, _config.ScreenshotSyncBatch);

        try { await Task.Delay(rng.Next(2000, 8000), stoppingToken); }
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

                await DrainPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ScreenshotSyncWorker drain failed");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_config.ScreenshotSyncPollSec),
                    stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task DrainPendingAsync(CancellationToken ct)
    {
        var info = await _store.GetEmployeeInfoAsync(ct).ConfigureAwait(false);
        if (info is null ||
            string.IsNullOrWhiteSpace(info.DeviceToken) ||
            string.IsNullOrWhiteSpace(info.EmployeeId))
            return;

        var pending = ScreenshotLocalStore.ListPendingOldest(_config.ScreenshotSyncBatch);
        if (pending.Count == 0) return;

        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();
            var ok = await UploadOneAsync(item, info.DeviceToken!, ct).ConfigureAwait(false);
            if (!ok)
            {
                // Stop the batch on first soft failure so Retry-After / offline backs off.
                break;
            }
        }
    }

    private async Task<bool> UploadOneAsync(PendingScreenshot item, string deviceToken, CancellationToken ct)
    {
        var marker = item.WebpPath + ".uploading";
        try { await File.WriteAllTextAsync(marker, DateTime.UtcNow.ToString("O"), ct); }
        catch { /* best-effort */ }

        try
        {
            // Read fully then close before DeleteAccepted — Windows locks OpenRead
            // handles, so deleting while the stream is open silently leaves orphans
            // (webp stays, json often goes) and GuessMetaFromName re-uploads forever.
            byte[] webpBytes;
            try
            {
                webpBytes = await File.ReadAllBytesAsync(item.WebpPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Screenshot upload: could not read {File}", item.WebpPath);
                return false;
            }

            if (webpBytes.Length == 0)
            {
                _logger.LogWarning("Screenshot upload: empty file {File} — moving to rejected/", Path.GetFileName(item.WebpPath));
                ScreenshotLocalStore.MoveToRejected(item);
                return true;
            }

            var screenshotId = string.IsNullOrWhiteSpace(item.Meta.ScreenshotId)
                ? null
                : item.Meta.ScreenshotId.Trim();
            if (string.IsNullOrWhiteSpace(screenshotId) || !Guid.TryParse(screenshotId, out _))
            {
                _logger.LogWarning(
                    "Screenshot upload: missing/invalid screenshotId for {File} — moving to rejected/",
                    Path.GetFileName(item.WebpPath));
                ScreenshotLocalStore.MoveToRejected(item);
                return true;
            }

            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(webpBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
            content.Add(fileContent, "file", Path.GetFileName(item.WebpPath));
            content.Add(new StringContent(screenshotId), "screenshotId");
            content.Add(new StringContent(item.Meta.CapturedAtUtc.ToString("o")), "capturedAt");
            content.Add(new StringContent(item.Meta.MachineId ?? ""), "machineId");
            content.Add(new StringContent(item.Meta.Width.ToString(CultureInfo.InvariantCulture)), "width");
            content.Add(new StringContent(item.Meta.Height.ToString(CultureInfo.InvariantCulture)), "height");
            content.Add(new StringContent(item.Meta.ContentType ?? "image/webp"), "contentType");

            var baseUrl = (_config.ServerUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl)) return false;
            var url = baseUrl + "/api/v1/screenshots/upload";
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Device", deviceToken);
            ClientIdentityHeaders.Apply(req);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = ParseRetryAfter(resp) ?? TimeSpan.FromSeconds(5);
                _logger.LogInformation(
                    "Screenshot upload Retry-After {Wait}s ({Status})",
                    wait.TotalSeconds, (int)resp.StatusCode);
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { }
                return false;
            }

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Screenshot upload auth failed ({Status})", (int)resp.StatusCode);
                return false;
            }

            if ((int)resp.StatusCode >= 500)
            {
                _logger.LogWarning("Screenshot upload server error {Status}", (int)resp.StatusCode);
                return false;
            }

            if ((int)resp.StatusCode >= 400)
            {
                var key = item.WebpPath;
                _failCounts.TryGetValue(key, out var n);
                n++;
                _failCounts[key] = n;
                if (n >= 5)
                {
                    _logger.LogWarning(
                        "Screenshot rejected after {N} 4xx — moving to rejected/ ({Status})",
                        n, (int)resp.StatusCode);
                    ScreenshotLocalStore.MoveToRejected(item);
                    _failCounts.Remove(key);
                }
                return true; // continue batch for other files
            }

            // 2xx — require accepted:true when body present
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var accepted = true;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("accepted", out var a) &&
                        a.ValueKind is JsonValueKind.False)
                        accepted = false;
                }
                catch
                {
                    accepted = resp.IsSuccessStatusCode;
                }
            }

            if (!accepted)
            {
                _logger.LogWarning("Screenshot upload 2xx but accepted=false — keeping local file");
                return true;
            }

            // Bytes already fully read — delete is safe on Windows (no open handle).
            ScreenshotLocalStore.DeleteAccepted(item);
            _failCounts.Remove(item.WebpPath);
            _logger.LogDebug("Screenshot uploaded + local deleted: {File}", Path.GetFileName(item.WebpPath));
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Screenshot upload offline/network error");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Screenshot upload failed");
            return false;
        }
        finally
        {
            try { if (File.Exists(marker)) File.Delete(marker); } catch { /* ignored */ }
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
            return date - DateTimeOffset.UtcNow;
        if (response.Headers.TryGetValues("Retry-After", out var vals))
        {
            var raw = vals.FirstOrDefault();
            if (int.TryParse(raw, out var sec) && sec >= 0)
                return TimeSpan.FromSeconds(Math.Min(sec, 300));
        }
        return null;
    }
}
