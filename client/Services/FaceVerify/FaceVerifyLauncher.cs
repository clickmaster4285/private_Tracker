using System.Diagnostics;
using client.Configuration;
using client.Core.Models;

namespace client.Services.FaceVerify;

/// <summary>
/// Soft-gate spawn of <c>--face-verify</c> after login / session restore (Windows only).
/// </summary>
public static class FaceVerifyLauncher
{
    public static void TryLaunchAfterLogin(AppConfig config, EmployeeInfo info)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!config.FaceVerifyEnabled) return;
        if (string.IsNullOrWhiteSpace(info.DeviceToken) && string.IsNullOrWhiteSpace(info.Token))
            return;
        if (string.IsNullOrWhiteSpace(config.ServerUrl)) return;
        if (FaceVerifySession.IsVerifyProcessRunning()) return;

        var token = !string.IsNullOrWhiteSpace(info.DeviceToken) ? info.DeviceToken! : info.Token!;
        FaceVerifySession.Write(new FaceVerifySession
        {
            EmployeeId = info.EmployeeId,
            EmployeeName = info.Name,
            DeviceToken = token,
            ApiBase = config.ServerUrl!.TrimEnd('/'),
            MachineId = config.ClientId,
            MatchThreshold = config.FaceMatchThreshold,
            // Optional override URL only — default download is DeviceAuth from the API
            // into %LocalAppData%\AlphaAITracker\face\models\.
            AdaFaceUrl = config.FaceAdaFaceUrl,
        });

        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            // Fire-and-forget sibling only — never await, never gate StartTracking/sync.
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--face-verify",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            try
            {
                if (proc != null)
                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch { /* ignore — priority is best-effort */ }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[client] Failed to launch --face-verify: {ex.Message}");
        }
    }
}
