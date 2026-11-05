using client.Services;
using Xunit;

namespace client.Tests;

public class TeardownReportTests
{
    [Fact]
    public void FullLocalSuccess_requires_all_postconditions()
    {
        var full = new TeardownReport
        {
            AccountRemoved = true,
            GateWritten = true,
            UsbRestored = true,
            HostsCleared = true,
            DlpServiceGone = true,
            AutoStartCleared = true,
            ServerNotified = false,
        };
        Assert.True(full.FullLocalSuccess);
        Assert.Equal(0, full.ExitCode);
        Assert.Contains("server self-remove not confirmed", full.SummaryLine);
    }

    [Fact]
    public void Partial_when_account_wiped_but_service_remains()
    {
        var partial = new TeardownReport
        {
            AccountRemoved = true,
            GateWritten = true,
            UsbRestored = true,
            HostsCleared = true,
            DlpServiceGone = false,
            AutoStartCleared = true,
            CleanupPendingElevation = true,
            FailedOperation = "remove_dlp_service",
        };
        Assert.False(partial.FullLocalSuccess);
        Assert.Equal(2, partial.ExitCode);
        Assert.Contains("DLP service still registered", partial.SummaryLine);
        Assert.Contains("elevated cleanup required", partial.SummaryLine);
    }

    [Fact]
    public void Hard_failure_before_account_wipe()
    {
        var fail = new TeardownReport
        {
            AccountRemoved = false,
            FailedOperation = "approve",
            Detail = "empty removal code",
        };
        Assert.Equal(1, fail.ExitCode);
        Assert.Contains("failed", fail.SummaryLine);
    }
}
