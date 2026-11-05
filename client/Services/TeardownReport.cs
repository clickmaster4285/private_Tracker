namespace client.Services;

/// <summary>
/// Independently verified postconditions for protected uninstall.
/// Full local success requires gate + USB restored + hosts/FW cleared + DLP service gone +
/// auto-start cleared. Server notification is reported separately and never flips local
/// success alone.
/// </summary>
public sealed class TeardownReport
{
    public bool AccountRemoved { get; init; }
    public bool ServerNotified { get; init; }
    public bool GateWritten { get; init; }
    public bool PipeUnlockOk { get; init; }
    public bool PipeTimedOut { get; init; }
    public bool UsbRestored { get; init; }
    public bool HostsCleared { get; init; }
    public bool DlpServiceGone { get; init; }
    public bool AutoStartCleared { get; init; }
    public bool CleanupPendingElevation { get; init; }
    public string? FailedOperation { get; init; }
    public string? Detail { get; init; }
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    /// <summary>All required local postconditions verified.</summary>
    public bool FullLocalSuccess =>
        AccountRemoved
        && GateWritten
        && UsbRestored
        && HostsCleared
        && DlpServiceGone
        && AutoStartCleared;

    /// <summary>
    /// Exit code: 0 full local success, 2 partial (account wiped / cleanup incomplete),
    /// 1 hard failure before/during account wipe.
    /// </summary>
    public int ExitCode
    {
        get
        {
            if (FullLocalSuccess) return 0;
            if (AccountRemoved) return 2;
            return 1;
        }
    }

    public string SummaryLine
    {
        get
        {
            if (FullLocalSuccess)
            {
                return ServerNotified
                    ? "Full protected uninstall verified (server notified)."
                    : "Full protected uninstall verified (server self-remove not confirmed).";
            }

            if (AccountRemoved)
            {
                var pending = new List<string>();
                if (!DlpServiceGone) pending.Add("DLP service still registered");
                if (!UsbRestored) pending.Add("USBSTOR not restored to manual(3)");
                if (!HostsCleared) pending.Add("AlphaAI-DLP hosts/firewall still present");
                if (!AutoStartCleared) pending.Add("auto-start still present");
                if (CleanupPendingElevation) pending.Add("elevated cleanup required");
                return "Partial uninstall — account wiped; remaining: " +
                       string.Join("; ", pending) +
                       (string.IsNullOrEmpty(FailedOperation) ? "" : $" (failed at {FailedOperation})");
            }

            return $"Protected uninstall failed{(string.IsNullOrEmpty(FailedOperation) ? "" : " at " + FailedOperation)}" +
                   (string.IsNullOrEmpty(Detail) ? "" : ": " + Detail);
        }
    }
}
