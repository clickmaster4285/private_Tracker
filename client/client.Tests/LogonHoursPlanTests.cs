using AlphaAITracker.Service;
using Xunit;

namespace client.Tests;

public class LogonHoursPlanTests
{
    private static LogonHoursPlan.Shift Day(params string[] holidays) => new(
        "Asia/Karachi",
        new Dictionary<string, string>
        {
            ["mon"] = "09:00-10:47", ["tue"] = "09:00-10:47", ["wed"] = "09:00-10:47",
            ["thu"] = "09:00-10:47", ["fri"] = "09:00-10:47",
        },
        new HashSet<string>(holidays));

    // Karachi = UTC+5. Friday 2026-11-20.
    private static DateTimeOffset Pkt(int day, int h, int m = 0) => new(2026, 11, day, h, m, 0, TimeSpan.FromHours(5));

    [Fact]
    public void DayShift_AllowsOnlyShiftHours()
    {
        var mask = LogonHoursPlan.Compute(Pkt(20, 8, 0), Day());
        Assert.False(LogonHoursPlan.IsAllowed(mask, Pkt(20, 8, 30)));   // before shift
        Assert.True(LogonHoursPlan.IsAllowed(mask, Pkt(20, 9, 0)));
        Assert.True(LogonHoursPlan.IsAllowed(mask, Pkt(20, 10, 50)));   // hour 10 overlaps 10:00–10:47
        Assert.False(LogonHoursPlan.IsAllowed(mask, Pkt(20, 11, 0)));   // after shift
        Assert.False(LogonHoursPlan.IsAllowed(mask, Pkt(21, 9, 30)));   // Saturday off
        Assert.True(LogonHoursPlan.IsAllowed(mask, Pkt(23, 9, 30)));    // next Monday
    }

    [Fact]
    public void CurrentHour_ClosesAtShiftEnd_NotAtTheHour()
    {
        // Shift ends 10:47. At 10:40 the hour is open; at 10:48 it is shut, even though
        // Windows hours are whole hours.
        Assert.True(LogonHoursPlan.IsAllowed(LogonHoursPlan.Compute(Pkt(20, 10, 40), Day()), Pkt(20, 10, 40)));
        Assert.False(LogonHoursPlan.IsAllowed(LogonHoursPlan.Compute(Pkt(20, 10, 48), Day()), Pkt(20, 10, 48)));
    }

    [Fact]
    public void CurrentHour_OpensOnlyAtShiftStart()
    {
        var halfPast = new LogonHoursPlan.Shift("Asia/Karachi",
            new Dictionary<string, string> { ["fri"] = "09:30-17:00" }, new HashSet<string>());
        Assert.False(LogonHoursPlan.IsAllowed(LogonHoursPlan.Compute(Pkt(20, 9, 10), halfPast), Pkt(20, 9, 10)));
        Assert.True(LogonHoursPlan.IsAllowed(LogonHoursPlan.Compute(Pkt(20, 9, 31), halfPast), Pkt(20, 9, 31)));
    }

    [Fact]
    public void Holiday_BlocksThatDay()
    {
        var mask = LogonHoursPlan.Compute(Pkt(20, 8, 0), Day("2026-11-23"));
        Assert.False(LogonHoursPlan.IsAllowed(mask, Pkt(23, 9, 30)));
        Assert.True(LogonHoursPlan.IsAllowed(mask, Pkt(24, 9, 30)));
    }

    [Fact]
    public void NightShift_CrossesMidnight()
    {
        var night = new LogonHoursPlan.Shift("Asia/Karachi",
            new Dictionary<string, string> { ["fri"] = "22:00-06:00" }, new HashSet<string>());
        var mask = LogonHoursPlan.Compute(Pkt(20, 12, 0), night);
        Assert.True(LogonHoursPlan.IsAllowed(mask, Pkt(20, 23, 0)));
        Assert.True(LogonHoursPlan.IsAllowed(mask, Pkt(21, 5, 30)));
        Assert.False(LogonHoursPlan.IsAllowed(mask, Pkt(21, 6, 0)));
        Assert.False(LogonHoursPlan.IsAllowed(mask, Pkt(20, 21, 0)));
    }

    [Fact]
    public void ParseShift_ReadsServerPayload_AndSkipsUnapprovedHolidays()
    {
        var s = LogonHoursWorker.ParseShift("""
            {"timezone":"Asia/Karachi","weeklyPattern":{"mon":"09:00-17:00"},
             "holidays":[{"date":"2026-11-23","status":"approved"},{"date":"2026-11-24","status":"pending"}]}
            """);
        Assert.NotNull(s);
        Assert.Equal("09:00-17:00", s!.WeeklyPattern["mon"]);
        Assert.Contains("2026-11-23", s.HolidayDates);
        Assert.DoesNotContain("2026-11-24", s.HolidayDates);
    }
}
