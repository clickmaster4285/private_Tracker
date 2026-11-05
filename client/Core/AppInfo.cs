using System.Reflection;
using System.Text.RegularExpressions;

namespace client.Core;

/// <summary>
/// Single runtime accessor for app branding and version.
///
/// Branding comes from <c>client/APP_IDENTIFIERS</c>, which is embedded into the
/// assembly by <c>client.csproj</c>. Version comes from <c>client/VERSION</c> via
/// the assembly's informational version. Edit either file, rebuild, and every
/// surface that reads <see cref="AppInfo"/> updates — there are no duplicated
/// literals to chase.
///
/// Mirrors the existing metadata read-back pattern in Configuration/AppConfig.cs.
/// </summary>
public static class AppInfo
{
    private const string ResourceName = "client.APP_IDENTIFIERS";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Identifiers =
        new(LoadIdentifiers, isThreadSafe: true);

    /// <summary>Human-readable product name — APP_IDENTIFIERS DISPLAY_NAME.</summary>
    public static string DisplayName => Get("DISPLAY_NAME", "Alpha AI Tracker");

    /// <summary>Publisher/author — APP_IDENTIFIERS PUBLISHER.</summary>
    public static string Publisher => Get("PUBLISHER", "Alpha AI");

    /// <summary>Product/support URL — APP_IDENTIFIERS APP_URL.</summary>
    public static string AppUrl => Get("APP_URL", string.Empty);

    /// <summary>Linux package name — APP_IDENTIFIERS PACKAGE_NAME.</summary>
    public static string PackageName => Get("PACKAGE_NAME", "alpha-ai-tracker");

    /// <summary>macOS bundle identifier — APP_IDENTIFIERS BUNDLE_ID.</summary>
    public static string BundleId => Get("BUNDLE_ID", "com.alphaai1.tracker");

    /// <summary>Executable name without extension — APP_IDENTIFIERS EXECUTABLE_NAME.</summary>
    public static string ExecutableName => Get("EXECUTABLE_NAME", "client");

    /// <summary>Single-instance mutex name — APP_IDENTIFIERS APP_MUTEX.</summary>
    public static string AppMutex => Get("APP_MUTEX", "AlphaAITracker");

    /// <summary>
    /// ProgramData / LocalAppData product folder stem — same as <see cref="AppMutex"/>.
    /// e.g. %ProgramData%\&lt;AppMutex&gt;\dlp
    /// </summary>
    public static string DataFolderName => AppMutex;

    /// <summary>Windows DLP SCM service name — APP_IDENTIFIERS WINDOWS_SERVICE_NAME.</summary>
    public static string WindowsServiceName =>
        Get("WINDOWS_SERVICE_NAME", $"{AppMutex}.Service");

    /// <summary>Services.msc display name — APP_IDENTIFIERS WINDOWS_SERVICE_DISPLAY_NAME.</summary>
    public static string WindowsServiceDisplayName =>
        Get("WINDOWS_SERVICE_DISPLAY_NAME", $"{DisplayName} DLP");

    /// <summary>DLP named pipe — APP_IDENTIFIERS WINDOWS_DLP_PIPE_NAME.</summary>
    public static string WindowsDlpPipeName =>
        Get("WINDOWS_DLP_PIPE_NAME", $"{AppMutex}.Dlp");

    /// <summary>Installer schtasks maintenance task — APP_IDENTIFIERS WINDOWS_SERVICE_MAINT_TASK.</summary>
    public static string WindowsServiceMaintTask =>
        Get("WINDOWS_SERVICE_MAINT_TASK", $"{AppMutex}SvcMaint");

    /// <summary>Linux StartupWMClass — APP_IDENTIFIERS WM_CLASS.</summary>
    public static string WmClass => Get("WM_CLASS", "AlphaAITracker");

    /// <summary>Semantic version from client/VERSION, e.g. "0.2.0".</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>Formatted for footers, e.g. "Version 0.2.0".</summary>
    public static string VersionDisplay => $"Version {Version}";

    /// <summary>
    /// Short uppercase product tagline shown under the wordmark — APP_IDENTIFIERS
    /// TAGLINE. Optional: the key is absent from the shipped file, so re-branders
    /// can add it without the build scripts needing to know about it.
    /// </summary>
    public static string Tagline => Get("TAGLINE", "ENTERPRISE SECURITY SUITE");

    /// <summary>Copyright line, e.g. "© 2026 Alpha AI".</summary>
    public static string Copyright => $"© {DateTime.Now.Year} {Publisher}";

    /// <summary>"Alpha AI Tracker 0.2.0" — window titles, tray tooltip, log banners.</summary>
    public static string TitleWithVersion => $"{DisplayName} {Version}";

    /// <summary>Uppercase initials of the display name, for the logo tile fallback.</summary>
    public static string Initials
    {
        get
        {
            var parts = DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
            return string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant();
        }
    }

    private static string Get(string key, string fallback) =>
        Identifiers.Value.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    private static string ResolveVersion()
    {
        // InformationalVersion is set from client/VERSION by client.csproj. Source-link
        // builds append "+<commit sha>", which is noise for a UI footer.
        var informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var clean = plus >= 0 ? informational[..plus] : informational;
            if (!string.IsNullOrWhiteSpace(clean)) return clean.Trim();
        }

        var asmVersion = typeof(AppInfo).Assembly.GetName().Version;
        return asmVersion is null ? "0.0.0" : $"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}";
    }

    /// <summary>
    /// Parses the embedded APP_IDENTIFIERS shell fragment. It is a flat list of
    /// KEY="value" assignments with '#' comments — deliberately parsed with a strict
    /// regex rather than executed, so the file stays safe to read as data.
    /// </summary>
    private static IReadOnlyDictionary<string, string> LoadIdentifiers()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var stream = typeof(AppInfo).Assembly.GetManifestResourceStream(ResourceName);
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
            // Branding must never take the app down — callers fall back to defaults.
        }

        return map;
    }
}
