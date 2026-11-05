using System.Net.Http.Headers;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Hourly DeviceAuth control poll (GET /api/v1/devices/control). Independent of
/// ALPHA_DLP_ENABLED — Fixed/Roaming must reach stopped machines that still have credentials.
/// P0: cache only; no Start/Stop transitions yet.
/// </summary>
public sealed class TrackingControlWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly TrackingControlStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TrackingControlWorker> _logger;
    private readonly object _pullGate = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private int _pullRequested;
    private bool _pullRunning;
    private bool _loggedWaiting;

    public TrackingControlWorker(
        DlpConfig cfg,
        TrackingControlStore store,
        IHttpClientFactory httpFactory,
        ILogger<TrackingControlWorker> logger)
    {
        _cfg = cfg;
        _store = store;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// Wake from sync <c>controlEpoch</c> / pipe <c>RefreshControl</c>.
    /// Uses a pending flag so a Set during an in-flight pull is not lost by the
    /// next loop's <see cref="ManualResetEventSlim.Reset"/>.
    /// </summary>
    public void RequestImmediateCheck()
    {
        Interlocked.Exchange(ref _pullRequested, 1);
        _wake.Set();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("TrackingControlWorker parked (Windows-only)");
            return;
        }

        _store.LoadOrDefault();
        var pollSec = Math.Clamp(_cfg.ControlPollSec, 60, 24 * 3600);
        _logger.LogInformation(
            "TrackingControlWorker starting (pollSec={Poll}, mode={Mode})",
            pollSec, _store.Snapshot().Mode);

        // Startup check when credentials exist.
        var pullOk = await TryPullOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Skip the wait when a wake arrived during the previous pull (Reset race).
            var forced = Interlocked.Exchange(ref _pullRequested, 0) != 0;
            if (!forced)
            {
                // After a failed pull, retry in ~60s so Fixed↔Roaming is not stuck for a
                // full hour when the API was briefly down (common after admin mode flips).
                var baseSec = pullOk ? pollSec : Math.Min(pollSec, 60);
                var jitter = 1.0 + (Random.Shared.NextDouble() * 0.4 - 0.2); // ±20%
                var delay = TimeSpan.FromSeconds(Math.Max(15, baseSec * jitter));
                try
                {
                    _wake.Reset();
                    // Re-check after Reset — Set may have landed between Exchange and Reset.
                    if (Volatile.Read(ref _pullRequested) != 0)
                    {
                        Interlocked.Exchange(ref _pullRequested, 0);
                        forced = true;
                    }
                    else
                    {
                        var signaled = await Task.Run(
                            () => _wake.Wait((int)delay.TotalMilliseconds, stoppingToken),
                            stoppingToken);
                        Interlocked.Exchange(ref _pullRequested, 0);
                        forced = signaled;
                    }
                }
                catch (OperationCanceledException) { break; }
            }

            if (forced)
                _logger.LogInformation("TrackingControlWorker woken for immediate pull");

            pullOk = await TryPullOnceAsync(stoppingToken);
        }
    }

    /// <returns>true when the HTTP control pull applied or confirmed unchanged.</returns>
    private async Task<bool> TryPullOnceAsync(CancellationToken ct)
    {
        lock (_pullGate)
        {
            if (_pullRunning) return false;
            _pullRunning = true;
        }

        try
        {
            _cfg.RefreshCredentials();
            if (string.IsNullOrWhiteSpace(_cfg.ServerUrl) || string.IsNullOrWhiteSpace(_cfg.DeviceToken))
            {
                if (!_loggedWaiting)
                {
                    _logger.LogInformation("TrackingControlWorker waiting for device credentials");
                    _loggedWaiting = true;
                }
                return false;
            }
            _loggedWaiting = false;

            return await PullOnceAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device control poll failed");
            _store.MarkCheck(ex.Message);
            return false;
        }
        finally
        {
            lock (_pullGate) { _pullRunning = false; }
        }
    }

    private async Task<bool> PullOnceAsync(CancellationToken ct)
    {
        var snap = _store.Snapshot();
        var client = _httpFactory.CreateClient("dlp");
        var url = $"{_cfg.ServerUrl}/api/v1/devices/control?sinceEpoch={snap.ControlEpoch}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Device", _cfg.DeviceToken);
        req.Headers.TryAddWithoutValidation("X-Client-Platform", "windows");

        using var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _store.MarkCheck($"http_{(int)resp.StatusCode}");
            _logger.LogWarning("Device control HTTP {Status}: {Body}", (int)resp.StatusCode, Truncate(body));
            return false;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var unchanged = root.TryGetProperty("unchanged", out var u) && u.ValueKind == JsonValueKind.True;
        var epoch = root.TryGetProperty("controlEpoch", out var e) && e.TryGetInt64(out var ep) ? ep : snap.ControlEpoch;
        var mode = root.TryGetProperty("trackingMode", out var m) ? m.GetString() ?? "fixed" : "fixed";
        var source = root.TryGetProperty("effectiveModeSource", out var s) ? s.GetString() : null;
        var stopWindow = root.TryGetProperty("stopWindow", out var sw) ? sw.GetString() ?? "anytime" : "anytime";
        var requireReason = root.TryGetProperty("requireStopReason", out var rr) && rr.ValueKind == JsonValueKind.True;
        var keepDlp = !root.TryGetProperty("keepDlpWhenStopped", out var kd) || kd.ValueKind != JsonValueKind.False;
        int? maxHours = null;
        if (root.TryGetProperty("maxStoppedHours", out var mh) && mh.ValueKind == JsonValueKind.Number && mh.TryGetInt32(out var mhv))
            maxHours = mhv;
        var desired = root.TryGetProperty("desiredState", out var ds) ? ds.GetString() ?? "running" : "running";
        var phase = root.TryGetProperty("phase", out var ph) ? ph.GetString() ?? "collecting" : "collecting";

        if (!unchanged || epoch != snap.ControlEpoch || !string.Equals(mode, snap.Mode, StringComparison.OrdinalIgnoreCase))
        {
            _store.ApplyServerControl(mode, source, stopWindow, requireReason, keepDlp, maxHours, desired, phase, epoch);
            _logger.LogInformation(
                "Device control applied mode={Mode} source={Source} epoch={Epoch} unchanged={Unchanged}",
                mode, source, epoch, unchanged);
        }
        else
        {
            _store.MarkCheck();
        }
        return true;
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= 200 ? s : s[..200];
}
