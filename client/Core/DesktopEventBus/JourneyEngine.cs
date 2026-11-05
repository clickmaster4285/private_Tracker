using System.Text.Json;
using Microsoft.Extensions.Logging;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Core.DesktopEventBus;

public class JourneyEngine
{
    private readonly ILogStore _store;
    private readonly ILogger<JourneyEngine> _logger;
    private readonly client.Configuration.AppConfig _config;
    private readonly Dictionary<string, string> _journeyAppSessionCache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly HashSet<string> FileManagerProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "nautilus", "org.gnome.Nautilus",
        "dolphin", "org.kde.dolphin",
        "thunar", "nemo", "caja", "pcmanfm",
    };

    private static readonly HashSet<string> JourneyActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "navigate", "open", "create", "delete", "rename", "modify", "close",
    };

    public JourneyEngine(ILogStore store, ILogger<JourneyEngine> logger, client.Configuration.AppConfig config)
    {
        _store = store;
        _logger = logger;
        _config = config;
    }

    public async Task ProcessEventAsync(DesktopEvent evt, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!JourneyActions.Contains(evt.Action)) return;

            var appSessionId = await ResolveAppSessionAsync(evt, ct);

            var itemType = DeriveItemType(evt);

            if (evt.Action == "close")
            {
                await HandleCloseAsync(evt, appSessionId, ct);
                return;
            }

            var existing = await _store.GetOpenJourneyEventAsync(
                evt.JourneyId, evt.ObjectType, evt.Action, evt.CurrentPath ?? "", ct);

            if (existing != null)
            {
                _logger.LogTrace("Duplicate journey event: {J}/{O}/{A} {Path}",
                    evt.JourneyId, evt.ObjectType, evt.Action, evt.CurrentPath);
                return;
            }

            var rootItemId = await EnsureRootItemAsync(evt, appSessionId, ct);

            var navItem = new AppItem
            {
                AppSessionId = appSessionId,
                ParentItemId = rootItemId,
                ItemType = itemType,
                Title = BuildTitle(evt),
                Identifier = evt.CurrentPath ?? evt.PreviousPath ?? string.Empty,
                OpenedAt = evt.Timestamp,
                ObjectType = evt.ObjectType,
                Action = evt.Action,
                JourneyId = evt.JourneyId,
                Sequence = evt.Sequence,
                PreviousPath = evt.PreviousPath ?? string.Empty,
                CurrentPath = evt.CurrentPath ?? string.Empty,
                WindowId = evt.WindowId,
                TabId = evt.TabId,
                MetadataJson = evt.MetadataJson,
            };

            await _store.StoreAppItemsAsync(new[] { navItem }, ct);

            _logger.LogDebug("Journey {Seq}/{J}: {O}/{A} → {Path}",
                evt.Sequence, evt.JourneyId[..8], evt.ObjectType, evt.Action, evt.CurrentPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in JourneyEngine.ProcessEventAsync");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ResolveAppSessionAsync(DesktopEvent evt, CancellationToken ct)
    {
        var journeyKey = $"journey:{evt.JourneyId}";
        if (_journeyAppSessionCache.TryGetValue(journeyKey, out var cached))
            return cached;

        var processName = evt.AppName ?? "file-manager";
        var appDisplayName = evt.AppDisplayName ?? GuessDisplayName(processName);

        var session = new AppSession
        {
            ProcessName = processName,
            AppDisplayName = appDisplayName,
            StartedAt = evt.Timestamp,
            MachineId = _config.ClientId,
            SessionId = string.Empty,
            Platform = GetPlatform(),
        };

        await _store.StoreAppSessionsAsync(new[] { session }, ct);

        _journeyAppSessionCache[journeyKey] = session.Id;

        if (_journeyAppSessionCache.Count > 500)
        {
            var keys = _journeyAppSessionCache.Keys.Take(100).ToList();
            foreach (var k in keys) _journeyAppSessionCache.Remove(k);
        }

        return session.Id;
    }

    private async Task<string?> EnsureRootItemAsync(DesktopEvent evt, string appSessionId, CancellationToken ct)
    {
        if (evt.WindowId.HasValue)
        {
            var existing = await _store.GetOpenAppItemAsync(
                appSessionId, "window", $"win:{evt.WindowId}", ct);
            if (existing != null) return existing.Id;
        }

        // FileSystemWatcher / RecentFiles events carry NO WindowId, so the window-root
        // lookup above never matches and every event used to mint a fresh file_manager_tab
        // root (the 5-roots-per-session duplication). Reuse any open root tab instead.
        var openTab = await _store.GetOpenRootItemAsync(appSessionId, "file_manager_tab", ct);
        if (openTab != null) return openTab.Id;

        var rootItem = new AppItem
        {
            AppSessionId = appSessionId,
            ItemType = "file_manager_tab",
            Title = evt.WindowTitle ?? GuessDisplayName(evt.AppName ?? "file-manager"),
            Identifier = evt.CurrentPath ?? string.Empty,
            OpenedAt = evt.Timestamp,
            ObjectType = "Window",
            Action = "open",
            JourneyId = evt.JourneyId,
            Sequence = 0,
            CurrentPath = evt.CurrentPath ?? string.Empty,
            WindowId = evt.WindowId,
            TabId = evt.TabId,
        };

        await _store.StoreAppItemsAsync(new[] { rootItem }, ct);
        return rootItem.Id;
    }

    private async Task HandleCloseAsync(DesktopEvent evt, string appSessionId, CancellationToken ct)
    {
        var journeyKey = $"journey:{evt.JourneyId}";
        _journeyAppSessionCache.Remove(journeyKey);

        await _store.CloseAppItemsBySessionIdsAsync(new[] { appSessionId }, evt.Timestamp, ct);

        await _store.StoreAppSessionsAsync(new[]
        {
            new AppSession { Id = appSessionId, ProcessName = string.Empty, EndedAt = evt.Timestamp }
        }, ct);

        _logger.LogDebug("Closed journey {JourneyId} session {SessionId}",
            evt.JourneyId[..8], appSessionId[..8]);
    }

    private static string DeriveItemType(DesktopEvent evt)
    {
        if (evt.Action == "navigate" || evt.Action == "open")
            return $"fm_{evt.ObjectType.ToLowerInvariant()}_nav";
        if (evt.Action is "create" or "delete" or "rename" or "modify")
            return $"fm_{evt.ObjectType.ToLowerInvariant()}_{evt.Action}";
        return "fm_event";
    }

    private static string BuildTitle(DesktopEvent evt)
    {
        var label = evt.ObjectType == "Folder" ? "Navigate to" : evt.Action;
        var pathLabel = !string.IsNullOrEmpty(evt.CurrentPath)
            ? Path.GetFileName(evt.CurrentPath.TrimEnd('/'))
            : string.Empty;
        if (!string.IsNullOrEmpty(pathLabel))
            return $"{label}: {pathLabel}";
        return $"{label}: {evt.CurrentPath ?? "(unknown)"}";
    }

    private static string GuessDisplayName(string processName)
    {
        return processName.ToLowerInvariant() switch
        {
            "nautilus" or "org.gnome.nautilus" => "Files",
            "dolphin" or "org.kde.dolphin" => "Dolphin",
            "thunar" => "Thunar",
            "nemo" => "Nemo",
            "caja" => "Caja",
            "pcmanfm" => "PCManFM",
            "explorer" or "explorer.exe" => "File Explorer",
            _ => processName,
        };
    }

    private static string GetPlatform()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return "Linux";
    }
}
