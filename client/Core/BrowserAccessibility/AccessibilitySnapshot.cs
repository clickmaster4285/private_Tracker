namespace client.Core.BrowserAccessibility;

/// <summary>
/// A single browser window as observed through the OS accessibility tree.
/// The address-bar text is the current URL of the active tab (browsers render the
/// omnibox without the scheme — see <see cref="BrowserAccessibilityHelpers.NormalizeUrl"/>).
/// </summary>
public sealed class AccessibilitySnapshot
{
    /// <summary>Stable per-window identity provided by the reader (a11y object path, UIA runtime id, ...).</summary>
    public required string WindowKey { get; init; }

    public int ProcessId { get; init; }

    /// <summary>Process name, e.g. "chrome", "firefox".</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>Window title as shown in the OS window list (page title + browser suffix).</summary>
    public string WindowTitle { get; init; } = string.Empty;

    /// <summary>Normalized URL of the active tab, or null/empty when the address bar is not readable.</summary>
    public string? Url { get; init; }

    /// <summary>True when the window is an incognito/private window (heuristic).</summary>
    public bool IsIncognito { get; init; }

    /// <summary>
    /// True when the OS marks this window as the ACTIVE/FOCUSED window (AT-SPI
    /// STATE_ACTIVE/STATE_FOCUSED on Linux, foreground-window HWND on Windows, frontmost
    /// process on macOS). The tracker credits exactly one tracked window per poll with
    /// foreground time; all other open windows earn background time.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Where the URL came from: "accessibility" (address-bar tree) or "history"
    /// (browser profile history DB fallback — used when the a11y tree cannot expose
    /// the omnibox, e.g. Linux Chrome 136+ / snap Firefox).
    /// </summary>
    public string UrlSource { get; init; } = "accessibility";

    /// <summary>
    /// True when this window is NOT a real browser but embeds web content — an
    /// Electron/embedded webview (VS Code Simple Browser, Slack, Teams, …) detected
    /// STRUCTURALLY: its accessibility tree contains a document node exposing an
    /// http(s) URL. No product-name lists — any app with an embedded http document
    /// is tracked; app chrome (settings/preview panes, vscode-webview:// URLs) is
    /// excluded by the URL scheme itself. Metadata "source" = "webview" for these.
    /// </summary>
    public bool IsWebview { get; init; }

    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
}
