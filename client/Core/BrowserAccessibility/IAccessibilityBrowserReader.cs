namespace client.Core.BrowserAccessibility;

/// <summary>
/// Reads the current state of browser windows from the OS accessibility tree —
/// no debugger, no extension, no catalog dependency. Works with ANY browser
/// (installed, portable, or removed 5 minutes later) as long as it renders a
/// window on screen.
///
/// Platform implementations:
///   Linux   → AT-SPI over D-Bus (python3 + dbus)
///   Windows → UI Automation (UIA)
///   macOS   → Accessibility API via osascript (requires the Accessibility grant)
/// </summary>
public interface IAccessibilityBrowserReader
{
    /// <summary>Human-readable platform name ("Linux", "Windows", "macOS").</summary>
    string Platform { get; }

    /// <summary>True when the reader can run on this machine (platform + tooling present).</summary>
    bool IsAvailable { get; }

    /// <summary>Snapshot every currently-visible browser window (active tab URL + title).</summary>
    Task<IReadOnlyList<AccessibilitySnapshot>> ReadAsync(CancellationToken ct);
}
