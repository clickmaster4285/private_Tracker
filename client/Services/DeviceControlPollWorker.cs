using System.Net.Http.Headers;
using System.Text.Json;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Linux/macOS Fixed/Roaming control poll (GET /api/v1/devices/control).
/// Runs in the user tracker process — cooperative; not LocalSystem-owned.
/// Windows keeps <c>TrackingControlWorker</c> inside DlpService.
/// </summary>
public sealed class DeviceControlPollWorker : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly HttpClient _http;
    private readonly ILogger<DeviceControlPollWorker> _logger;
    private readonly object _pullGate = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private int _pullRequested;
    private bool _pullRunning;
    private bool _loggedWaiting;

    /// <summary>Static wake so SyncService can request a pull without DI cycles.</summary>
    private static DeviceControlPollWorker? _instance;

    public DeviceControlPollWorker(
        AppConfig config,
        ILogStore store,
        HttpClient http,
        ILogger<DeviceControlPollWorker> logger)
    {
        _config = config;
        _store = store;
        _http = http;
        _logger = logger;
    }

    public void RequestImmediateCheck()
    {
        Interlocked.Exchange(ref _pullRequested, 1);
        _wake.Set();
    }

    /// <summary>Wake the live worker from sync <c>controlEpoch</c> (no-op if not started).</summary>
    public static void RequestImmediateCheckStatic()
    {
        _instance?.RequestImmediateCheck();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (OperatingSystem.IsWindows())
        {
            _logger.LogInformation("DeviceControlPollWorker parked (Windows uses DlpService)");
            return;
        }

        if (ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("DeviceControlPollWorker parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        _instance = this;
        var pollSec = Math.Clamp(_config.ControlPollSec, 60, 24 * 3600);
        _logger.LogInformation(
            "DeviceControlPollWorker starting (pollSec={Poll}, path={Path})",
            pollSec, TrackingControlPaths.ControlJsonPath);

        var pullOk = await TryPullOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var forced = Interlocked.Exchange(ref _pullRequested, 0) != 0;
            if (!forced)
            {
                var baseSec = pullOk ? pollSec : Math.Min(pollSec, 60);
                var jitter = 1.0 + (Random.Shared.NextDouble() * 0.4 - 0.2);
                var delay = TimeSpan.FromSeconds(Math.Max(15, baseSec * jitter));
                try
                {
                    _wake.Reset();
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
                _logger.LogInformation("DeviceControlPollWorker woken for immediate pull");

            pullOk = await TryPullOnceAsync(stoppingToken);
        }

        if (ReferenceEquals(_instance, this))
            _instance = null;
    }

    private async Task<bool> TryPullOnceAsync(CancellationToken ct)
    {
        lock (_pullGate)
        {
            if (_pullRunning) return false;
            _pullRunning = true;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(_config.ServerUrl))
            {
                if (!_loggedWaiting)
                {
                    _logger.LogInformation("DeviceControlPollWorker waiting for ServerUrl");
                    _loggedWaiting = true;
                }
                return false;
            }

            var employee = await _store.GetEmployeeInfoAsync(ct).ConfigureAwait(false);
            var deviceToken = employee?.DeviceToken;
            if (string.IsNullOrWhiteSpace(deviceToken))
            {
                if (!_loggedWaiting)
                {
                    _logger.LogInformation("DeviceControlPollWorker waiting for device credentials");
                    _loggedWaiting = true;
                }
                return false;
            }
            _loggedWaiting = false;

            return await PullOnceAsync(deviceToken!, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device control poll failed");
            UserTrackingControlStore.MarkCheck(ex.Message);
            return false;
        }
        finally
        {
            lock (_pullGate) { _pullRunning = false; }
        }
    }

    private async Task<bool> PullOnceAsync(string deviceToken, CancellationToken ct)
    {
        var snap = UserTrackingControlStore.Snapshot();
        var url = $"{_config.ServerUrl!.TrimEnd('/')}/api/v1/devices/control?sinceEpoch={snap.ControlEpoch}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Device", deviceToken);
        req.Headers.TryAddWithoutValidation("X-Client-Platform", "linux");
        req.Headers.TryAddWithoutValidation("X-Client-Version", AppInfo.Version);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            UserTrackingControlStore.MarkCheck($"http_{(int)resp.StatusCode}");
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
            UserTrackingControlStore.ApplyServerControl(
                mode, source, stopWindow, requireReason, keepDlp, maxHours, desired, phase, epoch);
            _logger.LogInformation(
                "Device control applied mode={Mode} source={Source} epoch={Epoch} unchanged={Unchanged}",
                mode, source, epoch, unchanged);
        }
        else
        {
            UserTrackingControlStore.MarkCheck();
        }
        return true;
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= 200 ? s : s[..200];
}
