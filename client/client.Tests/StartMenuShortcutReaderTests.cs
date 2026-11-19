using client.Core;
using Xunit;

namespace client.Tests;

public class StartMenuShortcutReaderTests
{
    [Fact]
    public void InProcessReader_ResolvesWindowsBuiltInShortcuts_WithoutPowerShell()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var output = InstalledAppDetector.ReadStartMenuShortcutsInProcess();
        Assert.NotNull(output);
        var lines = output!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Contains('\t', l));
        // Every Windows install ships a Command Prompt shortcut targeting cmd.exe.
        Assert.Contains(lines, l => l.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase));
    }
}
