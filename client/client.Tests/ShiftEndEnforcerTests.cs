using client.Core;
using client.Services;
using Xunit;

namespace client.Tests;

public class ShiftEndEnforcerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 17, 0, 0, TimeSpan.Zero);
    private static ShiftUnlockGate.Decision D(bool allowed) =>
        new(allowed, allowed ? "in_shift" : "outside_shift", null, null, TimeZoneInfo.Utc);

    [Fact]
    public void ShiftEnds_SignsOutImmediately()
    {
        bool was = false; DateTimeOffset? since = null;
        Assert.Equal(ShiftEndEnforcer.Action.None, ShiftEndEnforcer.Decide(D(true), true, ref was, ref since, T0));
        Assert.True(was);
        Assert.Equal(ShiftEndEnforcer.Action.SignOutNow, ShiftEndEnforcer.Decide(D(false), true, ref was, ref since, T0.AddSeconds(15)));
    }

    [Fact]
    public void SignedInOutsideShift_SignedOutAfterGrace()
    {
        bool was = false; DateTimeOffset? since = null;
        Assert.Equal(ShiftEndEnforcer.Action.None, ShiftEndEnforcer.Decide(D(false), true, ref was, ref since, T0));
        Assert.Equal(ShiftEndEnforcer.Action.None, ShiftEndEnforcer.Decide(D(false), true, ref was, ref since, T0.AddSeconds(45)));
        Assert.Equal(ShiftEndEnforcer.Action.SignOutNow,
            ShiftEndEnforcer.Decide(D(false), true, ref was, ref since, T0 + ShiftEndEnforcer.OutsideShiftGrace));
    }

    [Fact]
    public void NoSchedule_NeverSignsOut()
    {
        bool was = false; DateTimeOffset? since = null;
        for (var i = 0; i < 10; i++)
            Assert.Equal(ShiftEndEnforcer.Action.None,
                ShiftEndEnforcer.Decide(D(false), false, ref was, ref since, T0.AddMinutes(i * 5)));
    }
}
