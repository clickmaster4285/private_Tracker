using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Fast port/device guard. <see cref="EnforceWorker"/> does the full reconcile on policy
/// change / pending / Guard-reported reblock. This worker only re-checks the already-published
/// block set using native CfgMgr32 calls (no process spawns). Adaptive interval: 2s for ~30s
/// after a reblock hit, else <c>ALPHA_DLP_PORT_GUARD_SEC</c> (default 10).
///
/// Every re-block is audited and raised as a <c>device_control</c> alert (action
/// <c>reblocked</c>), rate-limited per device so a fight with the user cannot flood the inbox.
/// </summary>
public sealed class PortGuardWorker : BackgroundService
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FastWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AlertEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(30);

    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly ILogger<PortGuardWorker> _logger;
    private readonly Dictionary<string, DateTimeOffset> _lastAlert = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastWarn = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _fastUntil = DateTimeOffset.MinValue;

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
        _logger.LogInformation(
            "PortGuardWorker running (idle every {Sec}s, fast 2s after reblock, native CfgMgr32)",
            _cfg.PortGuardSec);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _cfg.RefreshLocalSwitches();
                if (_cfg.PortBlock && PortDeviceEnforcer.HasGuardTargets)
                {
                    var hits = await Task.Run(PortDeviceEnforcer.GuardTick, stoppingToken);
                    if (hits.Count > 0)
                    {
                        _fastUntil = DateTimeOffset.UtcNow.Add(FastWindow);
                        await HandleHitsAsync(hits, stoppingToken);
                        // Wake Enforce so fingerprint-stable Apply short-circuit breaks soon.
                        if (hits.Any(h => h.Outcome is "changed"))
                            DlpSignals.RequestEnforce();
                    }
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

            try
            {
                var delay = DateTimeOffset.UtcNow < _fastUntil
                    ? FastInterval
                    : TimeSpan.FromSeconds(Math.Max(2, _cfg.PortGuardSec));
                await Task.Delay(delay, stoppingToken);
            }
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
