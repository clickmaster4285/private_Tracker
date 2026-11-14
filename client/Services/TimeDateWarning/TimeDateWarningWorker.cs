using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;

namespace client.Services.TimeDateWarning;

/// <summary>
/// On tracker start, then every hour: compare OS clock/timezone to server UTC and
/// the location-derived IANA zone (IP geo, else shift schedule). When unhealthy,
/// write <c>timedatewarning/state.json</c> and spawn <c>--timedatewarning</c>.
/// Never changes the OS clock.
/// </summary>
public sealed class TimeDateWarningWorker : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly HttpClient _http;
    private readonly ILogger<TimeDateWarningWorker> _logger;

    public TimeDateWarningWorker(
        AppConfig config,
        ILogStore store,
        HttpClient http,
        ILogger<TimeDateWarningWorker> logger)
    {
        _config = config;
        _store = store;
        _http = http;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("TimeDateWarningWorker parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!_config.TaEnabled || !_config.TimeDateWarnEnabled)
        {
            _logger.LogInformation(
                "TimeDateWarningWorker: parked (TaEnabled={Ta} WarnEnabled={Warn})",
                _config.TaEnabled, _config.TimeDateWarnEnabled);
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _config.TimeDateCheckHours));
        _logger.LogInformation(
            "TimeDateWarningWorker starting (first check now, then every {Hours}h; skew>={Skew}s)",
            interval.TotalHours, _config.TimeDateSkewSec);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (client.Core.UpdatePriority.ShouldYield || client.Core.FaceVerifyYield.ShouldYield)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                await CheckAndMaybeWarnAsync(stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TimeDateWarningWorker: check failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task CheckAndMaybeWarnAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.ServerUrl))
        {
            _logger.LogDebug("TimeDateWarningWorker: no ALPHA_SERVER_URL — skip");
            return;
        }

        var skew = await MeasureSkewSecondsAsync(ct);
        if (skew is null)
        {
            _logger.LogDebug("TimeDateWarningWorker: server-time measure failed — skip");
            return;
        }

        var (expectedTzId, locationSource) = await ResolveExpectedTimeZoneAsync(ct);
        var osTz = TimeZoneInfo.Local;
        var utcNow = DateTime.UtcNow;
        var osLocal = TimeZoneInfo.ConvertTimeFromUtc(utcNow, osTz);

        string? expectedLocalStr = null;
        var timezoneMismatch = false;
        if (!string.IsNullOrWhiteSpace(expectedTzId))
        {
            if (TryFindTimeZone(expectedTzId!, out var expectedTz))
            {
                var expectedLocal = TimeZoneInfo.ConvertTimeFromUtc(utcNow, expectedTz!);
                expectedLocalStr = expectedLocal.ToString("yyyy-MM-dd HH:mm:ss");
                var osOffset = osTz.GetUtcOffset(utcNow);
                var expectedOffset = expectedTz!.GetUtcOffset(utcNow);
                timezoneMismatch = osOffset != expectedOffset;
            }
            else
            {
                _logger.LogDebug(
                    "TimeDateWarningWorker: could not resolve expected TZ {Tz}",
                    expectedTzId);
            }
        }

        var skewBad = Math.Abs(skew.Value) >= _config.TimeDateSkewSec;
        var reasons = new List<string>();
        if (skewBad) reasons.Add("clock_skew");
        if (timezoneMismatch) reasons.Add("timezone_mismatch");

        if (reasons.Count == 0)
        {
            _logger.LogDebug(
                "TimeDateWarningWorker: healthy (skew={Skew}s tz={Os})",
                skew.Value, osTz.Id);
            return;
        }

        var serverUtc = utcNow.AddSeconds(skew.Value);
        var state = new TimeDateWarningState
        {
            CheckedAt = DateTime.UtcNow.ToString("O"),
            ServerUtc = serverUtc.ToString("O"),
            LocalUtc = utcNow.ToString("O"),
            SkewSeconds = Math.Round(skew.Value, 0),
            OsTimeZoneId = osTz.Id,
            ExpectedTimeZoneId = expectedTzId,
            ExpectedLocalNow = expectedLocalStr,
            OsLocalNow = osLocal.ToString("yyyy-MM-dd HH:mm:ss"),
            LocationSource = locationSource,
            Reasons = reasons.ToArray(),
            DismissedAt = null,
        };

        // Preserve an in-cooldown dismiss so we do not clear it on rewrite.
        var prior = TimeDateWarningState.TryRead();
        if (prior?.DismissedAt is { Length: > 0 } dismissedAt
            && DateTime.TryParse(dismissedAt, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dismissedUtc)
            && DateTime.UtcNow - dismissedUtc.ToUniversalTime() <
               TimeSpan.FromHours(Math.Max(1, _config.TimeDateCooldownHours)))
        {
            state.DismissedAt = prior.DismissedAt;
            TimeDateWarningState.Write(state);
            _logger.LogInformation(
                "TimeDateWarningWorker: unhealthy ({Reasons}) but dismiss cooldown active — not spawning",
                string.Join(",", reasons));
            return;
        }

        TimeDateWarningState.Write(state);

        if (TimeDateWarningState.IsWarningProcessRunning())
        {
            _logger.LogInformation(
                "TimeDateWarningWorker: unhealthy ({Reasons}) — warning window already open",
                string.Join(",", reasons));
            return;
        }

        LaunchTimeDateWarningProcess();
        _logger.LogInformation(
            "TimeDateWarningWorker: spawned --timedatewarning ({Reasons}, skew={Skew}s)",
            string.Join(",", reasons), state.SkewSeconds);
    }

    private async Task<double?> MeasureSkewSecondsAsync(CancellationToken ct)
    {
        var beforeUtc = DateTime.UtcNow;
        var baseUrl = _config.ServerUrl?.TrimEnd('/') ?? "";
        if (baseUrl.Length == 0) return null;
        var endpoint = $"{baseUrl}/api/v1/server-time";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await _http.SendAsync(request, ct);
        var afterUtc = DateTime.UtcNow;
        response.EnsureSuccessStatusCode();

        var dateHeader = response.Headers.Date;
        if (!dateHeader.HasValue) return null;

        var serverUtc = dateHeader.Value.UtcDateTime;
        var clientMidpoint = beforeUtc + (afterUtc - beforeUtc) / 2;
        return (serverUtc - clientMidpoint).TotalSeconds;
    }

    private async Task<(string? TzId, string? Source)> ResolveExpectedTimeZoneAsync(CancellationToken ct)
    {
        try
        {
            var ip = await LocationProbe.TryGetIpTimeZoneAsync(_http, ct);
            if (ip.HasValue && !string.IsNullOrWhiteSpace(ip.Value.TimeZoneId))
                return (ip.Value.TimeZoneId, "ip");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TimeDateWarningWorker: IP timezone probe failed");
        }

        try
        {
            var schedules = await _store.ListEmployeeSchedulesAsync(ct);
            var first = schedules.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first.Timezone))
                return (first.Timezone, "schedule");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TimeDateWarningWorker: schedule TZ read failed");
        }

        return (null, null);
    }

    private static bool TryFindTimeZone(string id, out TimeZoneInfo? tz)
    {
        tz = null;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        // Windows may need the IANA→Windows map via the built-in conversion on modern .NET.
        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                return true;
            }
        }
        catch { /* ignore */ }

        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId))
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
                return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    private static void LaunchTimeDateWarningProcess()
    {
        try
        {
            LinuxDesktopEnvironment.Refresh();
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--timedatewarning",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[client] Failed to launch --timedatewarning: {ex.Message}");
        }
    }
}
