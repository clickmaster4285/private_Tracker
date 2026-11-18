using AlphaAITracker.Service;
using Xunit;

namespace client.Tests;

public class PolicyBundleValidationTests
{
    private const string CompleteBundle = """
        {
          "policyVersion": 7,
          "lockdown": true,
          "platformAllow": { "hosts": ["api.example.local"] },
          "allow": { "hosts": ["intranet.example.local"], "apps": [] },
          "rules": [],
          "shadowDeny": { "domains": [] },
          "deviceControl": []
        }
        """;

    [Fact]
    public void ValidateForPersistence_AcceptsCompleteBundle()
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(CompleteBundle))).ToLowerInvariant();
        var bundle = PolicyBundleParser.ValidateForPersistence(CompleteBundle, 7, "sha256:" + hash);
        Assert.Equal(7, bundle.PolicyVersion);
        Assert.True(bundle.LockdownEnabled);
    }

    [Fact]
    public void ValidateForPersistence_RejectsEmpty()
    {
        Assert.Throws<InvalidDataException>(() =>
            PolicyBundleParser.ValidateForPersistence("{}", 1, null));
    }

    [Fact]
    public void ValidateForPersistence_RejectsHashMismatch()
    {
        Assert.Throws<InvalidDataException>(() =>
            PolicyBundleParser.ValidateForPersistence(
                CompleteBundle, 7,
                "sha256:0000000000000000000000000000000000000000000000000000000000000000"));
    }

    [Fact]
    public void ValidateForPersistence_RejectsVersionMismatch()
    {
        Assert.Throws<InvalidDataException>(() =>
            PolicyBundleParser.ValidateForPersistence(CompleteBundle, 99, null));
    }

    [Fact]
    public void ServerUrlNormalizer_PreservesHttps()
    {
        Assert.Equal("https://192.168.88.35:8000",
            ServerUrlNormalizer.Normalize("https://192.168.88.35:8000/"));
        Assert.Equal("http://192.168.88.35:8000",
            ServerUrlNormalizer.Normalize("http://192.168.88.35:8000/"));
        Assert.Equal("https://192.168.88.35",
            ServerUrlNormalizer.Normalize("192.168.88.35"));
        Assert.False(ServerUrlNormalizer.NeedsRewrite("https://example.com"));
        Assert.Equal("", ServerUrlNormalizer.Normalize("ftp://bad"));
    }

    [Fact]
    public void IsUnsafeUserWritableAllow_RejectsUserPaths()
    {
        Assert.True(AppAllowEnforcer.IsUnsafeUserWritableAllow(@"C:\Users\bob\Downloads\evil.exe"));
        Assert.True(AppAllowEnforcer.IsUnsafeUserWritableAllow(@"%LOCALAPPDATA%\app\run.exe"));
        Assert.False(AppAllowEnforcer.IsUnsafeUserWritableAllow(
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"));
    }
}
