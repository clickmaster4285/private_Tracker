namespace AlphaAITracker.Service;

/// <summary>
/// Applies the locked service DACL once after the host starts (when installed
/// as a Windows Service under LocalSystem).
/// </summary>
public sealed class ServiceAclWorker : IHostedService
{
    private readonly DlpConfig _cfg;
    private readonly ILogger<ServiceAclWorker> _logger;

    public ServiceAclWorker(DlpConfig cfg, ILogger<ServiceAclWorker> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_cfg.Enabled || !OperatingSystem.IsWindows())
            return Task.CompletedTask;

        // Clear stale teardown.ok from a previous uninstall attempt.
        TeardownGate.Clear(_cfg.DataDir);
        ServiceAclHardener.TryApplyLocked(_logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
