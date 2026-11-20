using System.Diagnostics;

namespace AlphaAITracker.Service;

/// <summary>
/// Shift PCs: when another employee becomes active (Switch user / sign-in), sign the previous
/// user's left-behind session out right away, make sure their tracker is gone, and start the
/// new user's tracker immediately — instead of waiting for the 60 s enforce tick + grace.
/// Polls WTS sessions every 2 s (cheap). Only acts while the single-session guard is armed
/// (ALPHA_DLP_SINGLE_SESSION on, machine not paused). Never signs out the active session,
/// the same account, or ALPHA_DLP_SINGLE_SESSION_EXEMPT_USERS.
/// </summary>
internal sealed class SessionSwitchWorker : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TrackerExitWait = TimeSpan.FromSeconds(10);

    private readonly DlpConfig _cfg;
    private readonly ILogger<SessionSwitchWorker> _log;

    public SessionSwitchWorker(DlpConfig cfg, ILogger<SessionSwitchWorker> log)
    {
        _cfg = cfg;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_cfg.SingleSession && SessionGuardEnforcer.Armed)
                {
                    var grace = _cfg.SwitchImmediate
                        ? TimeSpan.Zero
                        : TimeSpan.FromSeconds(_cfg.SingleSessionGraceSec);
                    var signedOut = SessionGuardEnforcer.SignOutStale(grace, _cfg.SingleSessionExemptUsers);
                    if (signedOut.Count > 0)
                    {
                        _log.LogWarning("User switch: signed out previous session(s) {Who}",
                            string.Join(", ", signedOut.Select(s => s.Domain + "\\" + s.User + "#" + s.Id)));
                        await EnsureTrackersGoneAsync(signedOut.Select(s => s.Id).ToHashSet(), ct);
                        DlpSignals.RequestTrackerLaunch();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Session switch check failed");
            }

            try { await Task.Delay(Poll, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Sign-out lets the tracker shut down cleanly (unsent data stays in that user's profile).
    /// If it is still holding the machine-wide lock after a few seconds, end it so the new
    /// user's tracker can start.
    /// </summary>
    private async Task EnsureTrackersGoneAsync(HashSet<int> sessionIds, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TrackerExitWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var alive = TrackersIn(sessionIds);
            var any = alive.Count > 0;
            foreach (var p in alive) p.Dispose();
            if (!any) return;
            await Task.Delay(500, ct);
        }
        foreach (var p in TrackersIn(sessionIds))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                _log.LogWarning("User switch: ended leftover tracker pid={Pid} session={Sid}", p.Id, p.SessionId);
            }
            catch { /* already gone */ }
            finally { p.Dispose(); }
        }
    }

    private static List<Process> TrackersIn(HashSet<int> sessionIds)
    {
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName(ServiceIdentity.ExecutableName))
        {
            var keep = false;
            try { keep = sessionIds.Contains(p.SessionId); }
            catch { /* exited */ }
            if (keep) list.Add(p);
            else p.Dispose();
        }
        return list;
    }
}
