using System.Reflection;
using System.Text.RegularExpressions;

namespace AlphaAITracker.Service;

/// <summary>
/// DLP service branding from embedded <c>APP_IDENTIFIERS</c> (same file as the
/// tracker). No hardcoded product/service names in workers or ACL helpers.
/// </summary>
internal static class ServiceIdentity
{
    private const string ResourceName = "APP_IDENTIFIERS";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Identifiers =
        new(LoadIdentifiers, isThreadSafe: true);

    public static string DisplayName => Get("DISPLAY_NAME", "Alpha AI Tracker");
    public static string PackageName => Get("PACKAGE_NAME", "alpha-ai-tracker");
    public static string AppMutex => Get("APP_MUTEX", "AlphaAITracker");
    public static string DataFolderName => AppMutex;
    public static string ExecutableName => Get("EXECUTABLE_NAME", "client");

    public static string ServiceName =>
        Get("WINDOWS_SERVICE_NAME", $"{AppMutex}.Service");

    /// <summary>Linux systemd system unit name for the privileged DLP daemon.</summary>
    public static string LinuxSystemUnitName => $"{PackageName}-dlp.service";

    public static string ServiceDisplayName =>
        Get("WINDOWS_SERVICE_DISPLAY_NAME", $"{DisplayName} DLP");

    public static string DlpPipeName =>
        Get("WINDOWS_DLP_PIPE_NAME", $"{AppMutex}.Dlp");

    public static string MaintTaskName =>
        Get("WINDOWS_SERVICE_MAINT_TASK", $"{AppMutex}SvcMaint");

    private static string Get(string key, string fallback) =>
        Identifiers.Value.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    private static IReadOnlyDictionary<string, string> LoadIdentifiers()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = typeof(ServiceIdentity).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return map;

            using var reader = new StreamReader(stream);
            var pattern = new Regex(
                "^\\s*(?<key>[A-Z0-9_]+)\\s*=\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|(?<bare>[^#\\s]*))",
                RegexOptions.Compiled);

            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
                var m = pattern.Match(line);
                if (!m.Success) continue;
                var value = m.Groups["dq"].Success ? m.Groups["dq"].Value
                          : m.Groups["sq"].Success ? m.Groups["sq"].Value
                          : m.Groups["bare"].Value;
                map[m.Groups["key"].Value] = value;
            }
        }
        catch
        {
            // Branding must never take the service down.
        }
        return map;
    }
}
