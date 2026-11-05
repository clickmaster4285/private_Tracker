namespace AlphaAITracker.Service;

/// <summary>In-memory status for named-pipe GetUpdateStatus / RequestUpdateCheck.</summary>
public sealed class SilentUpdateState
{
    private readonly object _gate = new();
    private int _checkRequested;

    public string Phase { get; private set; } = "idle";
    public string? LatestVersion { get; private set; }
    public string? CurrentVersion { get; private set; }
    public string? Message { get; private set; }
    public double DownloadProgress { get; private set; }
    public DateTimeOffset? LastCheckAt { get; private set; }
    public DateTimeOffset? LastErrorAt { get; private set; }
    public int ConsecutiveFailures { get; private set; }

    public void Set(string phase, string? message = null, double? progress = null)
    {
        lock (_gate)
        {
            Phase = phase;
            if (message != null) Message = message;
            if (progress.HasValue) DownloadProgress = progress.Value;
        }
    }

    public void SetVersions(string? current, string? latest)
    {
        lock (_gate)
        {
            if (current != null) CurrentVersion = current;
            if (latest != null) LatestVersion = latest;
        }
    }

    public void MarkChecked()
    {
        lock (_gate) LastCheckAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailure(string message)
    {
        lock (_gate)
        {
            ConsecutiveFailures++;
            LastErrorAt = DateTimeOffset.UtcNow;
            Message = message;
            Phase = "error";
        }
    }

    public void MarkSuccess()
    {
        lock (_gate)
        {
            ConsecutiveFailures = 0;
            Phase = "idle";
            Message = "up_to_date";
            DownloadProgress = 0;
        }
    }

    public void RequestCheck() => Interlocked.Exchange(ref _checkRequested, 1);

    public bool ConsumeCheckRequest() => Interlocked.Exchange(ref _checkRequested, 0) == 1;

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                ok = true,
                phase = Phase,
                currentVersion = CurrentVersion,
                latestVersion = LatestVersion,
                message = Message,
                downloadProgress = DownloadProgress,
                lastCheckAt = LastCheckAt?.ToString("o"),
                lastErrorAt = LastErrorAt?.ToString("o"),
                consecutiveFailures = ConsecutiveFailures,
                updateInProgress = UpdatePaths.IsUpdateInProgress(),
            };
        }
    }
}
