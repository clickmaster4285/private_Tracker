using client.Services.FaceVerify;
using Xunit;

namespace client.Tests;

[Collection("FacePathsSerial")]
public class FaceFirstLocalIsolationTests
{
    [Fact]
    public async Task EnqueueVerifyEvent_ignores_first_mode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aat-face-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var prev = FacePaths.RootDirectoryOverride;
        FacePaths.RootDirectoryOverride = dir;
        try
        {
            using var store = new FaceBiometricStore();
            await store.InitializeAsync(CancellationToken.None);

            await store.EnqueueVerifyEventAsync(new LocalFaceVerifyEvent
            {
                Id = Guid.NewGuid().ToString(),
                EmployeeId = "EMP-TEST",
                Mode = "first",
                Result = "verified",
                CreatedAt = DateTime.UtcNow.ToString("O"),
            }, CancellationToken.None);
            var unsynced = await store.ListUnsyncedEventsAsync(10, CancellationToken.None);
            Assert.Empty(unsynced);

            await store.EnqueueVerifyEventAsync(new LocalFaceVerifyEvent
            {
                Id = Guid.NewGuid().ToString(),
                EmployeeId = "EMP-TEST",
                Mode = "simple",
                Result = "verified",
                CreatedAt = DateTime.UtcNow.ToString("O"),
            }, CancellationToken.None);
            unsynced = await store.ListUnsyncedEventsAsync(10, CancellationToken.None);
            Assert.Single(unsynced);
            Assert.Equal("simple", unsynced[0].Mode);
        }
        finally
        {
            FacePaths.RootDirectoryOverride = prev;
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
