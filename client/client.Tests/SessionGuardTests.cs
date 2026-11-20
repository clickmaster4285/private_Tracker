using AlphaAITracker.Service;
using Xunit;

namespace client.Tests;

/// <summary>Single-session guard: who gets signed out, and when (shift-account PCs).</summary>
public class SessionGuardTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(120);
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 14, 0, 0, TimeSpan.Zero);

    private static SessionGuardEnforcer.SessionInfo S(int id, int state, string user) =>
        new(id, state, user, "PC1");

    private const int Active = SessionGuardEnforcer.WtsActive;
    private const int Disc = SessionGuardEnforcer.WtsDisconnected;

    [Fact]
    public void OtherUsersDisconnectedSession_IsSignedOut_OnlyAfterGrace()
    {
        var map = new Dictionary<int, DateTimeOffset>();
        var sessions = new[] { S(1, Disc, "Shift-Morning"), S(2, Active, "Shift-Evening") };

        // First sighting starts the timer, nothing signed out.
        Assert.Empty(SessionGuardEnforcer.DecideLogoffs(sessions, map, T0, Grace, []));
        // Inside the grace window: still nothing.
        Assert.Empty(SessionGuardEnforcer.DecideLogoffs(sessions, map, T0.AddSeconds(60), Grace, []));
        // After the grace window: the Morning session goes.
        var off = SessionGuardEnforcer.DecideLogoffs(sessions, map, T0.AddSeconds(121), Grace, []);
        Assert.Single(off);
        Assert.Equal("Shift-Morning", off[0].User);
    }

    [Fact]
    public void NoActiveSession_NobodyIsSignedOut()
    {
        // e.g. everyone at the lock screen / sign-in screen — never sign out work in progress.
        var map = new Dictionary<int, DateTimeOffset> { [1] = T0 };
        var sessions = new[] { S(1, Disc, "Shift-Morning") };
        Assert.Empty(SessionGuardEnforcer.DecideLogoffs(sessions, map, T0.AddHours(1), Grace, []));
    }

    [Fact]
    public void ActiveSession_IsNeverSignedOut()
    {
        var map = new Dictionary<int, DateTimeOffset> { [2] = T0.AddHours(-1) };
        var sessions = new[] { S(2, Active, "Shift-Evening") };
        Assert.Empty(SessionGuardEnforcer.DecideLogoffs(sessions, map, T0, Grace, []));
        Assert.Empty(map); // timer forgotten once the session is no longer disconnected
    }

    [Fact]
    public void SameAccountOlderSession_IsNotSignedOut()
    {
        var map = new Dictionary<int, DateTimeOffset> { [1] = T0.AddHours(-1) };
        var sessions = new[] { S(1, Disc, "Shift-Evening"), S(2, Active, "Shift-Evening") };
        Assert.Empty(SessionGuardEnforcer.DecideLogoffs(sessions, map, T0, Grace, []));
    }

    [Fact]
    public void ExemptUser_IsNotSignedOut()
    {
        var map = new Dictionary<int, DateTimeOffset> { [1] = T0.AddHours(-1) };
        var sessions = new[] { S(1, Disc, "itadmin"), S(2, Active, "Shift-Night") };
        Assert.Empty(SessionGuardEnforcer.DecideLogoffs(sessions, map, T0, Grace, ["PC1\\itadmin"]));
        Assert.Single(SessionGuardEnforcer.DecideLogoffs(sessions, map, T0, Grace, []));
    }

    [Fact]
    public void ReconnectedSession_ResetsItsTimer()
    {
        var map = new Dictionary<int, DateTimeOffset>();
        var disc = new[] { S(1, Disc, "Shift-Morning"), S(2, Active, "Shift-Evening") };
        SessionGuardEnforcer.DecideLogoffs(disc, map, T0, Grace, []);
        // Morning comes back (switches back to its session): timer cleared.
        SessionGuardEnforcer.DecideLogoffs([S(1, Active, "Shift-Morning"), S(2, Disc, "Shift-Evening")],
            map, T0.AddSeconds(30), Grace, []);
        Assert.False(map.ContainsKey(1));
        // Morning disconnects again later: a fresh full grace period applies.
        Assert.Empty(SessionGuardEnforcer.DecideLogoffs(disc, map, T0.AddSeconds(200), Grace, []));
    }
}

public class SessionGuardImmediateTests
{
    [Fact]
    public void ZeroGrace_SignsOutPreviousUserOnFirstSighting()
    {
        var map = new Dictionary<int, DateTimeOffset>();
        var now = new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);
        var sessions = new[]
        {
            new SessionGuardEnforcer.SessionInfo(1, SessionGuardEnforcer.WtsDisconnected, "Amir", "PC1"),
            new SessionGuardEnforcer.SessionInfo(2, SessionGuardEnforcer.WtsActive, "Sohaib", "PC1"),
        };
        var off = SessionGuardEnforcer.DecideLogoffs(sessions, map, now, TimeSpan.Zero, []);
        Assert.Single(off);
        Assert.Equal("Amir", off[0].User);
        // Exempt IT account is still never signed out.
        map.Clear();
        Assert.Empty(SessionGuardEnforcer.DecideLogoffs(sessions, map, now, TimeSpan.Zero, ["Amir"]));
    }
}
