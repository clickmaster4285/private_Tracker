using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Measures approximate uplink kbps to ALPHA_SERVER_URL via a small DeviceAuth POST.
/// Used before live-stream publish so thin links fail loudly instead of slideshowing.
/// </summary>
public sealed class NetProbeService
{
    private const int ProbeBytes = 256 * 1024; // 256 KiB
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly HttpClient _http;
    private readonly ILogger<NetProbeService> _logger;
    private readonly object _gate = new();
    private int? _lastUplinkKbps;
    private DateTimeOffset _measuredAt = DateTimeOffset.MinValue;

    public NetProbeService(
        AppConfig config,
        ILogStore store,
        HttpClient http,
        ILogger<NetProbeService> logger)
    {
        _config = config;
        _store = store;
        _http = http;
        _logger = logger;
    }

    /// <summary>Last successful probe result (kbps), or null if never measured.</summary>
    public int? LastUplinkKbps
    {
        get { lock (_gate) return _lastUplinkKbps; }
    }

    /// <summary>
    /// Runs (or returns cached) uplink probe. Cache TTL 10 minutes.
    /// Returns measured kbps, or null on failure.
    /// </summary>
    public async Task<int?> MeasureUplinkKbpsAsync(CancellationToken ct, bool force = false)
    {
        lock (_gate)
        {
            if (!force && _lastUplinkKbps is int cached &&
                DateTimeOffset.UtcNow - _measuredAt < TimeSpan.FromMinutes(10))
                return cached;
        }

        if (string.IsNullOrWhiteSpace(_config.ServerUrl))
            return null;

        EmployeeInfo? employee = null;
        try { employee = await _store.GetEmployeeInfoAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NetProbe: employee lookup failed");
            return null;
        }

        if (employee is null ||
            (string.IsNullOrWhiteSpace(employee.DeviceToken) && string.IsNullOrWhiteSpace(employee.Token)))
            return null;

        var url = _config.ServerUrl.TrimEnd('/') + "/api/v1/live-stream/uplink-probe";
        var payload = new byte[ProbeBytes];
        Random.Shared.NextBytes(payload);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(employee.DeviceToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Device", employee.DeviceToken);
            else
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", employee.Token);
            ClientIdentityHeaders.Apply(req);
            req.Content = new ByteArrayContent(payload);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var sw = Stopwatch.StartNew();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("NetProbe uplink failed HTTP {Status}", (int)resp.StatusCode);
                return null;
            }

            var sec = Math.Max(0.05, sw.Elapsed.TotalSeconds);
            var kbps = (int)Math.Round(ProbeBytes * 8.0 / sec / 1000.0);
            kbps = Math.Clamp(kbps, 1, 100_000);

            lock (_gate)
            {
                _lastUplinkKbps = kbps;
                _measuredAt = DateTimeOffset.UtcNow;
            }

            _logger.LogInformation("NetProbe uplink ≈ {Kbps} kbps ({Bytes} bytes in {Ms:F0} ms)",
                kbps, ProbeBytes, sw.Elapsed.TotalMilliseconds);
            return kbps;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "NetProbe uplink measurement failed");
            return null;
        }
    }

    /// <summary>Pick encode bitrate from measured uplink and configured max.</summary>
    public static uint SelectBitrateKbps(int uplinkKbps, int maxBitrateKbps)
    {
        var cap = Math.Clamp(maxBitrateKbps, 500, 15000);
        // Leave ~30% headroom on the uplink for ICE/RTCP/other traffic.
        var budget = (int)(uplinkKbps * 0.7);
        int tier = budget switch
        {
            >= 15000 => 12000,
            >= 8000 => 6000,
            >= 4000 => 3000,
            >= 2500 => 1500,
            >= 1500 => 1000,
            _ => 500,
        };
        return (uint)Math.Clamp(Math.Min(tier, cap), 500, cap);
    }
}
