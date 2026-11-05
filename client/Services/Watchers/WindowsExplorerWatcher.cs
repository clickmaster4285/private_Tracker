using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using client.Core.DesktopEventBus;

namespace client.Services.Watchers;

/// <summary>
/// Windows file-explorer journey source. Polls the Windows shell's OWN window registry
/// (Shell COM: Shell.Application → Windows()) and reports every Explorer window's current
/// folder — the same data the OS uses to draw the window's address bar. No UIA walking,
/// no product-name lists: each window exposes a file:// LocationURL that is its exact
/// browsed folder.
///
/// On Linux the equivalent journey source is <see cref="ATSPIEventWatcher"/> (focus/window
/// events + /proc cwd); Windows has no such accessibility signal for the shell, so this
/// watcher polls the shell directly.
/// </summary>
public sealed class WindowsExplorerWatcher : IObservableEventSource, IExplorerWindowProvider
{
    private readonly ILogger<WindowsExplorerWatcher> _logger;
    private readonly CancellationTokenSource _cts = new();
    // Written by the poll background task, read concurrently from the FileSystemWatcher event
    // thread (EventCoordinator → TryGetWindowForPath). ConcurrentDictionary keeps the two
    // without locks — a plain Dictionary would risk InvalidOperationException under race.
    private readonly ConcurrentDictionary<long, string> _windowFolders = new(); // hwnd → current folder
    private readonly ConcurrentDictionary<long, string> _windowTitles = new();   // hwnd → window title
    private bool _isActive;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public string SourceName => "explorer";
    public bool IsActive => _isActive;

    public event EventHandler<RawDesktopEvent>? EventRaised;

    public WindowsExplorerWatcher(ILogger<WindowsExplorerWatcher> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_isActive || !OperatingSystem.IsWindows()) return Task.CompletedTask;
        _isActive = true;
        _ = Task.Run(PollLoopAsync);
        _logger.LogInformation("Windows Explorer watcher started");
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _isActive = false;
        try { _cts.Cancel(); } catch { }
        _windowFolders.Clear();
        _windowTitles.Clear();
        _logger.LogInformation("Windows Explorer watcher stopped");
    }

    /// <summary>
    /// Best-matching Explorer window for a filesystem event path: the window whose current
    /// folder is the longest ancestor (or exact match) of the path. Enables raw
    /// FileSystemWatcher events — which carry no window identity — to join the Explorer
    /// journey that actually performed the create/rename/delete.
    /// </summary>
    public bool TryGetWindowForPath(string path, out int windowId, out string windowTitle, out string processName)
    {
        windowId = 0;
        windowTitle = string.Empty;
        processName = string.Empty;
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return false;

        string? bestFolder = null;
        foreach (var kv in _windowFolders)
        {
            var f = kv.Value;
            if (f.Length == 0) continue;
            if (path.StartsWith(f, StringComparison.OrdinalIgnoreCase) &&
                (path.Length == f.Length || path[f.Length] == '\\' || path[f.Length] == '/') &&
                (bestFolder == null || f.Length > bestFolder.Length))
            {
                bestFolder = f;
                windowId = (int)kv.Key;
                windowTitle = _windowTitles.GetValueOrDefault(kv.Key, string.Empty);
            }
        }

        if (windowId == 0) return false;
        processName = "explorer"; // the Windows shell — platform constant, not a product name
        return true;
    }

    private async Task PollLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    PollOnce();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Explorer poll error");
            }

            try { await Task.Delay(PollInterval, _cts.Token); }
            catch (OperationCanceledException) { }
        }
    }

    [SupportedOSPlatform("windows")]
    private void PollOnce()
    {
        var current = new Dictionary<long, string>(); // hwnd → folder this poll
        var titles = new Dictionary<long, string>();
        object? shell = null;
        dynamic? windows = null;
        var items = new List<object>();

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            windows = ((dynamic)shell).Windows();
            if (windows == null) return;

            int count = (int)windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic w = windows.Item(i);
                    items.Add(w);

                    string url;
                    try { url = (string)w.LocationURL ?? string.Empty; }
                    catch { continue; }

                    // Only real filesystem views (Explorer windows). "This PC", Recycle Bin,
                    // libraries and other shell namespace views have shell: URLs — not file
                    // operations, so they are skipped like the browser tracker skips non-http.
                    if (!url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                        continue;

                    long hwnd;
                    try { hwnd = (long)w.HWND; }
                    catch { continue; }
                    if (hwnd == 0) continue;

                    // Only the shell's own file-manager windows. This excludes file-open/save
                    // dialogs hosted by other apps (Word, notepad, …) from polluting the
                    // Explorer journey with app-owned navigations.
                    if (!IsExplorerProcess(hwnd)) continue;

                    string folder;
                    try { folder = new Uri(url).LocalPath; }
                    catch { continue; }
                    if (string.IsNullOrWhiteSpace(folder)) continue;

                    folder = folder.TrimEnd('\\');
                    if (folder.Length == 0) continue;

                    current[hwnd] = folder;
                    string title;
                    try { title = (string)w.LocationName ?? string.Empty; }
                    catch { title = string.Empty; }
                    titles[hwnd] = title;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Explorer enumeration failed");
        }
        finally
        {
            ReleaseCom(windows);
            foreach (var item in items) ReleaseCom(item);
            ReleaseCom(shell);
        }

        // New windows / folder changes → navigate.
        foreach (var kv in current)
        {
            if (_windowFolders.TryGetValue(kv.Key, out var old) &&
                string.Equals(old, kv.Value, StringComparison.OrdinalIgnoreCase))
                continue;

            _windowFolders[kv.Key] = kv.Value;
            _windowTitles[kv.Key] = titles.GetValueOrDefault(kv.Key, string.Empty);
            Emit("navigate", kv.Key, kv.Value, old);
        }

        // Vanished windows → close.
        foreach (var hwnd in _windowFolders.Keys.Where(h => !current.ContainsKey(h)).ToList())
        {
            var folder = _windowFolders.TryGetValue(hwnd, out var f) ? f : string.Empty;
            var title = _windowTitles.TryGetValue(hwnd, out var t) ? t : string.Empty;
            _windowFolders.TryRemove(hwnd, out _);
            _windowTitles.TryRemove(hwnd, out _);
            Emit("close", hwnd, folder, null, title);
        }
    }

    private void Emit(string eventType, long hwnd, string folder, string? previous, string? title = null)
    {
        EventRaised?.Invoke(this, new RawDesktopEvent
        {
            Source = "explorer",
            EventType = eventType,
            AppName = "explorer",
            WindowTitle = title ?? Path.GetFileName(folder),
            WindowId = (int)hwnd,
            PreviousPath = previous ?? string.Empty,
            CurrentPath = folder,
            Timestamp = DateTime.UtcNow,
            MetadataJson = JsonSerializer.Serialize(new { source = "shell", hwnd }),
        });
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [SupportedOSPlatform("windows")]
    private static bool IsExplorerProcess(long hwnd)
    {
        try
        {
            if (GetWindowThreadProcessId(new IntPtr(hwnd), out var pid) == 0 || pid == 0)
                return false;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReleaseCom(object? obj)
    {
        try
        {
            if (obj != null && Marshal.IsComObject(obj))
                Marshal.FinalReleaseComObject(obj);
        }
        catch { }
    }
}
