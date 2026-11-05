using client.Services;
using Xunit;

namespace client.Tests;

public class DlpPipeClientTimeoutTests
{
    [Fact]
    public async Task SendDetailedAsync_connect_timeout_on_missing_pipe()
    {
        if (!OperatingSystem.IsWindows())
            return; // named pipes under test are Windows-focused

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await DlpPipeClient.SendDetailedAsync(
            new { op = "Health" },
            pipeName: "AlphaAITracker.Dlp.DefinitelyMissing." + Guid.NewGuid().ToString("N"),
            connectTimeoutMs: 800,
            responseTimeoutMs: 1000);

        sw.Stop();
        Assert.False(result.Connected);
        Assert.True(result.TimedOut || result.Error is not null);
        // Must not hang for tens of seconds.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"pipe call took {sw.Elapsed}; expected hard connect deadline");
    }
}
