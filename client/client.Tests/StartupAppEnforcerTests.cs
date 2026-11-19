using AlphaAITracker.Service;
using Xunit;

namespace client.Tests;

public class StartupAppEnforcerTests
{
    private static readonly ProductAvExclusions.Set NoProduct = new(
        new[] { @"C:\Program Files\Alpha AI Tracker" }, Array.Empty<string>(), Array.Empty<string>());

    private static List<AppProcessGuard.PathMatcher> Allow(params string[] paths) =>
        AppProcessGuard.BuildPublishedMatchers(paths.Select(p => new AllowAppEntry { Path = p }).ToList());

    [Fact]
    public void ParseCommand_Quoted_SplitsExeAndArgs()
    {
        var (exe, args) = StartupAppEnforcer.ParseCommand(@"""C:\Program Files\Foo\foo.exe"" --minimized -x");
        Assert.Equal(@"C:\Program Files\Foo\foo.exe", exe);
        Assert.Equal("--minimized -x", args);
    }

    [Fact]
    public void ParseCommand_UnquotedMissingFile_FirstToken()
    {
        var (exe, args) = StartupAppEnforcer.ParseCommand(@"C:\NoSuch\app.exe /background");
        Assert.Equal(@"C:\NoSuch\app.exe", exe);
        Assert.Equal("/background", args);
    }

    [Fact]
    public void ThirdPartyApp_NotAllowlisted_IsOff()
    {
        Assert.False(StartupAppEnforcer.IsAllowedTarget(
            @"C:\Program Files\Zoom\bin\Zoom.exe", "", NoProduct, Allow()));
    }

    [Fact]
    public void AllowlistedApp_StaysOn()
    {
        Assert.True(StartupAppEnforcer.IsAllowedTarget(
            @"C:\Program Files\Zoom\bin\Zoom.exe", "", NoProduct, Allow(@"C:\Program Files\Zoom\bin\*")));
    }

    [Fact]
    public void ProductBinary_StaysOn()
    {
        Assert.True(StartupAppEnforcer.IsAllowedTarget(
            @"C:\Program Files\Alpha AI Tracker\client.exe", "--background", NoProduct, Allow()));
    }

    [Fact]
    public void WindowsComponent_StaysOn_ButLauncherHostIsJudgedByItsTarget()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windir))
            return; // non-Windows test host
        var sys = Path.Combine(windir, "System32");
        Assert.True(StartupAppEnforcer.IsAllowedTarget(
            Path.Combine(sys, "SecurityHealthSystray.exe"), "", NoProduct, Allow()));
        Assert.False(StartupAppEnforcer.IsAllowedTarget(
            Path.Combine(sys, "rundll32.exe"), @"C:\Users\x\AppData\Local\evil.dll,Run", NoProduct, Allow()));
        Assert.True(StartupAppEnforcer.IsAllowedTarget(
            Path.Combine(sys, "rundll32.exe"), @"""C:\Program Files\Alpha AI Tracker\helper.dll"",Run", NoProduct, Allow()));
    }

    [Fact]
    public void UnresolvedTarget_IsOff()
    {
        Assert.False(StartupAppEnforcer.IsAllowedTarget("", "", NoProduct, Allow()));
    }

    [Fact]
    public void DisabledFlag_OddFirstByte()
    {
        Assert.True(StartupAppEnforcer.IsDisabled(StartupAppEnforcer.DisabledValue()));
        Assert.False(StartupAppEnforcer.IsDisabled(new byte[] { 0x02, 0, 0, 0 }));
        Assert.False(StartupAppEnforcer.IsDisabled(null));
    }
}
