using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using Microsoft.Extensions.Logging;

namespace client.Core.BrowserAccessibility;

/// <summary>
/// Windows implementation of <see cref="IAccessibilityBrowserReader"/> using UI Automation
/// (the built-in UIAutomationCore COM API via the Interop.UIAutomationClient wrapper).
///
/// Walks top-level windows, keeps browser-owned ones, and reads the address bar through
/// the ValuePattern of the "Address and search bar" edit control (Chrome/Edge expose it;
/// Firefox exposes its address field the same way). Works on every browser and every
/// Chrome version — no debugger, no extension.
///
/// Property/pattern ids are the stable UIA contract constants (UIAutomationClient.h).
/// </summary>
public sealed class WindowsUiaBrowserReader : IAccessibilityBrowserReader
{
    private const int UIA_ProcessIdPropertyId = 30002;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_NamePropertyId = 30005;
    private const int UIA_AutomationIdPropertyId = 30011;
    private const int UIA_NativeWindowHandlePropertyId = 30020;
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_WindowControlTypeId = 50032;
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_ButtonControlTypeId = 50000;
    private const int UIA_DocumentControlTypeId = 50030;

    // user32 foreground window — the OS statement of which top-level window is focused.
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly ILogger<WindowsUiaBrowserReader> _logger;
    private readonly IBrowserRegistry _browserRegistry;

    public string Platform => "Windows";
    public bool IsAvailable => OperatingSystem.IsWindows();

    public WindowsUiaBrowserReader(ILogger<WindowsUiaBrowserReader> logger, IBrowserRegistry browserRegistry)
    {
        _logger = logger;
        _browserRegistry = browserRegistry;
    }

    public Task<IReadOnlyList<AccessibilitySnapshot>> ReadAsync(CancellationToken ct)
    {
        var result = new List<AccessibilitySnapshot>();
        if (!OperatingSystem.IsWindows() || ct.IsCancellationRequested)
            return Task.FromResult<IReadOnlyList<AccessibilitySnapshot>>(result);

        CUIAutomationClass? automation = null;
        try
        {
            automation = new CUIAutomationClass();
            var uia = (IUIAutomation)automation;
            var root = uia.GetRootElement();
            var windowCondition = uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_WindowControlTypeId);
            var windows = root.FindAll(TreeScope.TreeScope_Children, windowCondition);

            // OS focus: the foreground top-level window (user32 GetForegroundWindow) is
            // the one the tracker credits with foreground time each poll.
            var fgHwnd = GetForegroundWindow();

            for (var i = 0; i < windows.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var element = windows.GetElement(i);
                    var pid = element.CurrentProcessId;
                    var title = element.CurrentName ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(title) || pid <= 0)
                        continue;

                    string processName;
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        processName = proc.ProcessName;
                    }
                    catch
                    {
                        continue;
                    }

                    var isBrowser = _browserRegistry.IsBrowser(processName);
                    string url;
                    if (isBrowser)
                    {
                        url = ReadAddressBar(element, uia);
                    }
                    else
                    {
                        // Embedded webview (Electron apps: VS Code Simple Browser, Slack,
                        // Teams, …) — STRUCTURAL detection, no app-name lists: a descendant
                        // document/edit exposing an http(s) URL via the Value pattern.
                        url = ReadWebviewUrl(element, uia);
                        if (string.IsNullOrWhiteSpace(url))
                            continue; // no embedded web content → not a journey window
                    }

                    result.Add(new AccessibilitySnapshot
                    {
                        WindowKey = $"uia:{pid}:{element.CurrentNativeWindowHandle.ToInt64()}",
                        ProcessId = pid,
                        ProcessName = processName,
                        WindowTitle = title,
                        Url = BrowserAccessibilityHelpers.NormalizeUrl(url),
                        // Chrome/Edge never put "incognito" in the window TITLE on Windows —
                        // the only reliable signal is the dedicated toggle BUTTON that the
                        // browsers expose in their accessibility tree ("Incognito"/"InPrivate").
                        IsIncognito = BrowserAccessibilityHelpers.TitleSuggestsIncognito(title)
                                     || DetectIncognito(element, uia),
                        // Foreground window: matches the HWND returned by GetForegroundWindow.
                        IsActive = fgHwnd != IntPtr.Zero && element.CurrentNativeWindowHandle == fgHwnd,
                        IsWebview = !isBrowser,
                    });
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UI Automation enumeration failed");
        }
        finally
        {
            if (automation is not null)
            {
                try
                {
                    if (automation is IDisposable disposable)
                        disposable.Dispose();
                    else
                        Marshal.ReleaseComObject(automation);
                }
                catch { }
            }
        }

        return Task.FromResult<IReadOnlyList<AccessibilitySnapshot>>(result);
    }

    /// <summary>
    /// Detect an incognito/private window by looking for the browser's dedicated
    /// incognito toggle button in the window's accessibility tree. Chrome exposes a
    /// button named "Incognito", Edge one named "InPrivate" (verified live via UIA).
    /// Firefox on Windows shows "Private Browsing" chrome that has no button; its
    /// window title heuristic is handled by <see cref="BrowserAccessibilityHelpers.TitleSuggestsIncognito"/>.
    /// The search is intentionally cheap: one descendant scan restricted to buttons.
    /// </summary>
