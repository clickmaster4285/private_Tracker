using AlphaAITracker.Service;
using Xunit;

namespace client.Tests;

public class AppLockerAdminRemotingTests
{
    [Fact]
    public void WinRmHost_IsAdministratorsOnly_NotSystemOrEveryone()
    {
        var admin = AppAllowEnforcer.AdminOnlyBootstrapPaths().ToList();
        Assert.Contains(admin, p => p.EndsWith(@"\wsmprovhost.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("S-1-5-32-544", AppAllowEnforcer.AdministratorsSid);

        // Interactive shells must stay LocalSystem-only — the remoting allow must not widen them.
        Assert.DoesNotContain(admin, p => p.Contains("powershell", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(admin, p => p.EndsWith(@"\cmd.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(AppAllowEnforcer.SystemOnlyBootstrapPaths(),
            p => p.Contains("wsmprovhost", StringComparison.OrdinalIgnoreCase));
    }
}
