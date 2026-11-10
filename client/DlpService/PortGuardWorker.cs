using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Fast port/device guard. <see cref="EnforceWorker"/> does the full reconcile every ~15 s
/// (registry class denies, PowerShell fallback, allow list). This worker only re-checks the
/// already-published block set every <see cref="Interval"/> using native CfgMgr32 calls
/// (no process spawns), so a device the employee re-enables in Device Manager or Network
/// Connections is disabled again within ~2 s instead of after the next full pass.
///
/// Every re-block is audited and raised as a <c>device_control</c> alert (action
/// <c>reblocked</c>), rate-limited per device so a fight with the user cannot flood the inbox.
/// </summary>
public sealed class PortGuardWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AlertEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(30);

    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly ILogger<PortGuardWorker> _logger;
    private readonly Dictionary<string, DateTimeOffset> _lastAlert = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastWarn = new(StringComparer.OrdinalIgnoreCase);

    public PortGuardWorker(DlpConfig cfg, PolicyStore store, ILogger<PortGuardWorker> logger)
    {
        _cfg = cfg;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() || !_cfg.Enabled)
            return;

        await _store.InitializeAsync(stoppingToken);
        _logger.LogInformation("PortGuardWorker running (every {Sec}s, native CfgMgr32)", Interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_cfg.PortBlock && PortDeviceEnforcer.HasGuardTargets)
                {
                    var hits = await Task.Run(PortDeviceEnforcer.GuardTick, stoppingToken);
                    if (hits.Count > 0)
                        await HandleHitsAsync(hits, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PortGuardWorker tick failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task HandleHitsAsync(IReadOnlyList<PortDeviceEnforcer.GuardHit> hits, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        long? version = null;

        foreach (var hit in hits)
        {
            if (hit.Outcome == "changed")
            {
                _logger.LogWarning("Port guard: blocked device was re-enabled — disabled again ({Id})", hit.InstanceId);
                await _store.AuditAsync("port_guard_reblock", $"instance={hit.InstanceId}", ct);

                if (_lastAlert.TryGetValue(hit.InstanceId, out var last) && now - last < AlertEvery)
                    continue;
                _lastAlert[hit.InstanceId] = now;

                version ??= (await _store.GetAppliedAsync(ct)).Version;
                await _store.EnqueueAlertAsync(
                    Guid.NewGuid().ToString(), null, version.Value, "device_control", "high", "reblocked",
                    now,
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["instances"] = hit.InstanceId,
                        ["detail"] = "Blocked device was re-enabled on the PC and disabled again by the port guard",
                    }),
                    ct);
            }
            else
            {
                // vetoed / failed: Windows refused. Retry silently; warn at most every 30 min.
                if (_lastWarn.TryGetValue(hit.InstanceId, out var last) && now - last < WarnEvery)
                    continue;
                _lastWarn[hit.InstanceId] = now;
                _logger.LogWarning("Port guard could not disable {Id} ({Outcome}) — will keep retrying",
                    hit.InstanceId, hit.Outcome);
                await _store.AuditAsync("port_guard_failed", $"instance={hit.InstanceId} outcome={hit.Outcome}", ct);
            }
        }
    }
}
