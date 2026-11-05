using client.Services;
using Xunit;

namespace client.Tests;

public class ProtectedUninstallNormalizeTests
{
    [Theory]
    [InlineData("'ABC-123'", "ABC-123")]
    [InlineData("\"ABC-123\"", "ABC-123")]
    [InlineData("  ABC-123  ", "ABC-123")]
    [InlineData("ABC-123", "ABC-123")]
    public void NormalizeCode_strips_cmd_style_quotes(string raw, string expected)
    {
        Assert.Equal(expected, ProtectedUninstallService.NormalizeCode(raw));
    }
}
