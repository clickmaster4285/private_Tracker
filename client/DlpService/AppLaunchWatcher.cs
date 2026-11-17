namespace AlphaAITracker.Service;

/// <summary>
/// Backup only while AppLocker verify is not enforcing. Polls user processes and
/// kills anything outside bootstrap + allow.apps, then records one block notice
/// for the tracker dialog. Stays idle once <c>verify=enforcing</c>.
/// </summary>
public sealed class AppLaunchWatcher : BackgroundService
{
    private readonly ILogger<AppLaunchWatcher> _logger;

    public AppLaunchWatcher(ILogger<AppLaunchWatcher> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("AppLaunchWatcher parked (not Windows)");
            return;
        }

        _logger.LogInformation("AppLaunchWatcher running (idle until AppLocker verify fails)");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (armed, apps) = EnforceStatus.Shared.WatchSnapshot();
                if (armed)
                {
                    var sweep = AppProcessGuard.Sweep(apps, (name, path) =>
                    {
                        EnforceStatus.Shared.NoteAppBlock(name, path);
                        _logger.LogWarning("App block (AppLocker not enforcing): {Name} {Path}", name, path);
                    });
                    if (sweep.Killed > 0)
                        _logger.LogWarning("AppLaunchWatcher killed {Count}", sweep.Killed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "AppLaunchWatcher pass failed");
            }

            try
            {
                await Task.Delay(400, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
