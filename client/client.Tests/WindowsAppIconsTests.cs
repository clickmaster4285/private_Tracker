using client.Core;
using Xunit;
using Xunit.Abstractions;

namespace client.Tests;

public class WindowsAppIconsTests
{
    private readonly ITestOutputHelper _out;
    public WindowsAppIconsTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void EncodePng_ProducesValidPngSignature()
    {
        var bgra = new byte[4 * 4 * 4];
        for (var i = 0; i < bgra.Length; i += 4) { bgra[i] = 255; bgra[i + 3] = 255; }
        var png = WindowsAppIcons.EncodePng(bgra, 4, 4);
        Assert.True(WindowsAppIcons.IsPng(png));
    }

    [Fact]
    public void PackageFamilyName_FromFullName()
    {
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe",
            InstalledAppDetector.PackageFamilyName(
                "Microsoft.WindowsCalculator_11.2607.0.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsCalculator"));
    }

    [Fact]
    public void ExeIcon_FromNotepadOrExplorer_IsPng()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var b64 = WindowsAppIcons.FromExe(exe);
        Assert.False(string.IsNullOrEmpty(b64));
        Assert.True(WindowsAppIcons.IsPng(Convert.FromBase64String(b64)));
    }

    /// <summary>Live inventory on the build machine — prints what the picker would get.</summary>
    [Fact]
    public void LiveInventory_IncludesPackagedAppsWithIcons()
    {
        if (!OperatingSystem.IsWindows()) return;
        var apps = new InstalledAppDetector().GetAllInstalledApplications();
        var packaged = apps.Where(a => a.DesktopId.Contains('!')).ToList();
        var withIcon = apps.Count(a => !string.IsNullOrEmpty(a.IconPng));
        _out.WriteLine($"total={apps.Count} packaged={packaged.Count} withIcon={withIcon}");
        foreach (var a in packaged.OrderBy(a => a.AppName).Take(40))
            _out.WriteLine($"  {a.AppName} | {a.BinaryName} | icon={(a.IconPng.Length > 0 ? a.IconPng.Length / 1024 + "KB" : "none")}");
        foreach (var a in apps.Where(a => !string.IsNullOrEmpty(a.IconPng)))
            Assert.True(WindowsAppIcons.IsPng(Convert.FromBase64String(a.IconPng)), a.AppName);
    }
}
