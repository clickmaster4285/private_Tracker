using AlphaAITracker.Service;
using Xunit;

namespace client.Tests;

public class UpdatePathsStaleTests
{
    [Fact]
    public void TryClearStaleInProgress_clears_ttl_exceeded_flag()
    {
        UpdatePaths.EnsureRoot();
        var flag = UpdatePaths.InProgressFlag;
        Directory.CreateDirectory(Path.GetDirectoryName(flag)!);
        File.WriteAllText(flag, $"{DateTime.UtcNow.AddMinutes(-30):o}\n1.3.6\n");
        File.SetLastWriteTimeUtc(flag, DateTime.UtcNow.AddMinutes(-30));

        var cleared = UpdatePaths.TryClearStaleInProgress(TimeSpan.FromMinutes(20), out var reason);
        Assert.True(cleared);
        Assert.Contains("ttl_exceeded", reason);
        Assert.False(File.Exists(flag));
    }

    [Fact]
    public void TryClearStaleInProgress_clears_orphan_without_installer()
    {
        UpdatePaths.EnsureRoot();
        var flag = UpdatePaths.InProgressFlag;
        Directory.CreateDirectory(Path.GetDirectoryName(flag)!);
        foreach (var f in Directory.EnumerateFiles(UpdatePaths.RootDir, "*.exe"))
            try { File.Delete(f); } catch { /* ignore */ }
        foreach (var f in Directory.EnumerateFiles(UpdatePaths.RootDir, "*.part"))
            try { File.Delete(f); } catch { /* ignore */ }

        File.WriteAllText(flag, $"{DateTime.UtcNow.AddMinutes(-5):o}\n1.3.6\n");
        File.SetLastWriteTimeUtc(flag, DateTime.UtcNow.AddMinutes(-5));

        var cleared = UpdatePaths.TryClearStaleInProgress(TimeSpan.FromMinutes(20), out var reason);
        Assert.True(cleared);
        Assert.Equal("orphan_no_installer", reason);
        Assert.False(File.Exists(flag));
    }

    [Fact]
    public void IsUpdateInProgressBlocking_false_after_stale_clear()
    {
        UpdatePaths.EnsureRoot();
        var flag = UpdatePaths.InProgressFlag;
        Directory.CreateDirectory(Path.GetDirectoryName(flag)!);
        File.WriteAllText(flag, $"{DateTime.UtcNow.AddHours(-1):o}\n1.0.0\n");
        File.SetLastWriteTimeUtc(flag, DateTime.UtcNow.AddHours(-1));

        Assert.False(UpdatePaths.IsUpdateInProgressBlocking(TimeSpan.FromMinutes(20)));
    }
}
