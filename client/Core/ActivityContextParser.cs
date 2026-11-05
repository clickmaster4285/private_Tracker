using System.Text.RegularExpressions;

namespace client.Core;

public sealed class ParsedActivityContext
{
    public string RootTitle { get; set; } = string.Empty;
    public string RootIdentifier { get; set; } = string.Empty;
    public List<ContextChildItem> Children { get; set; } = new();
}

public sealed class ContextChildItem
{
    public string ItemType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
}

/// <summary>
/// Extracts URLs, file paths, and page titles from window titles and process metadata.
/// </summary>
public static partial class ActivityContextParser
{
    // BROWSER SUFFIXES REMOVED (2026-07-30): hardcoded list was brittle
    // (locale-dependent, zero-width-space bug, requires recompile per browser).
    // Replaced with dynamic suffix stripping using the app's actual display
    // name from installed_applications (passed via appDisplayName parameter).

    private static readonly string[] FileManagerSuffixes =
    {
        " — Files", " - Files", " — Dolphin", " - Dolphin", " — Thunar", " - Thunar",
        " — Nemo", " - Nemo", " — File Explorer", " - File Explorer",
    };

    /// <summary>
    /// Parse a window title + process metadata into a structured activity context.
    /// </summary>
    /// <param name="processName">The base process/binary name (e.g. "chrome", "code")</param>
    /// <param name="windowTitle">The window title from the OS (may be null/empty on Wayland)</param>
    /// <param name="rootItemType">The resolved item type ("browser_tab", "folder", "tab", etc.)</param>
    /// <param name="chromeProfile">Browser profile identifier from ParentProcessResolver</param>
    /// <param name="appDisplayName">
    ///   The app's real display name from installed_applications (e.g. "Google Chrome",
    ///   "Mozilla Firefox"). Used to dynamically strip browser suffixes from window titles.
    ///   When null, the raw title is kept as-is — no generic regex fallback (see safety
    ///   constraint in Fix 4 design).
    /// </param>
    public static ParsedActivityContext Parse(
        string processName,
        string? windowTitle,
        string rootItemType,
        string? chromeProfile = null,
        string? appDisplayName = null)
    {
        var title = windowTitle?.Trim() ?? string.Empty;
        var context = new ParsedActivityContext
        {
            RootTitle = string.IsNullOrEmpty(title) ? processName : title,
            RootIdentifier = processName,
        };

        if (rootItemType == "browser_tab")
            return ParseBrowserContext(context, title, chromeProfile, appDisplayName);

        if (rootItemType == "folder")
            return ParseFileManagerContext(context, title);

        if (!string.IsNullOrEmpty(title))
            context.RootIdentifier = title;

        return context;
    }

    private static ParsedActivityContext ParseBrowserContext(
        ParsedActivityContext context, string title, string? chromeProfile, string? appDisplayName)
    {
        // Dynamically strip suffix from the app's real display name (e.g., " - Google Chrome",
        // " — Mozilla Firefox"). This is future-proof: new browsers detected by InstalledAppDetector
        // automatically get their suffix stripped without any code changes.
        // If appDisplayName is null (shouldn't happen for confirmed browsers, but be safe),
        // pass the title through UNMODIFIED — no generic regex fallback.
        var pageTitle = string.IsNullOrEmpty(appDisplayName)
            ? title
            : StripSuffix(title, new[] { $" - {appDisplayName}", $" — {appDisplayName}" });
        var tabId = string.IsNullOrEmpty(chromeProfile) ? "default" : chromeProfile;

        // No window title available (Wayland browsers don't expose titles via xprop).
        // The browser extension handles navigation events via native messaging.
        // DO NOT create fake browser_navigation items — they pollute the data.
        // Just set the root tab context so the session is tracked.
        if (string.IsNullOrWhiteSpace(title))
        {
            var appName = context.RootIdentifier;
            context.RootTitle = appName;
            context.RootIdentifier = $"browser:{appName}:{tabId}";
            // No children — only the extension should create browser_navigation items
            return context;
        }

        context.RootTitle = string.IsNullOrEmpty(pageTitle) ? title : pageTitle;
        context.RootIdentifier = tabId;

        var url = ExtractUrl(title);
        if (url != null)
        {
            context.Children.Add(new ContextChildItem
            {
                ItemType = "browser_navigation",
                Title = string.IsNullOrEmpty(pageTitle) ? url : pageTitle,
                Identifier = NormalizeUrl(url),
            });
        }
        else if (!string.IsNullOrEmpty(pageTitle))
        {
            // Store the page title even without a URL — useful for browser UX monitoring
            context.Children.Add(new ContextChildItem
            {
                ItemType = "browser_navigation",
                Title = pageTitle,
                Identifier = string.IsNullOrWhiteSpace(title) || pageTitle == title
                    ? $"title:{pageTitle.ToLowerInvariant()}"
                    : $"title:{pageTitle.ToLowerInvariant()}",
            });
        }

        return context;
    }

