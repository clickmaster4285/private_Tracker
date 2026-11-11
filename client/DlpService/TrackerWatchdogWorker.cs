namespace AlphaAITracker.Service;

/// <summary>
/// Keeps the user-session tracker alive after Task Manager "End Task".
/// LocalSystem cannot make a process truly unkillable for a local admin, but
/// restarting within seconds restores tracking. Skips when teardown.ok exists
/// (authorized uninstall) or device.auth is missing (logged out / wiped).
/// Always relaunches the normal headless <c>--background</c> tracker when its mutex is free.
/// </summary>
internal sealed class TrackerWatchdogWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly ILogger<TrackerWatchdogWorker> _log;
    private DateTimeOffset _lastLaunchAttempt = DateTimeOffset.MinValue;

    private static string TrackerMutexName => $@"Global\{ServiceIdentity.AppMutex}";

    public TrackerWatchdogWorker(
        DlpConfig cfg,
        ILogger<TrackerWatchdogWorker> log)
    {
        _cfg = cfg;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Linux user-unit Restart=always owns tracker reopen (L2 handoff).
            _log.LogInformation("Tracker watchdog parked (Windows-only; Linux uses user systemd)");
            return;
        }

        if (!_cfg.WatchdogEnabled)
        {
            _log.LogInformation("Tracker watchdog parked (ALPHA_DLP_WATCHDOG=false)");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var poll = TimeSpan.FromSeconds(Math.Clamp(_cfg.WatchdogPollSec, 3, 60));
        var exeName = ServiceIdentity.ExecutableName;
        _log.LogInformation(
            "Tracker watchdog active (process={Name}, poll={Poll}s)",
            exeName, poll.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Watchdog tick failed");
            }

            try { await Task.Delay(poll, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (File.Exists(TeardownGate.PathIn(_cfg.DataDir)))
            return;

        // Stale update_in_progress (failed helper) must not park relaunch forever.
        if (UpdatePaths.IsUpdateInProgressBlocking())
            return;

        if (!DeviceCredentialStore.TryLoad(out _, out var tok) || string.IsNullOrWhiteSpace(tok))
            return;

        var path = ResolveClientExe();
        if (path is null)
        {
            _log.LogDebug("Watchdog: client.exe not found under Program Files");
            return;
        }

        // Celebration UI removed — never launch --celebrate-update; clear leftover flag.
        UpdatePaths.ClearCelebrate();

        if (UserSessionProcessLauncher.IsNamedMutexPresent(TrackerMutexName))
            return;

        if (!CooldownOk(ref _lastLaunchAttempt))
            return;

        _log.LogWarning("Watchdog: tracker mutex free — relaunching --background {Path}", path);
        UserSessionProcessLauncher.TryLaunch(path, "--background", _log);
    }

    private static bool CooldownOk(ref DateTimeOffset last)
    {
        if (DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(15))
            return false;
        last = DateTimeOffset.UtcNow;
        return true;
    }

    private static string? ResolveClientExe()
    {
        var name = ServiceIdentity.ExecutableName + ".exe";
        var folder = ServiceIdentity.DisplayName;
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                folder, name),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                folder, name),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside)) return beside;

        return null;
    }
}