private static bool DetectIncognito(IUIAutomationElement window, IUIAutomation uia)
    {
        try
        {
            // The browser's own incognito toggle lives in the toolbar CHROME at the top of
            // the window. Restricting the scan to that zone avoids false positives from
            // PAGE CONTENT buttons (a webpage rendering an "Incognito" button would otherwise
            // flag a normal window — and the incognito flag gates URL capture downstream).
            var windowRect = window.CurrentBoundingRectangle;
            var toolbarBottom = windowRect.top + Math.Max(140, (int)((windowRect.bottom - windowRect.top) * 0.3));

            var buttonCondition = uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_ButtonControlTypeId);
            var buttons = window.FindAll(TreeScope.TreeScope_Descendants, buttonCondition);
            for (var i = 0; i < buttons.Length; i++)
            {
                try
                {
                    var button = buttons.GetElement(i);
                    var rect = button.CurrentBoundingRectangle;
                    if (rect.top >= windowRect.top && rect.bottom <= toolbarBottom)
                    {
                        var name = button.CurrentName ?? string.Empty;
                        var autoId = button.CurrentAutomationId ?? string.Empty;
                        if (name.Contains("incognito", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("inprivate", StringComparison.OrdinalIgnoreCase) ||
                            autoId.Contains("incognito", StringComparison.OrdinalIgnoreCase) ||
                            autoId.Contains("inprivate", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Structural webview-URL detection for non-browser windows: scans the window's
    /// descendants for a Document or Edit control whose Value (or Name) is an http(s)
    /// URL. Chromium/Electron webviews expose the embedded page this way; app chrome
    /// (settings/preview panes) never exposes an http URL here, so no app-name list is
    /// needed — only windows with real web content are emitted.
    /// </summary>
    private static string ReadWebviewUrl(IUIAutomationElement window, IUIAutomation uia)
    {
        try
        {
            var docCondition = uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_DocumentControlTypeId);
            var docs = window.FindAll(TreeScope.TreeScope_Descendants, docCondition);
            var valuePatternCondition = uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_EditControlTypeId);
            var edits = window.FindAll(TreeScope.TreeScope_Descendants, valuePatternCondition);

            for (var i = 0; i < docs.Length; i++)
            {
                try
                {
                    var url = ReadUrlValue(docs.GetElement(i));
                    if (url.Length > 0) return url;
                }
                catch { }
            }
            for (var i = 0; i < edits.Length; i++)
            {
                try
                {
                    var url = ReadUrlValue(edits.GetElement(i));
                    if (url.Length > 0) return url;
                }
                catch { }
            }
        }
        catch { }
        return string.Empty;
    }

    private static string ReadUrlValue(IUIAutomationElement element)
    {
        string? value = null;
        try
        {
            var pattern = element.GetCurrentPattern(UIA_ValuePatternId);
            if (pattern is IUIAutomationValuePattern valuePattern)
                value = valuePattern.CurrentValue;
        }
        catch { }
        value ??= element.CurrentName;
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : string.Empty;
    }

    private static string ReadAddressBar(IUIAutomationElement window, IUIAutomation uia)
    {
        try
        {
            var editCondition = uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_EditControlTypeId);
            var edits = window.FindAll(TreeScope.TreeScope_Descendants, editCondition);
            if (edits.Length == 0) return string.Empty;

            IUIAutomationElement? addressBar = null;
            for (var i = 0; i < edits.Length; i++)
            {
                try
                {
                    var edit = edits.GetElement(i);
                    var name = edit.CurrentName ?? string.Empty;
                    var automationId = edit.CurrentAutomationId ?? string.Empty;
                    if (name == "Address and search bar" || automationId == "Address and search bar" ||
                        name == "Search or enter address")
                    {
                        addressBar = edit;
                        break;
                    }
                    addressBar ??= edit;
                }
                catch { }
            }

            if (addressBar == null) return string.Empty;

            try
            {
                var pattern = addressBar.GetCurrentPattern(UIA_ValuePatternId);
                if (pattern is IUIAutomationValuePattern valuePattern)
                    return valuePattern.CurrentValue ?? string.Empty;
            }
            catch { }

            return addressBar.CurrentName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
