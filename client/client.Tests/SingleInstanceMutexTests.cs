using client.Core;
using Xunit;

namespace client.Tests;

public class SingleInstanceMutexTests
{
    [Fact]
    public void PerSessionNames_AreSessionScopedOnWindows_TrackerStaysMachineWide()
    {
        Assert.StartsWith(@"Global\", SingleInstanceService.MutexName);
        var face = client.Services.FaceVerify.FacePaths.MutexName;
        var warn = client.Services.TimeDateWarning.TimeDateWarningState.MutexName;
        var prefix = OperatingSystem.IsWindows() ? @"Local\" : @"Global\";
        Assert.StartsWith(prefix, face);
        Assert.StartsWith(prefix, warn);
        Assert.EndsWith("-face-verify", face);
    }

    [Fact]
    public void TryAcquire_SecondOpenInSameUser_IsHeldHere()
    {
        var name = @"Local\aat-test-" + Guid.NewGuid().ToString("N");
        Assert.Equal(SingleInstanceService.Acquire.Acquired, SingleInstanceService.TryAcquire(name, out var first));
        try
        {
            var t = new Thread(() =>
            {
                var r = SingleInstanceService.TryAcquire(name, out var second);
                second?.Dispose();
                Assert.Equal(SingleInstanceService.Acquire.HeldHere, r);
            });
            t.Start();
            t.Join();
        }
        finally
        {
            first!.ReleaseMutex();
            first.Dispose();
        }
    }
}
