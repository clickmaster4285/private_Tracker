using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Reads the machine-wide DLP credential file written by the user-session tracker
/// on login (%ProgramData%\&lt;APP_MUTEX&gt;\dlp\device.auth). LocalSystem cannot see
/// the interactive user's LocalAppData SQLite, so this file is the production bridge.
/// </summary>
internal static class DeviceCredentialStore
{
    internal const string FileName = "device.auth";

    public static string DefaultDir => DlpDataPaths.DlpRoot;

    public static string DefaultPath => Path.Combine(DefaultDir, FileName);

    public static bool TryLoad(out string serverUrl, out string deviceToken)
    {
        serverUrl = "";
        deviceToken = "";

        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("serverUrl", out var su))
                    serverUrl = (su.GetString() ?? "").TrimEnd('/');
                if (root.TryGetProperty("deviceToken", out var dt))
                    deviceToken = (dt.GetString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(serverUrl) || !string.IsNullOrWhiteSpace(deviceToken))
                    return true;
            }
            catch
            {
                /* try next */
            }
        }

        return false;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return DefaultPath;
        if (!OperatingSystem.IsLinux()) yield break;

        // Root daemon may also find a user-session mirror under ~/.local/share.
        List<string> extras;
        try
        {
            extras = new List<string>();
            foreach (var home in Directory.EnumerateDirectories("/home"))
            {
                extras.Add(Path.Combine(home, ".local", "share", ServiceIdentity.PackageName, "dlp", FileName));
                // Legacy Windows-style folder name some installs used for LocalAppData.
                extras.Add(Path.Combine(home, ".local", "share", ServiceIdentity.DataFolderName, "dlp", FileName));
            }
        }
        catch
        {
            yield break;
        }

        foreach (var p in extras)
            yield return p;
    }
}
