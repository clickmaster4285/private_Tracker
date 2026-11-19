using AlphaAITracker.Service;
using Xunit;

namespace client.Tests;

public class StartupAppEnforcerTests
{
    private static readonly ProductAvExclusions.Set NoProduct = new(
        new[] { @"C:\Program Files\Alpha AI Tracker" }, Array.Empty<string>(), Array.Empty<string>());

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
    public void ThirdPartyApp_IsOff()
    {
        Assert.False(StartupAppEnforcer.IsAllowedTarget(
            @"C:\Program Files\Zoom\bin\Zoom.exe", "", NoProduct));
    }

    [Fact]
    public void AllowlistedApp_IsStillOff_AllowedToRunIsNotAutoStart()
    {
        // Edge on /allowlists/apps may be opened, but its auto-launch entry stays disabled.
        Assert.False(StartupAppEnforcer.IsAllowedTarget(
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            "--no-startup-window --win-session-start", NoProduct));
    }

    [Fact]
    public void ProductBinary_StaysOn()
    {
        Assert.True(StartupAppEnforcer.IsAllowedTarget(
            @"C:\Program Files\Alpha AI Tracker\client.exe", "--background", NoProduct));
    }

    [Fact]
    public void OnlyDefenderStaysOn_AmongWindowsComponents()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windir))
            return; // non-Windows test host
        var sys = Path.Combine(windir, "System32");
        Assert.True(StartupAppEnforcer.IsAllowedTarget(
            Path.Combine(sys, "SecurityHealthSystray.exe"), "", NoProduct));
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(StartupAppEnforcer.IsAllowedTarget(
            Path.Combine(pf, "Windows Defender", "MSASCuiL.exe"), "", NoProduct));
        // Other Windows helpers are off too.
        Assert.False(StartupAppEnforcer.IsAllowedTarget(Path.Combine(sys, "ctfmon.exe"), "", NoProduct));
        Assert.False(StartupAppEnforcer.IsAllowedTarget(
            Path.Combine(windir, "SysWOW64", "OneDriveSetup.exe"), "/thfirstsetup", NoProduct));
        // A look-alike Defender name outside the OS folders is not Defender.
        Assert.False(StartupAppEnforcer.IsAllowedTarget(
            @"C:\Users\x\AppData\Local\SecurityHealthSystray.exe", "", NoProduct));
        // Launcher hosts: on only when they start the product.
        Assert.False(StartupAppEnforcer.IsAllowedTarget(
            Path.Combine(sys, "rundll32.exe"), @"C:\Users\x\AppData\Local\evil.dll,Run", NoProduct));
        Assert.True(StartupAppEnforcer.IsAllowedTarget(
            Path.Combine(sys, "rundll32.exe"), @"""C:\Program Files\Alpha AI Tracker\helper.dll"",Run", NoProduct));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void PackagedStartupTask_EnabledStates(int state, bool enabled) =>
        Assert.Equal(enabled, StartupAppEnforcer.IsAppTaskEnabled(state));

    [Fact]
    public void UnresolvedTarget_IsOff()
    {
        Assert.False(StartupAppEnforcer.IsAllowedTarget("", "", NoProduct));
    }

    [Fact]
    public void DisabledFlag_OddFirstByte()
    {
        Assert.True(StartupAppEnforcer.IsDisabled(StartupAppEnforcer.DisabledValue()));
        Assert.False(StartupAppEnforcer.IsDisabled(new byte[] { 0x02, 0, 0, 0 }));
        Assert.False(StartupAppEnforcer.IsDisabled(null));
    }
}

public class PackagedAppSweepTests
{
    [Fact]
    public void AllowedPackagedApp_SurvivesStoreVersionUpdate()
    {
        var published = @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2607.0.0_x64__8wekyb3d8bbwe\CalculatorApp.exe";
        var matchers = AppProcessGuard.BuildPublishedMatchers([new AllowAppEntry { Path = published }]);
        Assert.True(AppProcessGuard.IsPathAllowed(published, matchers));
        Assert.True(AppProcessGuard.IsPathAllowed(
            @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2700.3.0_x64__8wekyb3d8bbwe\CalculatorApp.exe", matchers));
        // Another package (same exe name, different family) is still not allowed.
        Assert.False(AppProcessGuard.IsPathAllowed(
            @"C:\Program Files\WindowsApps\Evil.Calculator_1.0.0.0_x64__abcdefghijklm\CalculatorApp.exe", matchers));
    }
}
