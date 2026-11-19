using System.Net;
using AlphaAITracker.Service;
using Xunit;

namespace client.Tests;

public class LockdownWebPortTests
{
    [Theory]
    [InlineData("192.168.88.35", "192.168.88.35", null)]
    [InlineData("192.168.88.35:3000", "192.168.88.35", 3000)]
    [InlineData("http://192.168.88.35:8080/login", "192.168.88.35", 8080)]
    [InlineData("intranet.local:443", "intranet.local", null)]
    [InlineData("intranet.local:80", "intranet.local", null)]
    [InlineData("[fd00::1]:8443", "fd00::1", 8443)]
    [InlineData("fd00::1", "fd00::1", null)]
    [InlineData("contoso.com:99999", "contoso.com", null)]
    public void ParseAllowHost_KeepsExplicitPort(string raw, string host, int? port)
    {
        var (h, p) = LockdownNetworkEnforcer.ParseAllowHost(raw);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Fact]
    public void WebHosts_GetHttpAndHttps_PortHosts_GetOnlyTheirPort()
    {
        var rules = LockdownNetworkEnforcer.BuildDesiredAllowRules(
            [IPAddress.Parse("192.168.88.44")],
            8000,
            [IPAddress.Parse("192.168.88.35")],
            [],
            [IPAddress.Parse("192.168.88.1")],
            new Dictionary<int, IReadOnlyList<IPAddress>> { [3000] = [IPAddress.Parse("192.168.88.36")] });

        var web = rules.Keys.Single(k => k.StartsWith(LockdownNetworkEnforcer.AllowWebTcpBase, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("192.168.88.35", rules[web]);
        Assert.Equal(("tcp", "80,443", ""), LockdownNetworkEnforcer.SpecForAllow(web));

        var port = rules.Keys.Single(k => k.StartsWith(LockdownNetworkEnforcer.AllowWebPortBase, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("192.168.88.36", rules[port]);
        Assert.Equal(("tcp", "3000", ""), LockdownNetworkEnforcer.SpecForAllow(port));

        foreach (var name in rules.Keys)
        {
            var spec = LockdownNetworkEnforcer.SpecForAllow(name);
            Assert.False(FirewallActiveStoreProof.AllowCoversUdp443(spec.Proto, spec.Port));
        }
    }
}
