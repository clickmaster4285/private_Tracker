using AlphaAITracker.Service;
using client.Core;
using Xunit;

namespace client.Tests;

public class ShiftStoreTests
{
    private const string NightJson =
        """{"timezone":"Asia/Karachi","graceMinutes":5,"weeklyPattern":{"mon":"22:00-06:00"},"holidays":[{"date":"2026-11-23","status":"approved"},{"date":"2026-11-24","status":"pending"}]}""";

    [Fact]
    public void Store_SavesLoadsAndRemembersNoShift()
    {
        var old = ShiftStore.Directory;
        ShiftStore.Directory = Path.Combine(Path.GetTempPath(), "aat-shiftstore-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(ShiftStore.Load("EMP-1"));

            ShiftStore.Save("EMP-1", NightJson);
            var e = ShiftStore.Load("EMP-1");
            Assert.NotNull(e);
            Assert.False(e!.NoShift);
            Assert.Contains("22:00-06:00", e.ScheduleJson);

            ShiftStore.Save("EMP-1", null); // server: no shift assigned
            Assert.True(ShiftStore.Load("EMP-1")!.NoShift);
            Assert.Null(ShiftStore.Load("EMP-2"));
        }
        finally
        {
            try { Directory.Delete(ShiftStore.Directory, true); } catch { }
            ShiftStore.Directory = old;
        }
    }

    [Fact]
    public void TrackerReadsServiceAnswer_SameRulesAsService()
    {
        var body = "{\"ok\":true,\"found\":true,\"noShift\":false,\"fetchedAt\":\"2026-11-21T09:00:00+00:00\",\"schedule\":" + NightJson + "}";
        Assert.True(ShiftSource.TryParseServiceAnswer(body, out var r));
        Assert.True(r.FromService);
        Assert.Equal("22:00-06:00", r.Schedule!.WeeklyPattern["mon"]);
        Assert.Contains("2026-11-23", r.Schedule.HolidayDates);
        Assert.DoesNotContain("2026-11-24", r.Schedule.HolidayDates); // pending not counted
        Assert.NotNull(r.FetchedAt);

        Assert.True(ShiftSource.TryParseServiceAnswer("{\"ok\":true,\"found\":true,\"noShift\":true}", out var none));
        Assert.Null(none.Schedule);

        Assert.False(ShiftSource.TryParseServiceAnswer("{\"ok\":true,\"found\":false}", out _)); // → tracker fallback
    }
}
