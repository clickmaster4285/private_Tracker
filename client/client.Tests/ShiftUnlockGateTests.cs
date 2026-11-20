using client.Core;
using Xunit;

namespace client.Tests;

public class ShiftUnlockGateTests
{
    private static ShiftUnlockGate.Schedule Sched(string pattern, params string[] holidays) =>
        new("UTC",
            ShiftUnlockGate.ParsePattern(pattern),
            new HashSet<string>(holidays));

    private const string WeekdayDay = """{"mon":"09:00-17:00","tue":"09:00-17:00","wed":"09:00-17:00","thu":"09:00-17:00","fri":"09:00-17:00"}""";
    private const string WeekdayNight = """{"mon":"22:00-06:00","tue":"22:00-06:00","wed":"22:00-06:00","thu":"22:00-06:00","fri":"22:00-06:00"}""";

    // 2026-11-19 is a Thursday.
    private static DateTimeOffset At(int day, int h, int m = 0) => new(2026, 11, day, h, m, 0, TimeSpan.Zero);

    [Fact]
    public void DayShift_InsideWindow_Unlocks()
    {
        Assert.True(ShiftUnlockGate.Evaluate(At(19, 9, 0), Sched(WeekdayDay)).Allowed);
        Assert.True(ShiftUnlockGate.Evaluate(At(19, 16, 59), Sched(WeekdayDay)).Allowed);
    }

    [Fact]
    public void DayShift_BeforeStart_StaysLocked_AndSaysWhen()
    {
        var now = At(19, 8, 30);
        var d = ShiftUnlockGate.Evaluate(now, Sched(WeekdayDay));
        Assert.False(d.Allowed);
        Assert.Equal(At(19, 9, 0), d.OpensAt);
        Assert.Contains("today at 09:00", ShiftUnlockGate.Describe(d, now));
    }

    [Fact]
    public void DayShift_AfterEnd_OpensTomorrow()
    {
        var now = At(19, 17, 0);
        var d = ShiftUnlockGate.Evaluate(now, Sched(WeekdayDay));
        Assert.False(d.Allowed);
        Assert.Equal(At(20, 9, 0), d.OpensAt);
        Assert.Contains("tomorrow at 09:00", ShiftUnlockGate.Describe(d, now));
    }

    [Fact]
    public void EarlyMinutes_AllowUnlockBeforeStart()
    {
        Assert.False(ShiftUnlockGate.Evaluate(At(19, 8, 50), Sched(WeekdayDay)).Allowed);
        Assert.True(ShiftUnlockGate.Evaluate(At(19, 8, 50), Sched(WeekdayDay), earlyMinutes: 15).Allowed);
    }

    [Fact]
    public void NightShift_AfterMidnight_StillOpen()
    {
        // Thursday 22:00 shift runs into Friday 06:00.
        Assert.True(ShiftUnlockGate.Evaluate(At(20, 3, 0), Sched(WeekdayNight)).Allowed);
        Assert.False(ShiftUnlockGate.Evaluate(At(20, 6, 0), Sched(WeekdayNight)).Allowed);
        Assert.True(ShiftUnlockGate.Evaluate(At(19, 22, 0), Sched(WeekdayNight)).Allowed);
    }

    [Fact]
    public void DayOff_And_Holiday_StayLocked()
    {
        // Saturday 2026-11-21 is not in the pattern; next opening is Monday 23rd.
        var sat = ShiftUnlockGate.Evaluate(At(21, 10, 0), Sched(WeekdayDay));
        Assert.False(sat.Allowed);
        Assert.Equal(At(23, 9, 0), sat.OpensAt);

        var holiday = ShiftUnlockGate.Evaluate(At(19, 10, 0), Sched(WeekdayDay, "2026-11-19"));
        Assert.False(holiday.Allowed);
        Assert.Equal(At(20, 9, 0), holiday.OpensAt);
    }

    [Fact]
    public void NoSchedule_DoesNotLockOut()
    {
        Assert.True(ShiftUnlockGate.Evaluate(At(19, 3, 0), null).Allowed);
        Assert.True(ShiftUnlockGate.Evaluate(At(19, 3, 0), Sched("{}")).Allowed);
    }
}
