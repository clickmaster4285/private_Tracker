using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace client.Core;

/// <summary>
/// Cross-platform location probe (Phase 3 GPS, finalplan §16). Tries the OS geolocation
/// API first; optional IP geolocation fallback (source=ip, coarse — often km off).
/// </summary>
public static class LocationProbe
{
    public readonly record struct LocationFix(
        double Latitude,
        double Longitude,
        double? AccuracyM,
        double? AltitudeM,
        string Source,
        string? Address);

    public static async Task<LocationFix?> TryGetFixAsync(HttpClient http, CancellationToken ct, bool allowIpFallback = true)
    {
        LocationFix? fix = null;
        if (OperatingSystem.IsWindows())
            fix = await TryWindowsAsync(ct);
        else if (OperatingSystem.IsLinux())
            fix = await TryLinuxAsync(ct);
        else if (OperatingSystem.IsMacOS())
            fix = await TryMacOSAsync(ct);

        if (fix.HasValue)
            return fix;

        if (!allowIpFallback)
            return null;

        return await TryIpGeolocationAsync(http, ct);
    }

    // ── Windows: Windows.Devices.Geolocation via PowerShell WinRT bridge ──

    private static async Task<LocationFix?> TryWindowsAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;

        const string script = """
            try {
              Add-Type -AssemblyName System.Runtime.WindowsRuntime
              $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
                $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
              })[0]
              function Await($WinRtTask, $ResultType) {
                $netTask = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($WinRtTask))
                $netTask.Wait(-1) | Out-Null
                $netTask.Result
              }
              [Windows.Devices.Geolocation.Geolocator,Windows.Devices.Geolocation,ContentType=WindowsRuntime] | Out-Null
              $loc = New-Object Windows.Devices.Geolocation.Geolocator
              $loc.DesiredAccuracy = [Windows.Devices.Geolocation.PositionAccuracy]::High
              $pos = Await ($loc.GetGeopositionAsync()) ([Windows.Devices.Geolocation.Geoposition])
              $c = $pos.Coordinate
              Write-Output ("{0},{1},{2},{3}" -f $c.Latitude, $c.Longitude, $c.Accuracy, $c.Altitude)
            } catch { exit 1 }
            """;

        return await Task.Run(() => ParseProbeOutput(RunPowerShell(script), "gps"), ct);
    }

    // ── Linux: GeoClue2 (geoclue-2-demo or busctl) ──

    private static async Task<LocationFix?> TryLinuxAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return null;

        var demoFix = await Task.Run(() => TryGeoclueDemo(), ct);
        if (demoFix.HasValue) return demoFix;

        var desktopId = AppInfo.PackageName;
        var script = """
            set -e
            CLIENT=$(busctl call --system org.freedesktop.GeoClue2 /org/freedesktop/GeoClue2/Manager org.freedesktop.GeoClue2.Manager GetClient s "__DESKTOP_ID__" 2>/dev/null | awk '{print $2}' | tr -d '"')
            [ -n "$CLIENT" ] || exit 1
            busctl set-property --system org.freedesktop.GeoClue2 "$CLIENT" org.freedesktop.GeoClue2.Client DesktopId s "__DESKTOP_ID__" 2>/dev/null || true
            busctl call --system org.freedesktop.GeoClue2 "$CLIENT" org.freedesktop.GeoClue2.Client Start 2>/dev/null || true
            sleep 5
            busctl get-property --system org.freedesktop.GeoClue2 "$CLIENT" org.freedesktop.GeoClue2.Client Location 2>/dev/null
            """.Replace("__DESKTOP_ID__", desktopId);

        return await Task.Run<LocationFix?>(() =>
        {
            var output = RunBash(script);
            return ParseDBusLocation(output, "wifi");
        }, ct);
    }

    private static LocationFix? TryGeoclueDemo()
    {
        if (!IsCommandAvailable("geoclue-2-demo")) return null;

        // -g exits after the first fix; typical line: "New location: 33.568712, 73.137185"
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "geoclue-2-demo",
            Arguments = "-g",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var output = ProcessFilter.RunProbe(psi, 20_000);
        if (string.IsNullOrWhiteSpace(output)) return null;

        var match = Regex.Match(
            output,
            @"(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            return null;

        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return null;
        return new LocationFix(lat, lon, null, null, "gps", null);
    }

    // ── macOS: CoreLocation not wired on net10.0 yet — IP fallback only ──

    private static Task<LocationFix?> TryMacOSAsync(CancellationToken ct) =>
        Task.FromResult<LocationFix?>(null);

    // ── IP geolocation fallback (all platforms) — coarse, often km off ──

    private static async Task<LocationFix?> TryIpGeolocationAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(
                "http://ip-api.com/json/?fields=status,lat,lon,city,regionName,country",
                ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.GetProperty("status").GetString() != "success") return null;

            var lat = root.GetProperty("lat").GetDouble();
            var lon = root.GetProperty("lon").GetDouble();
            var city = root.TryGetProperty("city", out var c) ? c.GetString() : "";
            var region = root.TryGetProperty("regionName", out var r) ? r.GetString() : "";
            var country = root.TryGetProperty("country", out var co) ? co.GetString() : "";
            var address = string.Join(", ", new[] { city, region, country }.Where(s => !string.IsNullOrWhiteSpace(s)));

            return new LocationFix(lat, lon, 5000, null, "ip", string.IsNullOrWhiteSpace(address) ? null : address);
        }
        catch
        {
            return null;
        }
    }

    private static LocationFix? ParseDBusLocation(string? output, string source)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        // busctl: "(ddddd) 33.568712 73.137185 10 0 0" or "d 33.56 d 73.13 ..."
        var nums = new List<double>();
        foreach (var part in output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                nums.Add(v);
        }

        if (nums.Count < 2) return null;
        var lat = nums[0];
        var lon = nums[1];
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return null;

        double? acc = nums.Count > 2 ? nums[2] : null;
        double? alt = nums.Count > 3 ? nums[3] : null;
        return new LocationFix(lat, lon, acc, alt, source, null);
    }

    private static LocationFix? ParseProbeOutput(string? output, string source)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var line = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(line)) return null;

        var parts = line.Split(',');
        if (parts.Length < 2) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return null;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return null;

        double? acc = parts.Length > 2 &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var accuracy)
            ? accuracy : null;
        double? alt = parts.Length > 3 &&
            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var altitude)
            ? altitude : null;

        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return null;
        return new LocationFix(lat, lon, acc, alt, source, null);
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sh",
                Arguments = $"-c \"command -v {command}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(2000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string? RunPowerShell(string script)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command {QuoteForShell(script)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return ProcessFilter.RunProbe(psi, 30_000);
    }

    private static string? RunBash(string script)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c {QuoteForShell(script)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return ProcessFilter.RunProbe(psi, 20_000);
    }

    private static string QuoteForShell(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}
