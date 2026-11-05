using System.Runtime.Versioning;
using client.Services;
using Xunit;

namespace client.Tests;

[SupportedOSPlatform("windows")]
public class HostsTeardownStripTests
{
    [Fact]
    public void StripOwnedHostsBlock_removes_begin_end_and_orphan_markers()
    {
        var input =
            "127.0.0.1 localhost\r\n" +
            "# AlphaAI-DLP-BEGIN\r\n" +
            "0.0.0.0 youtube.com # AlphaAI-DLP\r\n" +
            ":: youtube.com # AlphaAI-DLP\r\n" +
            "# AlphaAI-DLP-END\r\n" +
            "0.0.0.0 leftover.example # AlphaAI-DLP\r\n" +
            "192.168.1.1 router\r\n";

        var stripped = WindowsTeardownCleanup.StripOwnedHostsBlock(input);

        Assert.Contains("127.0.0.1 localhost", stripped);
        Assert.Contains("192.168.1.1 router", stripped);
        Assert.DoesNotContain("AlphaAI-DLP", stripped);
        Assert.DoesNotContain("youtube.com", stripped);
        Assert.DoesNotContain("leftover.example", stripped);
    }

    [Fact]
    public void StripOwnedHostsBlock_noop_when_clean()
    {
        var input = "127.0.0.1 localhost\r\n";
        var stripped = WindowsTeardownCleanup.StripOwnedHostsBlock(input);
        Assert.Equal("127.0.0.1 localhost", stripped);
    }
}
