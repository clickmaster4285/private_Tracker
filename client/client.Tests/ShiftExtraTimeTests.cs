using System.Text.Json;
using AlphaAITracker.Service;
using client.Core;
using Xunit;

namespace client.Tests;

/// <summary>"Give extra time": per-date minutes before the start / after the end.</summary>
public class ShiftExtraTimeTests
{
    // Day shift 09:00–17:00 Mon–Fri, UTC. 2026-11-20 is a Friday.
    private static readonly Dictionary<string, string> Week = new()
    {
        ["mon"] = "09:00-17:00", ["tue"] = "09:00-17:00", ["wed"] = "09:00-17:00",
        ["thu"] = "09:00-17:00", ["fri"] = "09:00-17:00",
    };

    private static DateTimeOffset At(int day, int h, int m = 0) => new(2026, 11, day, h, m, 0, TimeSpan.Zero);

    private static ShiftUnlockGate.Schedule Gate(int before, int after) => new(
        "UTC", Week, new HashSet<string>(),
        new Dictionary<string, ShiftUnlockGate.Extra> { ["2026-11-20"] = new(before, after) });

    [Fact]
    public void ExtraAfterEnd_KeepsThisEmployeeIn_OnlyOnThatDate()
    {
        Assert.True(ShiftUnlockGate.Evaluate(At(20, 17, 10), Gate(0, 15)).Allowed);   // 17:10 with +15
        Assert.False(ShiftUnlockGate.Evaluate(At(20, 17, 15), Gate(0, 15)).Allowed);  // ends 17:15
        // Thursday has no extra time → normal end.
        Assert.False(ShiftUnlockGate.Evaluate(At(19, 17, 10), Gate(0, 15)).Allowed);
    }

    [Fact]
    public void ExtraBeforeStart_OpensEarly_AndSaysSo()
    {
        var now = At(20, 8, 40);
        Assert.True(ShiftUnlockGate.Evaluate(At(20, 8, 50), Gate(10, 0)).Allowed);
        var d = ShiftUnlockGate.Evaluate(now, Gate(10, 0));
        Assert.False(d.Allowed);
        Assert.Contains("today at 08:50", ShiftUnlockGate.Describe(d, now));
    }

    [Fact]
    public void ServerExtensionsJson_IsParsed()
    {
        using var doc = JsonDocument.Parse("""[{"date":"2026-11-20","beforeMinutes":10,"afterMinutes":15},{"date":"2026-11-21","beforeMinutes":0,"afterMinutes":0}]""");
        var x = ShiftUnlockGate.ParseExtensions(doc.RootElement);
        Assert.Single(x); // zero-minute entries ignored
        Assert.Equal(15, x["2026-11-20"].AfterMinutes);
    }

    [Fact]
    public void WindowsHours_FollowTheExtraTime()
    {
        var shift = LogonHoursWorker.ParseShift("""
            {"timezone":"UTC","weeklyPattern":{"fri":"09:00-17:00"},
             "extensions":[{"date":"2026-11-20","beforeMinutes":0,"afterMinutes":30}]}
            """)!;
        var mask = LogonHoursPlan.Compute(At(20, 8, 0), shift);
        Assert.True(LogonHoursPlan.IsAllowed(mask, At(20, 17, 20))); // hour 17 opened by +30
        // Without the extension the 17:00 hour stays shut.
        var plain = LogonHoursWorker.ParseShift("""{"timezone":"UTC","weeklyPattern":{"fri":"09:00-17:00"}}""")!;
        Assert.False(LogonHoursPlan.IsAllowed(LogonHoursPlan.Compute(At(20, 8, 0), plain), At(20, 17, 20)));
    }
}
