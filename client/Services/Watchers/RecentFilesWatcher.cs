using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using client.Core.DesktopEventBus;

namespace client.Services.Watchers;

public class RecentFilesWatcher : IObservableEventSource
{
    private readonly ILogger<RecentFilesWatcher> _logger;
    private FileSystemWatcher? _watcher;
    private bool _isActive;
    private string? _recentFilesPath;
    private bool _isWindowsRecent;
    private DateTime _lastLnkResolve = DateTime.MinValue;
    private readonly HashSet<string> _recentlyEmitted = new(StringComparer.OrdinalIgnoreCase);

    public string SourceName => "recentfiles";
    public bool IsActive => _isActive;

    public event EventHandler<RawDesktopEvent>? EventRaised;

    public RecentFilesWatcher(ILogger<RecentFilesWatcher> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_isActive) return Task.CompletedTask;

        if (OperatingSystem.IsWindows())
        {
            StartWindowsRecent();
            return Task.CompletedTask;
        }

        // Linux/macOS: XDG recently-used.xbel (GNOME/XFCE write this; KDE uses kactivities —
        // not covered).
        var xdgDataDir = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _recentFilesPath = !string.IsNullOrEmpty(xdgDataDir)
            ? Path.Combine(xdgDataDir, "recently-used.xbel")
            : Path.Combine(home, ".local", "share", "recently-used.xbel");

        var dir = Path.GetDirectoryName(_recentFilesPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try
            {
                _watcher = new FileSystemWatcher(dir)
                {
                    Filter = "recently-used.xbel",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = false,
                };

                _watcher.Changed += OnRecentFilesChanged;
                _watcher.Error += (s, e) =>
                    _logger.LogWarning("RecentFilesWatcher error: {Msg}", e.GetException().Message);

                _watcher.EnableRaisingEvents = true;
                _isActive = true;
                _logger.LogInformation("RecentFilesWatcher started on {Path}", _recentFilesPath);

                ParseRecentFiles();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start RecentFilesWatcher");
            }
        }
        else
        {
            _logger.LogDebug("Recent files DB not found at {Path}", _recentFilesPath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Windows stores "recently opened files" as .lnk shortcuts in
    /// %APPDATA%\Microsoft\Windows\Recent. Every file opened from Explorer's jump list,
    /// Open/Save dialogs, or "Recent" shows up there. Resolve each .lnk to its real target
    /// (WScript.Shell COM — the OS's own shortcut reader) and emit an "open" event. This is
    /// the Windows equivalent of the Linux recently-used.xbel source.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void StartWindowsRecent()
    {
        var recentDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Recent");

        if (!Directory.Exists(recentDir))
        {
            _logger.LogDebug("Windows Recent folder not found at {Path}", recentDir);
            return;
        }

        try
        {
            _recentFilesPath = recentDir;
            _isWindowsRecent = true;

            _watcher = new FileSystemWatcher(recentDir)
            {
                Filter = "*.lnk",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = false,
            };

            _watcher.Created += OnRecentLnkCreated;
            _watcher.Renamed += OnRecentLnkCreated;
            _watcher.Changed += OnRecentLnkCreated;
            _watcher.Error += (s, e) =>
                _logger.LogWarning("RecentFilesWatcher error: {Msg}", e.GetException().Message);

            _watcher.EnableRaisingEvents = true;
            _isActive = true;
            _logger.LogInformation("RecentFilesWatcher started on {Path} (*.lnk)", recentDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start Windows RecentFilesWatcher");
        }
    }

    private void OnRecentLnkCreated(object sender, FileSystemEventArgs e)
    {
        if (!_isActive || !_isWindowsRecent) return;
        try
        {
            if (!e.FullPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return;

            // Explorer fires multiple events per jump-list action; collapse to one per 2s.
            var now = DateTime.UtcNow;
            if ((now - _lastLnkResolve).TotalSeconds < 2) return;
            _lastLnkResolve = now;

            var target = OperatingSystem.IsWindows() ? ResolveLnkTarget(e.FullPath) : null;
            if (string.IsNullOrEmpty(target) || !File.Exists(target)) return;

            // Suppress re-emission for the same file within a window (rename + change pair).
            if (!_recentlyEmitted.Add(target)) return;
            if (_recentlyEmitted.Count > 200)
                _recentlyEmitted.Clear();

            var raw = new RawDesktopEvent
            {
                Source = "recentfiles",
                EventType = "created",
                CurrentPath = target,
                RawData = e.FullPath,
                Timestamp = DateTime.UtcNow,
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    lnk = e.FullPath,
                    source = "windows-recent",
                }),
            };

            EventRaised?.Invoke(this, raw);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error resolving recent .lnk");
        }
    }

    /// <summary>Resolve a Windows .lnk shortcut to its target path via WScript.Shell COM.</summary>
    [SupportedOSPlatform("windows")]
    private static string? ResolveLnkTarget(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;

            dynamic? shell = null;
            dynamic? shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                if (shell == null) return null;
                shortcut = shell.CreateShortcut(lnkPath);
                if (shortcut == null) return null;
                return (string?)shortcut.TargetPath;
            }
            finally
            {
                if (shortcut != null) { try { Marshal.FinalReleaseComObject(shortcut); } catch { } }
                if (shell != null) { try { Marshal.FinalReleaseComObject(shell); } catch { } }
            }
        }
        catch
        {
            return null;
        }
    }

    public void Stop()
    {
        _isActive = false;
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _logger.LogInformation("RecentFilesWatcher stopped");
    }

    private DateTime _lastChangeTime = DateTime.MinValue;

    private void OnRecentFilesChanged(object sender, FileSystemEventArgs e)
    {
        if (!_isActive) return;

        if ((DateTime.UtcNow - _lastChangeTime).TotalMilliseconds < 1000)
            return;

        _lastChangeTime = DateTime.UtcNow;

        try
        {
            ParseRecentFiles();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error parsing recent files");
        }
    }

    private void ParseRecentFiles()
    {
        if (string.IsNullOrEmpty(_recentFilesPath) || !File.Exists(_recentFilesPath))
            return;

        var content = File.ReadAllText(_recentFilesPath);
        var doc = XDocument.Parse(content);

        if (doc.Root == null) return;

        var entries = doc.Root.Elements("bookmark")
            .OrderByDescending(e => (string?)e.Attribute("modified") ?? "")
            .Take(5);

        foreach (var entry in entries)
        {
            var href = (string?)entry.Attribute("href");
            if (string.IsNullOrEmpty(href)) continue;

            var modified = (string?)entry.Attribute("modified");
            var added = (string?)entry.Attribute("added");

            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;
            var localPath = uri.LocalPath;

            var raw = new RawDesktopEvent
            {
                Source = "recentfiles",
                EventType = "created",
                CurrentPath = localPath,
                RawData = href,
                Timestamp = DateTime.UtcNow,
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    modified,
                    added,
                    uri = href,
                }),
            };

            EventRaised?.Invoke(this, raw);
        }
    }
}
