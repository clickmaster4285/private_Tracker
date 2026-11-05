using System.Text;
using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Publishes DeviceAuth credentials for the privileged DLP service.
/// Windows: %ProgramData%\&lt;APP_MUTEX&gt;\dlp\device.auth (LocalSystem cannot read user SQLite).
/// Linux: /var/lib/&lt;package&gt;/dlp/device.auth (sticky 1777 from .deb — root daemon reads it).
/// </summary>
public static class DlpDeviceCredentialBridge
{
    private const string FileName = "device.auth";

    private static string DirPath
    {
        get
        {
            if (OperatingSystem.IsLinux())
                return Path.Combine("/var/lib", AppInfo.PackageName, "dlp");

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, AppInfo.DataFolderName, "dlp");
        }
    }

    private static string FilePath => Path.Combine(DirPath, FileName);

    /// <summary>User LocalAppData fallback mirror (Linux) when /var/lib is not writable yet.</summary>
    private static string? UserFallbackPath
    {
        get
        {
            if (!OperatingSystem.IsLinux()) return null;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) return null;
            return Path.Combine(local, AppInfo.PackageName, "dlp", FileName);
        }
    }

    public static void Publish(string? deviceToken, string? employeeId = null, string? serverUrl = null)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            Clear();
            return;
        }

        try
        {
            var url = (serverUrl ?? Environment.GetEnvironmentVariable("ALPHA_SERVER_URL") ?? "").TrimEnd('/');
            var payload = JsonSerializer.Serialize(new
            {
                serverUrl = url,
                deviceToken = deviceToken.Trim(),
                employeeId = employeeId ?? "",
                updatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });

            WriteAtomic(DirPath, FilePath, payload);

            var fallback = UserFallbackPath;
            if (fallback is not null)
            {
                var fbDir = Path.GetDirectoryName(fallback)!;
                WriteAtomic(fbDir, fallback, payload);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DlpDeviceCredentialBridge] Publish failed: {ex.Message}");
        }
    }

    public static void Clear()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DlpDeviceCredentialBridge] Clear failed: {ex.Message}");
        }

        try
        {
            var fallback = UserFallbackPath;
            if (fallback is not null && File.Exists(fallback))
                File.Delete(fallback);
        }
        catch { /* ignore */ }
    }

    private static void WriteAtomic(string dir, string path, string payload)
    {
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, payload, Encoding.UTF8);
        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
    }
}
