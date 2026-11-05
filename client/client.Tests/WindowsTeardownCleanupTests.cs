using client.Core;
using client.Services;
using Xunit;

namespace client.Tests;

public class WindowsTeardownCleanupTests
{
    [Fact]
    public void QueryServiceState_absent_for_nonexistent_service()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var state = WindowsTeardownCleanup.QueryServiceState(
            "AlphaAITracker.Service.DoesNotExist." + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal("absent", state);
        Assert.False(WindowsTeardownCleanup.ServiceExists(
            "AlphaAITracker.Service.DoesNotExist." + Guid.NewGuid().ToString("N")[..8]));
    }

    [Fact]
    public void ReadUsbStorStart_returns_value_or_null()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var start = WindowsTeardownCleanup.ReadUsbStorStart();
        Assert.True(start is null or >= 0 and <= 4);
    }

    [Fact]
    public void AppInfo_service_name_matches_identifiers()
    {
        Assert.Equal("AlphaAITracker.Service", AppInfo.WindowsServiceName);
    }
}
