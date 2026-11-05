using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Offline Roaming Start/Stop state — user-writable under LocalAppData.
/// Does not depend on AlphaAITracker.Service or the server. The Start/Stop modal
/// and the <c>--background</c> tracker both read/write this file.
/// <para>
/// Day timer contract (local calendar day) — working time only, never wall-clock:
/// <list type="bullet">
/// <item>Start at 08:30 → counter begins at 00:00:00 and counts only while running.</item>
/// <item>Stop at 10:30 → counter freezes at 02:00:00.</item>
/// <item>Same-day Start again → continues adding to today's total.</item>
/// <item>Local midnight → force Stop + counter 00:00:00 (must Start again to count today).
/// This avoids "09:40" looking like the clock when left running overnight.</item>
/// </list>
/// </para>
/// </summary>
public static class LocalRoamingControl
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly object Gate = new();

    public static string FilePath
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInfo.DataFolderName, "control");
            return Path.Combine(root, "local-roaming.json");
        }
    }

    public sealed class State
    {
        public string DesiredState { get; set; } = "stopped";
        public string Phase { get; set; } = "idle";
        public long Generation { get; set; }
        public string? SessionStartedAt { get; set; }
        public long SessionAccumulatedActiveSec { get; set; }
        public string? DayKey { get; set; }
        public string? DayFirstStartedAt { get; set; }
    }

    public static State ReadOrDefault()
    {
        lock (Gate)
        {
            var s = TryReadUnlocked() ?? new State();
            var now = DateTimeOffset.UtcNow;
            // Midnight rollover even while still "running" — modal must not show yesterday.
            if (EnsureDayBucket(s, now))
                PersistUnlocked(s);
            return Clone(s);
        }
    }

    public static State WriteStart()
    {
        lock (Gate)
        {
            var s = TryReadUnlocked() ?? new State();
            var now = DateTimeOffset.UtcNow;
            EnsureDayBucket(s, now);
            if (string.IsNullOrWhiteSpace(s.DayFirstStartedAt))
                s.DayFirstStartedAt = now.ToString("o");
            s.SessionStartedAt = now.ToString("o");
            s.DesiredState = "running";
            s.Phase = "collecting";
            s.Generation++;
            PersistUnlocked(s);
            return Clone(s);
        }
    }

    public static State WriteStop()
    {
        lock (Gate)
        {
            var s = TryReadUnlocked() ?? new State();
            var now = DateTimeOffset.UtcNow;
            EnsureDayBucket(s, now);
            AccumulateSegment(s, now);
            s.DesiredState = "stopped";
            s.Phase = "idle";
            s.Generation++;
            PersistUnlocked(s);
            return Clone(s);
        }
    }

    public static void WritePhase(string phase)
    {
        lock (Gate)
        {
            var s = TryReadUnlocked() ?? new State();
            s.Phase = phase;
            PersistUnlocked(s);
        }
    }

    /// <summary>
    /// Active seconds for the current local calendar day only.
    /// Open segments that started before today are clipped to local midnight.
    /// </summary>
    public static long ElapsedActiveSec(State s, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var dayStart = StartOfLocalDay(now);
        var total = Math.Max(0, s.SessionAccumulatedActiveSec);
        if (string.Equals(s.DesiredState, "running", StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.TryParse(s.SessionStartedAt, out var started))
        {
            if (started < dayStart)
                started = dayStart;
            total += (long)Math.Max(0, (now - started).TotalSeconds);
        }
        return total;
    }

    /// <returns>true when the day bucket rolled (caller should persist).</returns>
    private static bool EnsureDayBucket(State s, DateTimeOffset nowUtc)
    {
        var dayKey = nowUtc.ToLocalTime().ToString("yyyy-MM-dd");
        if (string.Equals(s.DayKey, dayKey, StringComparison.Ordinal))
            return false;

        // New calendar day: discard yesterday's working time. Always end the day
        // stopped — continuing "running" from local midnight made the modal show
        // ~HH:MM equal to wall-clock (e.g. 09:40) which is NOT working time.
        s.DayKey = dayKey;
        s.SessionAccumulatedActiveSec = 0;
        s.DayFirstStartedAt = null;
        s.SessionStartedAt = null;
        s.DesiredState = "stopped";
        s.Phase = "idle";
        s.Generation++;
        return true;
    }

    private static DateTimeOffset StartOfLocalDay(DateTimeOffset nowUtc)
    {
        var local = nowUtc.ToLocalTime();
        var midnightLocal = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
        return midnightLocal.ToUniversalTime();
    }

    private static void AccumulateSegment(State s, DateTimeOffset nowUtc)
    {
        if (!DateTimeOffset.TryParse(s.SessionStartedAt, out var started))
        {
            s.SessionStartedAt = null;
            return;
        }
        var dayStart = StartOfLocalDay(nowUtc);
        if (started < dayStart)
            started = dayStart;
        s.SessionAccumulatedActiveSec += (long)Math.Max(0, (nowUtc - started).TotalSeconds);
        s.SessionStartedAt = null;
    }

    private static State? TryReadUnlocked()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static void PersistUnlocked(State s)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $"local-roaming.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonOpts));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static State Clone(State s) => new()
    {
        DesiredState = s.DesiredState,
        Phase = s.Phase,
        Generation = s.Generation,
        SessionStartedAt = s.SessionStartedAt,
        SessionAccumulatedActiveSec = s.SessionAccumulatedActiveSec,
        DayKey = s.DayKey,
        DayFirstStartedAt = s.DayFirstStartedAt,
    };
}
