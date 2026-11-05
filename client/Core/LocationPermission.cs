using System.Net.Http;

namespace client.Core;

/// <summary>
/// OS location permission probe for the permission wizard (Phase 3 GPS B.4).
/// </summary>
public static class LocationPermission
{
    public static bool IsPlatformSupported()
    {
        if (OperatingSystem.IsWindows()) return true;
        if (OperatingSystem.IsLinux())
            return IsCommandAvailable("busctl");
        return OperatingSystem.IsMacOS();
    }

    /// <summary>Returns true when a location fix can be obtained (OS or IP fallback).</summary>
    public static async Task<bool> CheckGrantedAsync(HttpClient http, CancellationToken ct)
    {
        var fix = await LocationProbe.TryGetFixAsync(http, ct);
        return fix.HasValue;
    }

    public static IReadOnlyDictionary<string, bool> GetPermissionStatus(HttpClient http)
    {
        // Synchronous snapshot for permission_status sync — best-effort without blocking long.
        try
        {
            var granted = CheckGrantedAsync(http, CancellationToken.None).GetAwaiter().GetResult();
            return new Dictionary<string, bool> { ["location"] = granted };
        }
        catch
        {
            return new Dictionary<string, bool> { ["location"] = false };
        }
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = command,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            return proc.WaitForExit(2000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
