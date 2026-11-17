using client.Configuration;
using client.Core.Models;
using Microsoft.Extensions.Logging;

namespace client.Services.FaceVerify;

/// <summary>
/// Tracker-owned network warm-up: models, embeddings, enrollment images, and verify-event drain.
/// The --face-verify modal stays local-only on the open path.
/// </summary>
public sealed class FaceWarmupService
{
    private readonly AppConfig _config;
    private readonly ILogger? _logger;
    private int _running;

    public FaceWarmupService(AppConfig config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    public void TryStartBackground(EmployeeInfo info)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!_config.FaceVerifyEnabled) return;
        // Soft-lock owns CPU/GPU — do not compete with verify for model downloads.
        if (client.Core.FaceVerifyYield.ShouldYield) return;
        if (string.IsNullOrWhiteSpace(_config.ServerUrl)) return;
        var token = !string.IsNullOrWhiteSpace(info.DeviceToken) ? info.DeviceToken! : info.Token;
        if (string.IsNullOrWhiteSpace(token)) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        var config = _config;
        var logger = _logger;
        _ = Task.Run(async () =>
        {
            try
            {
                if (client.Core.FaceVerifyYield.ShouldYield) return;
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                await RunWarmupAsync(config, info, token!, logger, cts.Token);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Face warm-up failed");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    /// <summary>
    /// Synchronous force-refresh used before soft-lock spawn when local templates are missing/stale.
    /// </summary>
    public static async Task<bool> ForceRefreshEmbeddingsAsync(
        AppConfig config,
        EmployeeInfo info,
        ILogger? logger,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrWhiteSpace(config.ServerUrl)) return false;
        var token = !string.IsNullOrWhiteSpace(info.DeviceToken) ? info.DeviceToken! : info.Token;
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            using var store = new FaceBiometricStore();
            await store.InitializeAsync(ct);
            using var api = new FaceApiClient(config.ServerUrl!.TrimEnd('/'), token!);
            var session = new FaceVerifySession
            {
                EmployeeId = info.EmployeeId,
                EmployeeName = info.Name,
                DeviceToken = token!,
                ApiBase = config.ServerUrl!.TrimEnd('/'),
                MachineId = config.ClientId,
                MatchThreshold = config.FaceMatchThreshold,
                RecModelUrl = config.FaceBuffaloRecUrl,
            };
            try
            {
                var downloader = new FaceModelDownloader();
                await FaceModelDownloadUiHost.RunWithUiAsync(
                    progress => downloader.EnsureModelsAsync(session, api, progress, ct), ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Face model force-refresh failed");
            }

            return await SyncEmbeddingsAsync(store, api, info, config, logger, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Face embedding force-refresh failed");
            return false;
        }
    }

    public static async Task RunWarmupAsync(
        AppConfig config,
        EmployeeInfo info,
        string deviceToken,
        ILogger? logger,
        CancellationToken ct)
    {
        using var store = new FaceBiometricStore();
        await store.InitializeAsync(ct);
        using var api = new FaceApiClient(config.ServerUrl!.TrimEnd('/'), deviceToken);

        var session = new FaceVerifySession
        {
            EmployeeId = info.EmployeeId,
            EmployeeName = info.Name,
            DeviceToken = deviceToken,
            ApiBase = config.ServerUrl!.TrimEnd('/'),
            MachineId = config.ClientId,
            MatchThreshold = config.FaceMatchThreshold,
            RecModelUrl = config.FaceBuffaloRecUrl,
        };

        try
        {
            var downloader = new FaceModelDownloader();
            await FaceModelDownloadUiHost.RunWithUiAsync(
                progress => downloader.EnsureModelsAsync(session, api, progress, ct), ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Face model warm-up failed");
        }

        await SyncEmbeddingsAsync(store, api, info, config, logger, ct);

        try
        {
            await SyncSampleImagesAsync(store, api, info.EmployeeId, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Face sample image warm-up failed");
        }

        try
        {
            await DrainVerifyEventsAsync(store, api, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Face verify-event drain failed");
        }

        // Keep draining simple-mode events + honor soft-locker embedding requests.
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 240 && !ct.IsCancellationRequested; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    using var s2 = new FaceBiometricStore();
                    await s2.InitializeAsync(ct);
                    using var api2 = new FaceApiClient(config.ServerUrl!.TrimEnd('/'), deviceToken);

                    if (FaceEmbeddingRequest.TryConsume(out var reqEmp, out var reason))
                    {
                        logger?.LogInformation(
                            "Face embedding re-fetch requested ({Reason}) for {EmployeeId}",
                            reason, reqEmp);
                        var emp = string.IsNullOrWhiteSpace(reqEmp) ? info.EmployeeId : reqEmp;
                        var info2 = new EmployeeInfo
                        {
                            EmployeeId = emp,
                            Name = info.Name,
                            DeviceToken = deviceToken,
                            Token = deviceToken,
                        };
                        await SyncEmbeddingsAsync(s2, api2, info2, config, logger, ct);
                    }

                    await DrainVerifyEventsAsync(s2, api2, ct);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Face verify-event periodic drain ended");
            }
        }, ct);
    }

    public static async Task<bool> SyncEmbeddingsAsync(
        FaceBiometricStore store,
        FaceApiClient api,
        EmployeeInfo info,
        AppConfig config,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            // Drop AdaFace / corrupt local rows when model_id is wrong so soft-lock cannot
            // compare against incompatible vectors.
            var localProfile = await store.GetProfileAsync(info.EmployeeId, ct);
            if (localProfile != null
                && !string.IsNullOrWhiteSpace(localProfile.ModelId)
                && !string.Equals(localProfile.ModelId, FaceModelIds.BuffaloL, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogInformation(
                    "Clearing stale face templates model_id={ModelId} → {Expected}",
                    localProfile.ModelId, FaceModelIds.BuffaloL);
                await store.ClearEmployeeAsync(info.EmployeeId, ct);
                localProfile = null;
            }

            var emb = await api.GetEmbeddingsAsync(ct);
            if (emb.Profile == null || emb.Embeddings is not { Count: > 0 })
            {
                // Admin purged face data on the server — drop local biometric.db so soft-lock
                // cannot keep matching stale templates offline.
                var hadLocal = localProfile != null
                               || (await store.GetTemplatesAsync(info.EmployeeId, ct)).Count > 0;
                if (hadLocal)
                {
                    logger?.LogInformation(
                        "Clearing local face templates — server has no active embeddings for {EmployeeId}",
                        info.EmployeeId);
                    await store.ClearEmployeeAsync(info.EmployeeId, ct);
                }
                else
                {
                    logger?.LogDebug("No face embeddings on server for {EmployeeId}", info.EmployeeId);
                }
                return false;
            }

            // Ignore server templates that are still AdaFace until reprocessed.
            var templates = emb.Embeddings
                .Where(e => e.Vector is { Length: > 0 })
                .Where(e =>
                    string.IsNullOrWhiteSpace(e.ModelId)
                    || string.Equals(e.ModelId, FaceModelIds.BuffaloL, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(emb.Profile.ModelId, FaceModelIds.BuffaloL, StringComparison.OrdinalIgnoreCase))
                .Select(e => new LocalFaceTemplate
                {
                    Id = e.Id,
                    EmployeeId = string.IsNullOrWhiteSpace(e.EmployeeId) ? info.EmployeeId : e.EmployeeId,
                    ProfileId = e.ProfileId,
                    SampleId = e.SampleId,
                    PoseId = e.PoseId,
                    ModelId = string.IsNullOrWhiteSpace(e.ModelId) ? emb.Profile.ModelId : e.ModelId,
                    ModelVersion = e.ModelVersion,
                    PreprocessVersion = e.PreprocessVersion,
                    EmbeddingDims = e.EmbeddingDims,
                    QualityScore = e.QualityScore,
                    Vector = e.Vector!,
                })
                .Where(t => string.Equals(t.ModelId, FaceModelIds.BuffaloL, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(emb.Profile.ModelId, FaceModelIds.BuffaloL, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (templates.Count == 0)
            {
                logger?.LogWarning(
                    "Server face profile model_id={ModelId} is not buffalo_l — clearing local and waiting for re-enroll",
                    emb.Profile.ModelId);
                await store.ClearEmployeeAsync(info.EmployeeId, ct);
                return false;
            }

            var localTemplates = await store.GetTemplatesAsync(info.EmployeeId, ct);
            if (EmbeddingsMatchServer(localProfile, localTemplates, emb.Profile, templates))
            {
                logger?.LogDebug(
                    "Face embeddings already match server for {EmployeeId} (profile={ProfileId} v{Version} count={Count})",
                    info.EmployeeId, emb.Profile.Id, emb.Profile.ProfileVersion, templates.Count);
                await store.MarkSyncAckAsync(info.EmployeeId, emb.Profile.ProfileVersion, emb.Profile.ModelVersion, ct);
                try
                {
                    await api.SyncAckAsync(emb.Profile.ProfileVersion, emb.Profile.ModelVersion, config.ClientId, ct);
                }
                catch { /* best-effort */ }
                return true;
            }

            logger?.LogInformation(
                "Updating local face embeddings from server for {EmployeeId}: local(v{LocalVer},n={LocalN}) → server(v{ServerVer},n={ServerN},profile={ProfileId})",
                info.EmployeeId,
                localProfile?.ProfileVersion ?? 0,
                localTemplates.Count,
                emb.Profile.ProfileVersion,
                templates.Count,
                emb.Profile.Id);

            var nextProfile = new LocalFaceProfile
            {
                EmployeeId = info.EmployeeId,
                ProfileId = emb.Profile.Id,
                Status = emb.Profile.Status,
                ProfileVersion = emb.Profile.ProfileVersion,
                EnrollmentVersion = emb.Profile.EnrollmentVersion,
                ModelId = emb.Profile.ModelId,
                ModelVersion = emb.Profile.ModelVersion,
                PreprocessVersion = emb.Profile.PreprocessVersion,
                EmbeddingDims = emb.Profile.EmbeddingDims,
                EmbeddingCount = templates.Count,
            };
            await store.ReplaceProfileAsync(nextProfile, templates, ct);
            await store.MarkSyncAckAsync(info.EmployeeId, nextProfile.ProfileVersion, nextProfile.ModelVersion, ct);
            try
            {
                await api.SyncAckAsync(nextProfile.ProfileVersion, nextProfile.ModelVersion, config.ClientId, ct);
            }
            catch { /* best-effort */ }
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Face embedding warm-up failed");
            return false;
        }
    }

    /// <summary>
    /// True when local biometric cache matches the active server profile + template ids.
    /// </summary>
    internal static bool EmbeddingsMatchServer(
        LocalFaceProfile? localProfile,
        IReadOnlyList<LocalFaceTemplate> localTemplates,
        FaceProfileDto serverProfile,
        IReadOnlyList<LocalFaceTemplate> serverTemplates)
    {
        if (localProfile == null) return false;
        if (!string.Equals(localProfile.ProfileId, serverProfile.Id, StringComparison.OrdinalIgnoreCase))
            return false;
        if (localProfile.ProfileVersion != serverProfile.ProfileVersion) return false;
        if (localProfile.EnrollmentVersion != serverProfile.EnrollmentVersion) return false;
        if (!string.Equals(localProfile.ModelId, serverProfile.ModelId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(localProfile.ModelVersion, serverProfile.ModelVersion, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(localProfile.PreprocessVersion, serverProfile.PreprocessVersion, StringComparison.OrdinalIgnoreCase))
            return false;
        if (localTemplates.Count != serverTemplates.Count) return false;

        var localIds = localTemplates.Select(t => t.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        var serverIds = serverTemplates.Select(t => t.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var i = 0; i < localIds.Length; i++)
        {
            if (!string.Equals(localIds[i], serverIds[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public static async Task SyncSampleImagesAsync(
        FaceBiometricStore store,
        FaceApiClient api,
        string employeeId,
        CancellationToken ct)
    {
        var pack = await api.GetMySamplesAsync(ct);
        var samples = pack.Samples ?? new();
        var existing = (await store.GetSamplesAsync(employeeId, ct))
            .ToDictionary(s => s.SampleId, StringComparer.OrdinalIgnoreCase);
        var serverIds = new HashSet<string>(
            samples.Select(s => s.SampleId).Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);

        // Drop local sample rows/files that no longer exist on the server (admin purge / re-enroll).
        foreach (var local in existing.Values)
        {
            if (serverIds.Contains(local.SampleId)) continue;
            try
            {
                if (!string.IsNullOrWhiteSpace(local.LocalPath) && File.Exists(local.LocalPath))
                    File.Delete(local.LocalPath);
            }
            catch { /* best-effort */ }
            await store.DeleteSampleAsync(local.SampleId, ct);
        }

        foreach (var item in samples)
        {
            if (string.IsNullOrWhiteSpace(item.SampleId)) continue;
            var path = FacePaths.SampleImagePath(item.SampleId);
            if (existing.TryGetValue(item.SampleId, out var local)
                && string.Equals(local.ContentHash, item.ContentHash, StringComparison.OrdinalIgnoreCase)
                && File.Exists(path))
            {
                continue;
            }

            await api.DownloadSampleImageAsync(item.SampleId, path, ct);
            await store.UpsertSampleAsync(new LocalFaceSample
            {
                SampleId = item.SampleId,
                EmployeeId = employeeId,
                PoseId = item.PoseId ?? "",
                ContentHash = item.ContentHash ?? "",
                ByteSize = item.ByteSize,
                LocalPath = path,
            }, ct);
        }
    }

    public static async Task DrainVerifyEventsAsync(
        FaceBiometricStore store,
        FaceApiClient api,
        CancellationToken ct)
    {
        while (true)
        {
            var batch = await store.ListUnsyncedEventsAsync(50, ct);
            if (batch.Count == 0) break;
            var resp = await api.SyncVerifyEventsAsync(batch, ct);
            var accepted = resp.AcceptedIds ?? new List<string>();
            if (accepted.Count == 0) break;
            await store.MarkEventsSyncedAsync(accepted, ct);
        }
    }
}
