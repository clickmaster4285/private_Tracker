using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using client.Services.Watchers;

namespace client.Core.DesktopEventBus;

public class EventCoordinator : IDisposable
{
    private readonly ILogger<EventCoordinator> _logger;
    private readonly IExplorerWindowProvider? _explorerProvider;
    private readonly ConcurrentDictionary<string, JourneyRecord> _activeJourneys = new();
    private readonly ConcurrentDictionary<string, DateTime> _dedupCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _correlationCache = new();
    private readonly List<IObservableEventSource> _sources = new();
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan JourneyTimeout = TimeSpan.FromMinutes(15);
    private static readonly int MaxJourneys = 500;

    public event EventHandler<DesktopEvent>? NormalizedEventRaised;

    public EventCoordinator(ILogger<EventCoordinator> logger, IExplorerWindowProvider? explorerProvider = null)
    {
        _logger = logger;
        _explorerProvider = explorerProvider;
    }

    public void Subscribe(IObservableEventSource source)
    {
        _sources.Add(source);
        source.EventRaised += OnRawEvent;
        _logger.LogDebug("EventCoordinator subscribed to {Source}", source.SourceName);
    }

    public void UnsubscribeAll()
    {
        foreach (var source in _sources)
        {
            source.EventRaised -= OnRawEvent;
        }
        _sources.Clear();
    }

    public void CleanupStaleJourneys()
    {
        var cutoff = DateTime.UtcNow - JourneyTimeout;
        foreach (var kv in _activeJourneys)
        {
            if (kv.Value.LastEventAt < cutoff)
            {
                _activeJourneys.TryRemove(kv.Key, out _);
                _logger.LogDebug("Reaped stale journey {JourneyId}", kv.Key);
            }
        }
    }

    private void OnRawEvent(object? sender, RawDesktopEvent raw)
    {
        try
        {
            if (DesktopEventValidator.IsIgnoredProcess(raw.AppName)) return;

            // Windows: raw FileSystemWatcher events carry no window identity — attribute them
            // to the Explorer window that is browsing the containing folder so the create /
            // rename / delete joins the SAME journey as the navigation (same WindowId key).
            if (_explorerProvider != null &&
                raw.Source == "filesystem" &&
                !raw.WindowId.HasValue &&
                !string.IsNullOrEmpty(raw.CurrentPath))
            {
                if (_explorerProvider.TryGetWindowForPath(raw.CurrentPath,
                        out var winId, out var winTitle, out var procName))
                {
                    raw.AppName = procName;
                    raw.WindowTitle = winTitle;
                    raw.WindowId = winId;
                }
            }

            var normalized = Normalize(raw);
            if (normalized == null) return;

            if (IsDuplicate(normalized)) return;

            var journeyKey = GetJourneyKey(normalized);
            var journey = GetOrCreateJourney(journeyKey, normalized);
            normalized.JourneyId = journey.JourneyId;
            normalized.Sequence = ++journey.Sequence;
            journey.CurrentPath = normalized.CurrentPath ?? string.Empty;
            journey.LastEventAt = DateTime.UtcNow;

            TrackCorrelation(normalized);

            NormalizedEventRaised?.Invoke(this, normalized);

            LimitCacheSize();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in EventCoordinator.OnRawEvent");
        }
    }

    private DesktopEvent? Normalize(RawDesktopEvent raw)
    {
        var (objectType, action) = InferObjectTypeAndAction(raw);

        if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(action))
            return null;

