using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Single-flight, rate-limited wrapper around <see cref="DefenderGuard.EnsureSelfProtection"/>.
/// Shared by <see cref="AntivirusGuardWorker"/> (startup + self-block events) and the pipe
/// op <c>EnsureAvSelfProtection</c> (soft-lock ORT load failure).
/// <para>
/// Automatic triggers (startup / Defender events) are held to a persisted cooldown: 60 min
/// after a run, doubling after each consecutive failure (cap 24 h). The state is stored on
/// disk so a service restart cannot restart a detection → heal → detection cycle.
/// The pipe trigger is a user-facing repair and keeps the original 30 s floor.
/// </para>
/// </summary>
public static class AvSelfProtection
{
    public enum Trigger { Startup, Event, Pipe }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static readonly TimeSpan PipeMinInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan BaseCooldown = TimeSpan.FromMinutes(60);
    internal static readonly TimeSpan MaxCooldown = TimeSpan.FromHours(24);

    private static DateTimeOffset _lastRun = DateTimeOffset.MinValue;
    private static Result _last = new(false, [], [], ["not_run"], DateTimeOffset.MinValue);
    private static State? _state;

    public sealed record Result(
        bool Ok,
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Restored,
        IReadOnlyList<string> Errors,
        DateTimeOffset At,
        bool Skipped = false,
        string SkipReason = "");

    /// <summary>Persisted retry / cooldown state (also surfaced in antivirus health).</summary>
    public sealed record State(
        DateTimeOffset LastRunUtc,
        DateTimeOffset NextAllowedUtc,
        int ConsecutiveFailures,
        bool LastOk,
        string LastErrors);

    /// <summary>Back-compat overload: force = pipe-style repair, otherwise an automatic trigger.</summary>
    public static Task<Result> RunAsync(bool force, CancellationToken ct) =>
        RunAsync(force ? Trigger.Pipe : Trigger.Event, ct);

    public static async Task<Result> RunAsync(Trigger trigger, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, [], [], ["unsupported_os"], DateTimeOffset.UtcNow);

        await Gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var state = LoadState();

            var skip = SkipReason(trigger, now, _lastRun, state);
            if (skip is not null)
                return _last with { Skipped = true, SkipReason = skip };

            var (ok, added, restored, errors) = await Task.Run(DefenderGuard.EnsureSelfProtection, ct);
            _lastRun = DateTimeOffset.UtcNow;
            _last = new Result(ok, added, restored, errors, _lastRun);
            SaveState(NextState(state, ok, errors, _lastRun));
            return _last;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Current cooldown state (null when self-protection never ran).</summary>
    public static State? CurrentState()
    {
        Gate.Wait();
        try { return LoadState(); }
        finally { Gate.Release(); }
    }

    // ── Pure policy (unit tested) ────────────────────────────────────────────

    /// <summary>Null = run now; otherwise why the run is skipped.</summary>
    internal static string? SkipReason(Trigger trigger, DateTimeOffset now, DateTimeOffset lastRunThisProcess, State? state)
    {
        if (trigger == Trigger.Pipe)
            return now - lastRunThisProcess < PipeMinInterval ? "min_interval" : null;
        if (state is not null && now < state.NextAllowedUtc)
            return "cooldown";
        return null;
    }

    /// <summary>60 min after a successful run; 60 × 2^failures after a failed one (cap 24 h).</summary>
    internal static TimeSpan CooldownFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return BaseCooldown;
        var factor = Math.Pow(2, Math.Min(consecutiveFailures, 10));
        var ticks = Math.Min(BaseCooldown.Ticks * factor, MaxCooldown.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    internal static State NextState(State? prev, bool ok, IReadOnlyList<string> errors, DateTimeOffset at)
    {
        var failures = ok ? 0 : (prev?.ConsecutiveFailures ?? 0) + 1;
        return new State(
            LastRunUtc: at,
            NextAllowedUtc: at + CooldownFor(failures),
            ConsecutiveFailures: failures,
            LastOk: ok,
            LastErrors: string.Join(" | ", errors.Take(5)));
    }

    // ── Persistence ─────────────────────────────────────────────────────────

    private static string StatePath => Path.Combine(DlpDataPaths.DlpRoot, "av_self_protection.json");

    private static State? LoadState()
    {
        if (_state is not null) return _state;
        try
        {
            if (File.Exists(StatePath))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath));
        }
        catch { _state = null; /* corrupt → treat as never run */ }
        return _state;
    }

    private static void SaveState(State s)
    {
        _state = s;
        try
        {
            Directory.CreateDirectory(DlpDataPaths.DlpRoot);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch { /* in-memory state still applies for this process */ }
    }
}
