using client.Configuration;
using client.Core.Models;
using Microsoft.Extensions.Logging;

namespace client.Services.FaceVerify;

/// <summary>
/// Tracker-side soft-lock supervisor: spawn, debounce, relaunch on crash until success gate.
/// Soft-lock only — not Windows authentication. Fail-closed (crash ≠ success).
/// </summary>
public sealed class FaceVerifySupervisor
{
    private readonly AppConfig _config;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private DateTime _lastLaunchUtc = DateTime.MinValue;
    private string? _watchingEmployeeId;
    private CancellationTokenSource? _watchCts;

    public FaceVerifySupervisor(AppConfig config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    public void RequestLaunch(EmployeeInfo info, string trigger)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!_config.FaceVerifyEnabled) return;

        lock (_gate)
        {
            // Debounce rapid unlock/lock storms.
            if ((DateTime.UtcNow - _lastLaunchUtc).TotalSeconds < 3
                && FaceVerifySession.IsVerifyProcessRunning())
                return;

            FaceVerifySuccessGate.Clear();
            _watchingEmployeeId = info.EmployeeId;
            _watchCts?.Cancel();
            _watchCts = new CancellationTokenSource();
            var emp = info.EmployeeId;
            var ct = _watchCts.Token;
            _ = Task.Run(() => LaunchAfterRefreshAsync(info, trigger, ct), ct);
        }
    }

    private async Task LaunchAfterRefreshAsync(EmployeeInfo info, string trigger, CancellationToken ct)
    {
        try
        {
            var needRefresh = await LocalTemplatesNeedRefreshAsync(info.EmployeeId, ct);
            if (needRefresh)
            {
                _logger?.LogInformation(
                    "Face soft-lock: local templates missing/stale — refreshing from server before spawn");
                using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                refreshCts.CancelAfter(TimeSpan.FromSeconds(45));
                await FaceWarmupService.ForceRefreshEmbeddingsAsync(_config, info, _logger, refreshCts.Token);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Face soft-lock pre-spawn embedding refresh failed");
        }

        if (ct.IsCancellationRequested) return;

        lock (_gate)
        {
            FaceVerifyLauncher.SpawnProcess(_config, info, trigger);
            _lastLaunchUtc = DateTime.UtcNow;
        }

        await WatchAsync(info.EmployeeId, info, ct);
    }

    private static async Task<bool> LocalTemplatesNeedRefreshAsync(string employeeId, CancellationToken ct)
    {
        try
        {
            using var store = new FaceBiometricStore();
            await store.InitializeAsync(ct);
            var templates = await store.GetTemplatesAsync(employeeId, ct);
            if (templates.Count == 0) return true;
            var profile = await store.GetProfileAsync(employeeId, ct);
            if (profile != null
                && !string.IsNullOrWhiteSpace(profile.ModelId)
                && !string.Equals(profile.ModelId, FaceModelIds.BuffaloL, StringComparison.OrdinalIgnoreCase))
                return true;
            if (templates.Any(t =>
                    !string.IsNullOrWhiteSpace(t.ModelId)
                    && !string.Equals(t.ModelId, FaceModelIds.BuffaloL, StringComparison.OrdinalIgnoreCase)))
                return true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private async Task WatchAsync(string employeeId, EmployeeInfo info, CancellationToken ct)
    {
        await Task.Delay(1500, ct);
        var relaunches = 0;
        while (!ct.IsCancellationRequested && relaunches < 8)
        {
            if (FaceVerifySuccessGate.HasRecentSuccess(employeeId, TimeSpan.FromMinutes(2)))
                return;

            // Soft-locker may request embeddings while open.
            if (FaceEmbeddingRequest.IsPending())
            {
                try
                {
                    await FaceWarmupService.ForceRefreshEmbeddingsAsync(_config, info, _logger, ct);
                    FaceEmbeddingRequest.TryConsume(out _, out _);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Face embedding request while watching failed");
                }
            }

            if (!FaceVerifySession.IsVerifyProcessRunning())
            {
                _logger?.LogWarning("Face soft-locker missing before success — relaunching");
                FaceVerifyLauncher.SpawnProcess(_config, info, trigger: "supervisor");
                relaunches++;
                _lastLaunchUtc = DateTime.UtcNow;
            }

            await Task.Delay(2000, ct);
        }
    }

    public void DisposeWatch()
    {
        try { _watchCts?.Cancel(); } catch { /* ignore */ }
        _watchCts?.Dispose();
        _watchCts = null;
    }
}
