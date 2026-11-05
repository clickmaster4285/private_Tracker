namespace client.Core;

/// <summary>
/// Metadata-driven software category resolution.
/// Replaces hardcoded browser/IDE/file-manager name lists with .desktop Categories
/// (Linux), macOS bundle identifiers, and Windows registry hints.
///
/// One software entry belongs to exactly one primary category.
/// </summary>
public static class SoftwareCategoryResolver
{
    // Canonical category strings (stored in installed_packages.category and used
    // by AppProcessClassifier for item_type derivation).
    public const string Application = "application";
    public const string Browser = "browser";
    public const string Ide = "ide";
    public const string FileManager = "file_manager";
    public const string Runtime = "runtime";
    public const string CliTool = "tool";
    public const string Service = "service";
    public const string Driver = "driver";
    public const string Library = "library";
    public const string Package = "package";
    public const string SystemComponent = "system_component";
    public const string Unknown = "unknown";
    public const string Ignored = "ignored";

    /// <summary>
    /// Resolve the primary software category from a Linux .desktop Categories string
    /// (semicolon-separated, e.g. "WebBrowser;Network;GTK;") or a macOS bundle id.
    /// Returns Unknown when no category can be inferred.
    /// </summary>
    public static string ResolveFromCategories(string? categories, bool isBrowser)
    {
        if (isBrowser) return Browser;
        if (string.IsNullOrWhiteSpace(categories)) return Unknown;

        // macOS bundle id reverse-DNS path (e.g. "com.microsoft.VSCode").
        // We can't fully classify from a bundle id, but browser is already handled above.
        if (categories.Contains('.')) return Application;

        var cats = categories.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = new HashSet<string>(cats, StringComparer.OrdinalIgnoreCase);

        if (set.Contains("WebBrowser")) return Browser;
        if (set.Contains("FileManager")) return FileManager;
        // IDEs advertise Development + IDE (or Development alone as a strong signal)
        if (set.Contains("IDE")) return Ide;
        if (set.Contains("Development")) return Ide;
        // Terminal emulators
        if (set.Contains("TerminalEmulator")) return Application;
        // Runtimes / languages
        if (set.Contains("Interpreter") || set.Contains("Development\\;IDE")) return Runtime;
        // A GUI application with a generic category (Network, Office, Game, etc.)
        return Application;
    }

    /// <summary>
    /// Resolve category for an installed package based on its source manager + category hint.
    /// Runtimes and build tools are recognized by their package-manager category; everything
    /// else from a package manager is a CLI tool/library.
    /// </summary>
    public static string ResolveForPackage(string packageName, string sourceManager, string existingCategory)
    {
        // Existing category (set by PackageDetector at scan time) is the strongest signal.
        if (string.Equals(existingCategory, "runtime", StringComparison.OrdinalIgnoreCase))
            return Runtime;
        if (string.Equals(existingCategory, "library", StringComparison.OrdinalIgnoreCase))
            return Library;
        if (string.Equals(existingCategory, "driver", StringComparison.OrdinalIgnoreCase))
            return Driver;
        return CliTool;
    }

    /// <summary>Whether a category represents a GUI application (vs a CLI/runtime/library).</summary>
    public static bool IsGuiCategory(string category) =>
        category is Browser or Ide or FileManager or Application;
}
