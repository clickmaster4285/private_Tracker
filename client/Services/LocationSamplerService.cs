using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Phase 3 GPS (finalplan §16 B.1): polls the OS location API on
/// <see cref="AppConfig.LocationPollSec"/> and persists fixes to SQLite for sync.
/// Default OFF — requires ALPHA_LOCATION_ENABLED=true and OS location permission.
/// </summary>
public sealed class LocationSamplerService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly HttpClient _http;
    private readonly ILogger<LocationSamplerService> _logger;

    public LocationSamplerService(
        AppConfig config,
        ILogStore store,
        HttpClient http,
        ILogger<LocationSamplerService> logger)
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
            _logger.LogInformation("LocationSamplerService parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!_config.LocationEnabled)
        {
            _logger.LogInformation("LocationSamplerService: ALPHA_LOCATION_ENABLED=false — parked");
            return;
        }

        _logger.LogInformation(
            "LocationSamplerService starting (poll={PollSec}s)",
            _config.LocationPollSec);

        // First sample shortly after startup so login sync can include a fix.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (client.Core.UpdatePriority.ShouldYield || client.Core.FaceVerifyYield.ShouldYield)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var fix = await LocationProbe.TryGetFixAsync(
                    _http, stoppingToken, _config.LocationIpFallback);
                if (fix.HasValue)
                {
                    var sample = new LocationSample
                    {
                        Latitude = fix.Value.Latitude,
                        Longitude = fix.Value.Longitude,
                        AccuracyM = fix.Value.AccuracyM,
                        AltitudeM = fix.Value.AltitudeM,
                        Source = fix.Value.Source,
                        Address = fix.Value.Address,
                        CapturedAt = DateTime.UtcNow,
                    };
                    await _store.StoreLocationSamplesAsync(new[] { sample }, stoppingToken);
                    _logger.LogDebug(
                        "Location sample stored ({Source}): {Lat:F5}, {Lon:F5}",
                        sample.Source, sample.Latitude, sample.Longitude);
                }
                else
                {
                    _logger.LogDebug("LocationSamplerService: no fix available this poll");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LocationSamplerService: poll failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.LocationPollSec), stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }
}
