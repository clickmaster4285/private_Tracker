namespace client.Core.BrowserAccessibility;

/// <summary>
/// Dynamic browser registry that knows which processes are browsers
/// without any hardcoded product names. Sources from the installed-applications
/// inventory (IsBrowser flag set via .desktop Categories / URL associations /
/// bundle metadata) and refreshes every 5 minutes.
/// </summary>
public interface IBrowserRegistry
{
    /// <summary>True if the process name/command line belongs to a browser.</summary>
    bool IsBrowser(string processName);

    /// <summary>True if the process name or command line belongs to a browser.</summary>
    bool IsBrowser(string processName, string? commandLine);

    /// <summary>Human-readable display name for a browser process (e.g. "Google Chrome").</summary>
    string? GetDisplayName(string processName);

    /// <summary>All known browser process names (binary + display names).</summary>
    IReadOnlyList<string> GetAllBrowserProcessNames();

    /// <summary>All known browser display names (for AppleScript lists, etc.).</summary>
    IReadOnlyList<string> GetAllBrowserDisplayNames();
}
