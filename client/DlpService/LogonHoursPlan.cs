using System.Globalization;

namespace AlphaAITracker.Service;

/// <summary>
/// Pure logic: turn an employee's shift into Windows logon hours.
///
/// Windows stores logon hours as 21 bytes = 168 bits, one per hour of the week, in UTC,
/// starting Sunday 00:00 UTC (bit 0 = Sun 00:00–01:00 UTC, little-endian bit order within
/// each byte). We fill the next 168 hours from "now" against the real calendar, so holidays
/// and day-offs in the coming week are honoured. An hour is allowed when it overlaps the shift
/// window at all (hour granularity — the tracker's end-of-shift sign-out handles the minutes).
/// </summary>
internal static class LogonHoursPlan
{
    public sealed record Shift(
        string Timezone,
        IReadOnlyDictionary<string, string> WeeklyPattern, // "mon" → "09:00-17:00"
        IReadOnlySet<string> HolidayDates);                  // "yyyy-MM-dd" in the shift timezone

    public const int Bytes = 21;

    /// <summary>All 168 hours allowed (Windows default).</summary>
    public static byte[] AllHours()
    {
        var b = new byte[Bytes];
        Array.Fill(b, (byte)0xFF);
        return b;
    }

    public static byte[] Compute(DateTimeOffset nowUtc, Shift shift, int earlyMinutes = 0)
    {
        var zone = ResolveZone(shift.Timezone);
        var early = TimeSpan.FromMinutes(Math.Clamp(earlyMinutes, 0, 240));
        var mask = new byte[Bytes];
        var hour0 = new DateTimeOffset(nowUtc.UtcDateTime.Date.AddHours(nowUtc.UtcDateTime.Hour), TimeSpan.Zero);

        // Every shift window that can touch the next 168 h (one day of slack each side).
        var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var firstLocal = TimeZoneInfo.ConvertTime(hour0, zone).Date.AddDays(-1);
        for (var d = 0; d <= 9; d++)
        {
            if (TryWindow(firstLocal.AddDays(d), shift, zone, out var s, out var e))
                windows.Add((s - early, e));
        }

        for (var h = 0; h < 168; h++)
        {
            var slotStart = hour0.AddHours(h);
            var slotEnd = slotStart.AddHours(1);
            // The current hour is decided by "is the shift open right now", not "does the shift
            // touch this hour": otherwise a shift ending at 15:39 leaves 15:39–16:00 open in
            // Windows. The worker re-runs every minute, so the hour opens at the shift start
            // and closes at the shift end to within about a minute.
            var allowed = h == 0
                ? windows.Any(w => w.Start <= nowUtc && w.End > nowUtc)
                : windows.Any(w => w.Start < slotEnd && w.End > slotStart);
            if (!allowed) continue;
            var bit = (int)slotStart.UtcDateTime.DayOfWeek * 24 + slotStart.UtcDateTime.Hour;
            mask[bit / 8] |= (byte)(1 << (bit % 8));
        }
        return mask;
    }

    /// <summary>Is <paramref name="utc"/> inside an allowed hour of <paramref name="mask"/>?</summary>
    public static bool IsAllowed(byte[] mask, DateTimeOffset utc)
    {
        var bit = (int)utc.UtcDateTime.DayOfWeek * 24 + utc.UtcDateTime.Hour;
        return (mask[bit / 8] & (1 << (bit % 8))) != 0;
    }

    private static bool TryWindow(DateTime localDate, Shift shift, TimeZoneInfo zone,
        out DateTimeOffset start, out DateTimeOffset end)
    {
        start = end = default;
        if (shift.HolidayDates.Contains(localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            return false;
        var key = localDate.DayOfWeek.ToString()[..3].ToLowerInvariant();
        if (!shift.WeeklyPattern.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !TimeSpan.TryParse(parts[0], CultureInfo.InvariantCulture, out var s)
            || !TimeSpan.TryParse(parts[1], CultureInfo.InvariantCulture, out var e))
            return false;
        var startLocal = localDate + s;
        var endLocal = localDate + e;
        if (endLocal <= startLocal) endLocal = endLocal.AddDays(1); // overnight shift
        start = new DateTimeOffset(startLocal, zone.GetUtcOffset(startLocal));
        end = new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal));
        return true;
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
            catch { /* unknown → local */ }
        }
        return TimeZoneInfo.Local;
    }
}
