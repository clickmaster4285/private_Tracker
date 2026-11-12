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

    [Fact]
    public void BuildAppLockerClearPowerShell_emits_set_policy_and_srpv2_delete()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var ps = WindowsTeardownCleanup.BuildAppLockerClearPowerShell(@"C:\Temp\teardown");
        Assert.Contains("Set-AppLockerPolicy", ps, StringComparison.Ordinal);
        Assert.Contains("SrpV2", ps, StringComparison.Ordinal);
        Assert.Contains("NotConfigured", ps, StringComparison.Ordinal);
        Assert.Contains("AppIDSvc", ps, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyAppLockerPolicyXml_is_notconfigured()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Contains("NotConfigured", WindowsTeardownCleanup.EmptyAppLockerPolicyXml, StringComparison.Ordinal);
        Assert.Contains("Exe", WindowsTeardownCleanup.EmptyAppLockerPolicyXml, StringComparison.Ordinal);
    }
}
