namespace AlphaAITracker.Service;

/// <summary>
/// Single-flight, rate-limited wrapper around <see cref="DefenderGuard.EnsureSelfProtection"/>.
/// Shared by <see cref="AntivirusGuardWorker"/> (startup + self-block events) and the pipe
/// op <c>EnsureAvSelfProtection</c> (soft-lock ORT load failure), so Defender never blocks
/// or quarantines the product's own binaries for longer than one pass.
/// </summary>
public static class AvSelfProtection
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    private static DateTimeOffset _lastRun = DateTimeOffset.MinValue;
    private static Result _last = new(false, [], [], ["not_run"], DateTimeOffset.MinValue);

    public sealed record Result(
        bool Ok,
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Restored,
        IReadOnlyList<string> Errors,
        DateTimeOffset At);

    public static async Task<Result> RunAsync(bool force, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, [], [], ["unsupported_os"], DateTimeOffset.UtcNow);

        await Gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (!force && now - _lastRun < MinInterval)
                return _last;

            var (ok, added, restored, errors) = await Task.Run(DefenderGuard.EnsureSelfProtection, ct);
            _lastRun = DateTimeOffset.UtcNow;
            _last = new Result(ok, added, restored, errors, _lastRun);
            return _last;
        }
        finally
        {
            Gate.Release();
        }
    }
}
