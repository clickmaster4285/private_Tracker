namespace AlphaAITracker.Service;

/// <summary>
/// Coalescing wake-up signals between workers.
/// <list type="bullet">
/// <item><see cref="PullNow"/> — "the server published a new policy": PolicyPullWorker
/// pulls immediately instead of waiting out ALPHA_DLP_POLICY_POLL_SEC. Raised by the
/// pipe op <c>RequestPolicyPull</c> (the tracker forwards the server's
/// <c>policy_changed</c> WebSocket push).</item>
/// <item><see cref="EnforceNow"/> — "a new bundle was saved": EnforceWorker applies it
/// immediately instead of waiting out <c>ALPHA_DLP_ENFORCE_TICK_SEC</c>.</item>
/// </list>
/// Each signal holds at most one pending wake, so bursts collapse into one pass.
/// </summary>
internal static class DlpSignals
{
    private static readonly SemaphoreSlim Pull = new(0, 1);
    private static readonly SemaphoreSlim Enforce = new(0, 1);

    public static void RequestPull() => Release(Pull);
    public static void RequestEnforce() => Release(Enforce);

    /// <summary>Wait up to <paramref name="max"/> or until a pull is requested.</summary>
    public static Task<bool> WaitPullAsync(TimeSpan max, CancellationToken ct) => Pull.WaitAsync(max, ct);

    /// <summary>Wait up to <paramref name="max"/> or until an enforce pass is requested.</summary>
    public static Task<bool> WaitEnforceAsync(TimeSpan max, CancellationToken ct) => Enforce.WaitAsync(max, ct);

    private static void Release(SemaphoreSlim s)
    {
        if (s.CurrentCount > 0) return; // a wake is already pending
        try { s.Release(); }
        catch (SemaphoreFullException) { /* raced with another caller — one wake is enough */ }
    }
}