        return new DesktopEvent
        {
            ObjectType = objectType,
            Action = action,
            AppName = raw.AppName,
            WindowTitle = raw.WindowTitle,
            WindowId = raw.WindowId,
            TabId = raw.TabId,
            PreviousPath = raw.PreviousPath,
            CurrentPath = raw.CurrentPath,
            Timestamp = raw.Timestamp,
            MetadataJson = raw.MetadataJson,
        };
    }

    private static (string objectType, string action) InferObjectTypeAndAction(RawDesktopEvent raw)
    {
        var eventType = raw.EventType;
        var path = raw.CurrentPath;
        var src = raw.Source;

        var objectType = InferObjectType(path, eventType, src);
        var action = InferAction(eventType, src);

        return (objectType, action);
    }

    private static string InferObjectType(string? path, string eventType, string source)
    {
        if (string.IsNullOrEmpty(path) && source == "atspi" && eventType.Contains("window", StringComparison.OrdinalIgnoreCase))
            return "Window";

        if (string.IsNullOrEmpty(path))
            return string.Empty;

        try
        {
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                path = new Uri(path).LocalPath;
        }
        catch { return string.Empty; }

        // Prefer the OS's own answer first. Browser cache churn creates/renames/deletes the
        // target before the debounce fires, so File.GetAttributes can throw even though the
        // path was a real file ("Local State", "TransportSecurity" — extensionless FILES
        // that the old extension-heuristic misread as folders).
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Directory) != 0)
                return "Folder";
            return "File";
        }
        catch { }

        if (path.EndsWith('/'))
            return "Folder";

        // Ambiguous (path already gone): use the platform convention. On Windows an
        // extensionless name is normally a FILE (browser config stores: Local State,
        // Cookies, Preferences, History…); on Linux an extensionless path is normally a
        // directory. Structural rule, not a product list.
        if (OperatingSystem.IsWindows() && path.Length >= 2 && path[1] == ':')
            return "File";

        return "Folder";
    }

    private static string InferAction(string eventType, string source)
    {
        return source switch
        {
            "atspi" => eventType switch
            {
                "focus" => "navigate",
                "window:activate" => "open",
                "window:deactivate" => "close",
                "window:create" => "open",
                "window:destroy" => "close",
                "name_change" => "rename",
                _ => eventType,
            },
            "filesystem" => eventType switch
            {
                "created" => "create",
                "deleted" => "delete",
                "renamed" => "rename",
                "changed" => "modify",
                _ => eventType,
            },
            // The Windows Explorer watcher emits raw events with the same vocabulary as the
            // AT-SPI watcher: navigate on folder change, close when a window disappears.
            "explorer" => eventType switch
            {
                "navigate" => "navigate",
                "close" => "close",
                _ => eventType,
            },
            "recentfiles" => eventType switch
            {
                "created" => "open",
                "updated" => "open",
                _ => eventType,
            },
            _ => eventType,
        };
    }

    private bool IsDuplicate(DesktopEvent evt)
    {
        var dedupKey = $"{evt.ObjectType}:{evt.Action}:{evt.CurrentPath ?? ""}";
        if (_dedupCache.TryGetValue(dedupKey, out var lastTime))
        {
            if (DateTime.UtcNow - lastTime < DedupWindow)
                return true;
        }

        var correlationKey = $"{evt.CurrentPath ?? ""}:{evt.Action}";
        if (_correlationCache.TryGetValue(correlationKey, out var lastCorrelation))
        {
            if (DateTime.UtcNow - lastCorrelation < CorrelationWindow)
                return true;
        }

        _dedupCache[dedupKey] = DateTime.UtcNow;
        return false;
    }

    private void TrackCorrelation(DesktopEvent evt)
    {
        var correlationKey = $"{evt.CurrentPath ?? ""}:{evt.Action}";
        _correlationCache[correlationKey] = DateTime.UtcNow;
    }

    private static string GetJourneyKey(DesktopEvent evt)
    {
        if (evt.WindowId.HasValue)
            return $"fm:win:{evt.WindowId.Value}";
        if (evt.TabId.HasValue)
            return $"fm:tab:{evt.AppName ?? "unknown"}:{evt.TabId.Value}";
        return $"fm:app:{evt.AppName ?? "unknown"}";
    }

    private JourneyRecord GetOrCreateJourney(string key, DesktopEvent evt)
    {
        if (_activeJourneys.TryGetValue(key, out var existing))
            return existing;

        var record = new JourneyRecord
        {
            JourneyId = Guid.NewGuid().ToString("N"),
            AppSessionId = string.Empty,
            ObjectType = evt.ObjectType,
            CurrentPath = evt.CurrentPath ?? string.Empty,
            StartedAt = DateTime.UtcNow,
            LastEventAt = DateTime.UtcNow,
            WindowId = evt.WindowId,
            TabId = evt.TabId,
        };

        _activeJourneys[key] = record;
        _logger.LogDebug("Created journey {JourneyId} for {Key}", record.JourneyId, key);
        return record;
    }

    internal IReadOnlyDictionary<string, JourneyRecord> ActiveJourneys =>
        _activeJourneys;

    private void LimitCacheSize()
    {
        if (_dedupCache.Count > 2000)
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
            foreach (var kv in _dedupCache.Where(kv => kv.Value < cutoff).ToList())
                _dedupCache.TryRemove(kv.Key, out _);
        }

        if (_activeJourneys.Count > MaxJourneys)
        {
            var toRemove = _activeJourneys
                .OrderBy(kv => kv.Value.LastEventAt)
                .Take(50)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in toRemove)
                _activeJourneys.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        UnsubscribeAll();
        _activeJourneys.Clear();
        _dedupCache.Clear();
        _correlationCache.Clear();
    }
}