    private static ParsedActivityContext ParseFileManagerContext(ParsedActivityContext context, string title)
    {
        // 🟡 Issue 5: Handle empty window title for file managers on Wayland
        if (string.IsNullOrWhiteSpace(title))
        {
            // Use process name as folder name fallback
            context.RootTitle = context.RootIdentifier;
            context.RootIdentifier = context.RootIdentifier;
            context.Children.Add(new ContextChildItem
            {
                ItemType = "folder",
                Title = context.RootTitle,
                Identifier = context.RootIdentifier,
            });
            return context;
        }

        var path = ExtractPath(title);
        if (path != null)
        {
            context.RootTitle = System.IO.Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } name
                ? name
                : path;
            context.RootIdentifier = path;

            if (System.IO.File.Exists(path))
            {
                context.Children.Add(new ContextChildItem
                {
                    ItemType = "file",
                    Title = System.IO.Path.GetFileName(path),
                    Identifier = path,
                });
            }
            else if (System.IO.Directory.Exists(path))
            {
                context.Children.Add(new ContextChildItem
                {
                    ItemType = "folder",
                    Title = context.RootTitle,
                    Identifier = path,
                });
            }
        }
        else
        {
            // Strip file-manager suffix to get folder display name
            var folderName = StripSuffix(title, FileManagerSuffixes);
            if (string.IsNullOrEmpty(folderName)) folderName = title;
            context.RootTitle = folderName;

            // Try to resolve the folder name to an absolute path
            var resolvedPath = TryResolveFolderNameToPath(folderName);
            context.RootIdentifier = resolvedPath ?? folderName;

            if (resolvedPath != null)
            {
                context.Children.Add(new ContextChildItem
                {
                    ItemType = "folder",
                    Title = folderName,
                    Identifier = resolvedPath,
                });
            }
        }

        return context;
    }

    /// <summary>
    /// Tries to map a folder display name to an absolute path by looking in common locations.
    /// </summary>
    private static string? TryResolveFolderNameToPath(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Home folder itself
        if (folderName.Equals("Home", StringComparison.OrdinalIgnoreCase) ||
            folderName.Equals(System.IO.Path.GetFileName(home), StringComparison.OrdinalIgnoreCase))
            return home;

        // Check common locations
        var candidates = new[]
        {
            System.IO.Path.Combine(home, folderName),
            System.IO.Path.Combine(home, "Documents", folderName),
            System.IO.Path.Combine(home, "Desktop", folderName),
            System.IO.Path.Combine(home, "Downloads", folderName),
            System.IO.Path.Combine("/media", Environment.UserName, folderName),
            $"/{folderName}",
        };

        foreach (var candidate in candidates)
        {
            if (System.IO.Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string? ExtractUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = UrlRegex().Match(text);
        return match.Success ? match.Value.TrimEnd('.', ',', ';') : null;
    }

    public static string? ExtractPath(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Unix absolute path in title
        var unix = UnixPathRegex().Match(title);
        if (unix.Success) return unix.Value;

        // Windows path like C:\Users\...
        var win = WindowsPathRegex().Match(title);
        if (win.Success) return win.Value;

        // Home-relative ~/path
        var home = HomePathRegex().Match(title);
        if (home.Success)
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(homeDir, home.Groups[1].Value);
        }

        return null;
    }

    public static string NormalizeUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        try
        {
            var uri = new Uri(url);
            var builder = new UriBuilder(uri) { Fragment = string.Empty };
            return builder.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            return url.Trim().ToLowerInvariant();
        }
    }

    private static string StripSuffix(string title, IEnumerable<string> suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return title[..^suffix.Length].Trim();
        }
        return title;
    }

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?<![\w/])/[\w./_-]+")]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"~/(?<rest>[\w./_-]+)")]
    private static partial Regex HomePathRegex();
}
