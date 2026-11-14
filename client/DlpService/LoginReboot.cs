using System.Diagnostics;

namespace AlphaAITracker.Service;

/// <summary>
/// One-shot Windows reboot after tracker login + DLP policy apply.
/// Only scheduled from the credentials-appeared path (service already had
/// device.auth at boot → no reboot loop after restart).
/// </summary>
internal static class LoginReboot
{
    /// <summary>
    /// Schedule a forced reboot. Returns false if the OS rejected the request
    /// (caller should keep the pending flag and retry).
    /// </summary>
    public static (bool Ok, string Detail) TrySchedule(int delaySeconds = 5)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "unsupported_os");

        var delay = Math.Clamp(delaySeconds, 5, 120);
        try
        {
            // LocalSystem already holds SeShutdownPrivilege; /f closes apps without prompts.
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "shutdown.exe"),
                Arguments =
                    $"/r /t {delay} /f /c \"Alpha AI Tracker: lockdown applied after login — restarting\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
                return (false, "shutdown_start_failed");
            // shutdown.exe exits immediately after queueing; do not WaitForExit forever.
            if (!p.WaitForExit(5000))
                return (true, $"queued delay={delay}s (shutdown still running)");
            if (p.ExitCode != 0)
                return (false, $"shutdown_exit={p.ExitCode}");
            return (true, $"queued delay={delay}s");
        }
        catch (Exception ex)
        {
            return (false, "exception:" + ex.GetType().Name);
        }
    }
}
