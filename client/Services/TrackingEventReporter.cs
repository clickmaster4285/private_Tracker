using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Queues Start/Stop clicks locally, then POSTs to <c>/api/v1/devices/tracking-events</c>
/// (DeviceAuth). Survives offline Roaming control; only removes rows the server accepts.
/// </summary>
public sealed class TrackingEventReporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly object Gate = new();

    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly HttpClient _http;
    private readonly ILogger<TrackingEventReporter> _logger;
    private int _flushInFlight;

    public TrackingEventReporter(
        AppConfig config,
        ILogStore store,
        HttpClient http,
        ILogger<TrackingEventReporter> logger)
    {
        _config = config;
        _store = store;
        _http = http;
        _logger = logger;
    }

    private static string QueuePath
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInfo.DataFolderName, "control");
            return Path.Combine(root, "pending-tracking-events.json");
        }
    }

    /// <summary>Maps client drain wording onto the server CHECK vocabulary.</summary>
    public static string? NormalizeFlushResult(string? flushResult)
    {
        if (string.IsNullOrWhiteSpace(flushResult))
            return null;
        return flushResult.Trim().ToLowerInvariant() switch
        {
            "ok" or "complete" or "completed" => "complete",
            "timeout" or "partial" or "skipped" => "partial",
            "pending" => "pending",
            _ => null,
        };
    }

    public Task ReportAsync(
        string eventType,
        string? reason = null,
        string? flushResult = null,
        CancellationToken ct = default)
    {
        var et = (eventType ?? "").Trim().ToLowerInvariant();
        if (et is not ("start" or "stop"))
            return Task.CompletedTask;

        var entry = new PendingEvent
        {
            ClientEventId = Guid.NewGuid().ToString("D"),
            EventType = et,
            OccurredAt = DateTime.UtcNow.ToString("o"),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            FlushResult = NormalizeFlushResult(flushResult),
            ClientVersion = AppInfo.Version,
        };

        Enqueue(entry);
        return FlushPendingAsync(ct);
    }

    /// <summary>Retry any previously queued Start/Stop rows (offline / failed POST).</summary>
    public Task FlushPendingAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _flushInFlight, 1, 0) != 0)
            return Task.CompletedTask;
        return FlushPendingCoreAsync(ct);
    }

    private async Task FlushPendingCoreAsync(CancellationToken ct)
    {
        try
        {
            var pending = ReadQueue();
            if (pending.Count == 0)
                return;

            var emp = await _store.GetEmployeeInfoAsync(ct).ConfigureAwait(false);
            var token = emp?.DeviceToken;
            if (string.IsNullOrWhiteSpace(token))
                return;

            var serverUrl = (_config.ServerUrl ?? "").TrimEnd('/');
            if (string.IsNullOrWhiteSpace(serverUrl))
                return;

            var payload = new { events = pending };
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"{serverUrl}/api/v1/devices/tracking-events");
            req.Headers.Authorization = new AuthenticationHeaderValue("Device", token);
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "tracking-events flush → HTTP {Status} (keeping {Count} pending)",
                    (int)resp.StatusCode, pending.Count);
                return;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var remove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("acceptedIds", out var accepted) && accepted.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in accepted.EnumerateArray())
                {
                    var s = id.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        remove.Add(s);
                }
            }
            // Rejected ids are poison for this queue (bad mode / invalid) — drop them too.
            if (root.TryGetProperty("rejectedIds", out var rejected) && rejected.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in rejected.EnumerateArray())
                {
                    var s = id.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        remove.Add(s);
                }
            }

            if (remove.Count == 0)
            {
                // Old server without acceptedIds: treat whole batch as sent (idempotent upserts).
                ClearQueue();
                _logger.LogInformation("tracking-events flushed {Count} (no acceptedIds — mark all)", pending.Count);
                return;
            }

            lock (Gate)
            {
                var left = ReadQueueUnlocked().Where(e => !remove.Contains(e.ClientEventId)).ToList();
                WriteQueueUnlocked(left);
            }
            _logger.LogInformation(
                "tracking-events flushed accepted/rejected={Removed} remaining={Left}",
                remove.Count, Math.Max(0, pending.Count - remove.Count));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "tracking-events flush failed — keeping pending queue");
        }
        finally
        {
            Interlocked.Exchange(ref _flushInFlight, 0);
        }
    }

    private void Enqueue(PendingEvent entry)
    {
        lock (Gate)
        {
            var list = ReadQueueUnlocked();
            list.Add(entry);
            // Soft cap — keep newest (Start/Stop volume is tiny).
            const int max = 500;
            if (list.Count > max)
                list = list.Skip(list.Count - max).ToList();
            WriteQueueUnlocked(list);
        }
    }

    private List<PendingEvent> ReadQueue()
    {
        lock (Gate)
            return ReadQueueUnlocked();
    }

    private static List<PendingEvent> ReadQueueUnlocked()
    {
        try
        {
            if (!File.Exists(QueuePath))
                return new List<PendingEvent>();
            var json = File.ReadAllText(QueuePath);
            if (string.IsNullOrWhiteSpace(json))
                return new List<PendingEvent>();
            return JsonSerializer.Deserialize<List<PendingEvent>>(json, JsonOpts)
                   ?? new List<PendingEvent>();
        }
        catch
        {
            return new List<PendingEvent>();
        }
    }

    private static void WriteQueueUnlocked(List<PendingEvent> list)
    {
        var dir = Path.GetDirectoryName(QueuePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = QueuePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts));
        File.Copy(tmp, QueuePath, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    private static void ClearQueue()
    {
        lock (Gate)
            WriteQueueUnlocked(new List<PendingEvent>());
    }

    private sealed class PendingEvent
    {
        public string ClientEventId { get; set; } = "";
        public string EventType { get; set; } = "";
        public string OccurredAt { get; set; } = "";
        public string? Reason { get; set; }
        public string? FlushResult { get; set; }
        public string? ClientVersion { get; set; }
    }
}
