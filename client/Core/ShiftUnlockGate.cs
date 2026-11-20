using System.Globalization;
using System.Text.Json;

namespace client.Core;

/// <summary>
/// Decides whether the face soft-lock may unlock right now, based on the employee's
/// shift (mirrored locally by <c>ScheduleCacheService</c> into <c>employee_schedule</c>
/// and <c>company_holidays</c>). Pure logic — no I/O — so it is unit-testable.
///
/// A shift that ends at or before it starts (22:00–06:00) runs into the next day, so at
/// 03:00 the previous day's shift is the one that is open.
/// </summary>
public static class ShiftUnlockGate
{
    public sealed record Schedule(
        string Timezone,
        IReadOnlyDictionary<string, string> WeeklyPattern,
        IReadOnlySet<string> HolidayDates);

    public sealed record Decision(
        bool Allowed,
        string Reason,
        DateTimeOffset? OpensAt,
        DateTimeOffset? ClosesAt,
        TimeZoneInfo Zone);

    /// <param name="earlyMinutes">How many minutes before the shift start unlock is allowed.</param>
    public static Decision Evaluate(DateTimeOffset nowUtc, Schedule? schedule, int earlyMinutes = 0)
    {
        var zone = ResolveZone(schedule?.Timezone);
        // No shift assigned / never synced → do not lock people out.
        if (schedule == null || schedule.WeeklyPattern.Count == 0)
            return new Decision(true, "no_schedule", null, null, zone);

        var early = TimeSpan.FromMinutes(Math.Clamp(earlyMinutes, 0, 240));
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var today = localNow.Date;

        // A shift that started yesterday may still be running (overnight), so check it too.
        foreach (var day in new[] { today.AddDays(-1), today })
        {
            if (!TryWindow(day, schedule, zone, out var start, out var end)) continue;
            if (nowUtc >= start - early && nowUtc < end)
                return new Decision(true, "in_shift", start, end, zone);
        }

        // Closed: find when the next shift opens (look a little over a week ahead).
        for (var i = 0; i <= 8; i++)
        {
            var day = today.AddDays(i);
            if (!TryWindow(day, schedule, zone, out var start, out var end)) continue;
            var opens = start - early;
            if (opens > nowUtc)
                return new Decision(false, "outside_shift", opens, end, zone);
        }
        return new Decision(false, "no_upcoming_shift", null, null, zone);
    }

    /// <summary>Shift window for one local work date, or false when it is a day off / holiday.</summary>
    private static bool TryWindow(
        DateTime localDate, Schedule schedule, TimeZoneInfo zone,
        out DateTimeOffset start, out DateTimeOffset end)
    {
        start = end = default;
        if (schedule.HolidayDates.Contains(localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            return false;
        var key = localDate.DayOfWeek.ToString()[..3].ToLowerInvariant();
        if (!schedule.WeeklyPattern.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !TimeSpan.TryParse(parts[0], CultureInfo.InvariantCulture, out var s)
            || !TimeSpan.TryParse(parts[1], CultureInfo.InvariantCulture, out var e))
            return false;

        var startLocal = localDate + s;
        var endLocal = localDate + e;
        if (endLocal <= startLocal) endLocal = endLocal.AddDays(1);
        start = new DateTimeOffset(startLocal, zone.GetUtcOffset(startLocal));
        end = new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal));
        return true;
    }

    public static TimeZoneInfo ResolveZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
            catch { /* unknown id → local */ }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>Parse the locally cached <c>weekly_pattern</c> JSON (<c>{"mon":"09:00-18:00",…}</c>).</summary>
    public static IReadOnlyDictionary<string, string> ParsePattern(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed != null)
                foreach (var (k, v) in parsed)
                    if (k.Length >= 3 && !string.IsNullOrWhiteSpace(v))
                        result[k[..3].ToLowerInvariant()] = v;
        }
        catch { /* malformed → empty (no gate) */ }
        return result;
    }

    /// <summary>Screen text for a closed gate, in the shift's own timezone.</summary>
    public static string Describe(Decision d, DateTimeOffset nowUtc)
    {
        if (d.OpensAt is not { } opens)
            return "No upcoming shift is scheduled for you. Contact HR.";
        var local = TimeZoneInfo.ConvertTime(opens, d.Zone);
        var wait = opens - nowUtc;
        var waitText = wait.TotalHours >= 1
            ? $"{(int)wait.TotalHours} h {wait.Minutes} min"
            : $"{Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))} min";
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, d.Zone);
        var day = local.Date == nowLocal.Date ? "today"
            : local.Date == nowLocal.Date.AddDays(1) ? "tomorrow"
            : local.ToString("dddd", CultureInfo.InvariantCulture);
        return $"Your shift starts {day} at {local:HH:mm} (in {waitText}). This PC unlocks then.";
    }
}
