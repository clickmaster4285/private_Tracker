using AlphaAITracker.Service;
using Xunit;

namespace client.Tests;

public class AppAllowNormalizePathTests
{
    [Fact]
    public void NormalizeAppLockerPath_pf86_edge_does_not_emit_space_x86_macro()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrEmpty(pf86))
            return; // non-Windows CI

        var raw = Path.Combine(pf86, "Microsoft", "Edge", "Application") + @"\*";
        var norm = AppAllowEnforcer.NormalizeAppLockerPath(raw);
        Assert.StartsWith("%PROGRAMFILES%", norm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%PROGRAMFILES% (x86)", norm, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"\Microsoft\Edge\Application\*", norm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeAppLockerPath_rewrites_legacy_broken_macro()
    {
        var broken = @"%PROGRAMFILES% (x86)\Microsoft\Edge\Application\*";
        var norm = AppAllowEnforcer.NormalizeAppLockerPath(broken);
        Assert.Equal(@"%PROGRAMFILES%\Microsoft\Edge\Application\*", norm);
    }
}
