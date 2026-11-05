using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Core.Abstractions;
using client.Core.DesktopEventBus;
using client.Services.Watchers;

namespace client.Services;

public class DesktopEventService : BackgroundService
{
    private readonly ILogger<DesktopEventService> _logger;
    private readonly EventCoordinator _coordinator;
    private readonly JourneyEngine _engine;
    private readonly ATSPIEventWatcher _atspiWatcher;
    private readonly WindowsExplorerWatcher _explorerWatcher;
    private readonly FileSystemEventWatcher _fsWatcher;
    private readonly RecentFilesWatcher _recentWatcher;
    private readonly ILogStore _store;
    private readonly List<IObservableEventSource> _watchers = new();
    private bool _initialized;

    public DesktopEventService(
        ILogStore store,
        ILogger<DesktopEventService> logger,
        EventCoordinator coordinator,
        JourneyEngine engine,
        ATSPIEventWatcher atspiWatcher,
        WindowsExplorerWatcher explorerWatcher,
        FileSystemEventWatcher fsWatcher,
        RecentFilesWatcher recentWatcher)
    {
        _store = store;
        _logger = logger;
        _coordinator = coordinator;
        _engine = engine;
        _atspiWatcher = atspiWatcher;
        _explorerWatcher = explorerWatcher;
        _fsWatcher = fsWatcher;
        _recentWatcher = recentWatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("DesktopEventService parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        await WaitForDatabaseAsync(stoppingToken);

        _logger.LogInformation("DesktopEventService starting");

        _coordinator.NormalizedEventRaised += OnNormalizedEvent;

        var startupTasks = new List<Task>();

        if (OperatingSystem.IsLinux())
        {
            startupTasks.Add(StartWatcherSafe(_atspiWatcher, stoppingToken));
        }
        else
        {
            _logger.LogInformation("AT-SPI watcher skipped (not Linux)");
        }

        if (OperatingSystem.IsWindows())
        {
            startupTasks.Add(StartWatcherSafe(_explorerWatcher, stoppingToken));
        }
        else
        {
            _logger.LogInformation("Windows Explorer watcher skipped (not Windows)");
        }

        startupTasks.Add(StartWatcherSafe(_fsWatcher, stoppingToken));
        startupTasks.Add(StartWatcherSafe(_recentWatcher, stoppingToken));

        await Task.WhenAll(startupTasks);

        _watchers.Add(_atspiWatcher);
        _watchers.Add(_explorerWatcher);
        _watchers.Add(_fsWatcher);
        _watchers.Add(_recentWatcher);

        foreach (var w in _watchers)
        {
            _coordinator.Subscribe(w);
        }

        _initialized = true;
        _logger.LogInformation("DesktopEventService started with {Count} watchers", _watchers.Count(w => w.IsActive));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _coordinator.CleanupStaleJourneys();
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }

        Shutdown();
    }

    private async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        for (int i = 0; i < 30; i++)
        {
            try
            {
                await _store.InitializeAsync(ct);
                return;
            }
            catch
            {
                await Task.Delay(1000, ct);
            }
        }
        _logger.LogWarning("Database init timeout — DesktopEventService will retry on each event");
    }

    private async Task StartWatcherSafe(IObservableEventSource watcher, CancellationToken ct)
    {
        try
        {
            await watcher.StartAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start watcher {Name}", watcher.SourceName);
        }
    }

    private void OnNormalizedEvent(object? sender, DesktopEvent evt)
    {
        _ = OnNormalizedEventAsync(evt);
    }

    private async Task OnNormalizedEventAsync(DesktopEvent evt)
    {
        try
        {
            if (!_initialized)
            {
                await _store.InitializeAsync(CancellationToken.None);
                _initialized = true;
            }

            // Journey-driven watching: when the user NAVIGATES an Explorer window to a folder
            // outside the fixed 6 user folders (e.g. C:\project, D:\Work), attach a recursive
            // watcher to it so file create/rename/delete there is captured too. On Linux the
            // AT-SPI watcher emits cwd-based navigations that flow through the same event.
            //
            // ONLY navigate actions may trigger this. File create/rename/delete/modify events
            // (including cache churn inside an already-watched broad folder) must never spawn
            // new watchers — that was the feedback loop: AppData\Local was watched, its cache
            // churn emitted "Folder/rename" events for extensionless files, and each one tried
            // to watch an even deeper cache folder.
            if (evt.ObjectType == "Folder" && evt.Action == "navigate" &&
                !string.IsNullOrEmpty(evt.CurrentPath))
            {
                _fsWatcher.EnsureWatching(evt.CurrentPath);
            }

            await _engine.ProcessEventAsync(evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing desktop event");
        }
    }

    private void Shutdown()
    {
        _coordinator.NormalizedEventRaised -= OnNormalizedEvent;
        _coordinator.UnsubscribeAll();

        foreach (var w in _watchers)
        {
            try { w.Stop(); } catch { }
        }

        _coordinator.Dispose();
        _logger.LogInformation("DesktopEventService stopped");
    }
}
