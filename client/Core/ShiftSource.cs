using System.Text.Json;
using client.Configuration;
using client.Services;

namespace client.Core;

/// <summary>
/// Where the face lock and end-of-shift check get the employee's shift: first the service's
/// shift store (pipe op <c>GetShift</c> — one source for the whole PC, kept fresh every minute
/// even while the employee is signed out, and kept across restarts / server outages), then the
/// tracker's own cached copy when the service cannot answer.
/// </summary>
public static class ShiftSource
{
    /// <param name="Schedule">The shift; null = no shift known / assigned.</param>
    /// <param name="FetchedAt">When the service last downloaded it from the server (service answers only).</param>
    public sealed record Result(ShiftUnlockGate.Schedule? Schedule, DateTimeOffset? FetchedAt, bool FromService);

    public static async Task<Result> LoadAsync(string employeeId, AppConfig config, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(employeeId))
        {
            try
            {
                var (ok, body) = await DlpPipeClient.SendAsync(
                    new { op = "GetShift", employeeId },
                    connectTimeoutMs: 1_500,
                    responseTimeoutMs: 3_000,
                    ct: ct);
                if (ok && TryParseServiceAnswer(body, out var fromService))
                    return fromService;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* service unreachable → tracker copy */ }
        }
        return new Result(ShiftScheduleReader.Load(employeeId, config), null, false);
    }

    /// <summary>Parse the service's GetShift reply; false when it has nothing for this employee.</summary>
    internal static bool TryParseServiceAnswer(string body, out Result result)
    {
        result = new Result(null, null, true);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("found", out var f) || f.ValueKind != JsonValueKind.True)
                return false;
            DateTimeOffset? fetchedAt = root.TryGetProperty("fetchedAt", out var fa) && fa.TryGetDateTimeOffset(out var at)
                ? at : null;
            if (root.TryGetProperty("noShift", out var ns) && ns.ValueKind == JsonValueKind.True)
            {
                result = new Result(null, fetchedAt, true);
                return true;
            }
            if (!root.TryGetProperty("schedule", out var s) || s.ValueKind != JsonValueKind.Object)
                return false;

            var tz = s.TryGetProperty("timezone", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var pattern = s.TryGetProperty("weeklyPattern", out var wp) && wp.ValueKind == JsonValueKind.Object
                ? ShiftUnlockGate.ParsePattern(wp.GetRawText())
                : ShiftUnlockGate.ParsePattern(null);
            var holidays = new HashSet<string>(StringComparer.Ordinal);
            if (s.TryGetProperty("holidays", out var hs) && hs.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hs.EnumerateArray())
                {
                    // Same rule as the service: only approved holidays close the PC.
                    if (h.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                        && st.GetString() is { Length: > 0 } status
                        && !status.Equals("approved", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (h.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        var date = (d.GetString() ?? "").Trim();
                        holidays.Add(date.Length > 10 ? date[..10] : date);
                    }
                }
            }
            var extensions = s.TryGetProperty("extensions", out var ex)
                ? ShiftUnlockGate.ParseExtensions(ex)
                : null;
            result = new Result(new ShiftUnlockGate.Schedule(tz, pattern, holidays, extensions), fetchedAt, true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
