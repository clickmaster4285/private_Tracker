using client.Configuration;
using client.Core;
using Microsoft.Data.Sqlite;
using OpenCvSharp;

namespace client.Services.FaceVerify;

/// <summary>
/// Avalonia-free verification controller for the WinForms soft-locker.
/// Local-only for camera match; downloads ONNX models only when missing (with ETA).
/// First-mode never enqueues verify events.
/// </summary>
public sealed class FaceVerifyController : IDisposable
{
    public const int FirstPoseSkipAfterFails = 10;

    private readonly CancellationTokenSource _cts = new();
    private FaceBiometricStore? _store;
    private FaceRecognitionEngine? _engine;
    private FaceVerifySession? _session;
    private string _mode = "simple";
    private IReadOnlyList<string> _poseQueue = Array.Empty<string>();
    private int _poseIndex;
    private int _poseFailStreak;
    private bool _frontVerifiedThisRun;
    private double _effectiveThreshold = 0.8;
    private IReadOnlyList<LocalFaceSample> _samples = Array.Empty<LocalFaceSample>();
    private bool _allowClose;
    private bool _running;

    public string StatusTitle { get; private set; } = "Face verification";
    public string StatusDetail { get; private set; } = "Preparing…";
    public string ReferenceImagePath { get; private set; } = "";
    public bool AllowClose => _allowClose;
    public string Mode => _mode;
    public double EffectiveThreshold => _effectiveThreshold;

    /// <summary>True while ONNX packages are downloading.</summary>
    public bool ShowDownloadProgress { get; private set; }
    public double DownloadFraction { get; private set; }
    public string DownloadMeta { get; private set; } = "";
    public bool DownloadIndeterminate { get; private set; } = true;

    public event Action? Changed;
    public event Action? Completed;
    /// <summary>Match-cycle result (status / streak). Preview UI should use <see cref="PreviewFrame"/>.</summary>
    public event Action<FaceMatchResult>? FrameProcessed;
    /// <summary>Smooth camera frames (~15 fps). Subscriber owns and must Dispose the Mat.</summary>
    public event Action<Mat>? PreviewFrame;

    public async Task StartAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            StatusTitle = "alpha-ai-tracker-face-verification";
            StatusDetail = "Loading local face data…";
            RaiseChanged();

            _session = FaceVerifySession.TryRead();
            if (_session == null || string.IsNullOrWhiteSpace(_session.EmployeeId))
            {
                StatusTitle = "Cannot verify";
                StatusDetail = "Missing login session. Sign in again.";
                RaiseChanged();
                return;
            }

            _store = new FaceBiometricStore();
            await _store.InitializeAsync(_cts.Token);

            if (!File.Exists(FacePaths.DetPath) || !File.Exists(FacePaths.RecPath)
                || new FileInfo(FacePaths.DetPath).Length < 100_000
                || new FileInfo(FacePaths.RecPath).Length <= 1_000_000)
            {
                if (!await EnsureModelsDownloadedAsync(_session, _cts.Token))
                    return;
            }

            var templates = (await _store.GetTemplatesAsync(_session.EmployeeId, _cts.Token)).ToList();
            _samples = await _store.GetSamplesAsync(_session.EmployeeId, _cts.Token);
            var profile = await _store.GetProfileAsync(_session.EmployeeId, _cts.Token);
            var modelStale = profile != null
                && !string.IsNullOrWhiteSpace(profile.ModelId)
                && !string.Equals(profile.ModelId, FaceModelIds.BuffaloL, StringComparison.OrdinalIgnoreCase);
            if (templates.Count == 0 || modelStale)
            {
                // Zero-network locker: ask tracker to re-fetch embeddings, then reload.
                StatusTitle = "Syncing face data…";
                StatusDetail = modelStale
                    ? "Local templates are for an old model — waiting for buffalo_l sync."
                    : "Requesting embeddings from the tracker…";
                RaiseChanged();
                FaceEmbeddingRequest.Write(_session.EmployeeId, reason: modelStale ? "stale_model" : "missing");
                templates = await WaitForTemplatesAsync(_session.EmployeeId, _cts.Token);
                _samples = await _store.GetSamplesAsync(_session.EmployeeId, _cts.Token);
                if (templates.Count == 0)
                {
                    StatusTitle = "No local face data";
                    StatusDetail = "Meet HR to enroll, then keep this PC online once to sync.";
                    RaiseChanged();
                    return;
                }
            }

            var firstDone = await _store.IsFirstVerifyCompleteAsync(_session.EmployeeId, _cts.Token);
            if (!firstDone)
            {
                _mode = "first";
                _poseQueue = FacePoseCatalog.OrderedPoseIds(templates.Select(t => t.PoseId));
                if (_poseQueue.Count == 0)
                    _poseQueue = new[] { "front" };
                _poseIndex = 0;
                StatusTitle = "First-time face check (local only)";
                StatusDetail = $"Step 1 of {_poseQueue.Count}: {FacePoseCatalog.Label(_poseQueue[0])}";
            }
            else
            {
                _mode = "simple";
                _poseQueue = Array.Empty<string>();
                StatusTitle = "Look at the camera";
                StatusDetail = "Sit 50–70 cm away — keep your face centered.";
            }

            // Same floor for first-local and simple verify (no first-mode +0.05 bump).
            _effectiveThreshold = Math.Max(0.8, _session.MatchThreshold);

            StatusDetail += $" (threshold {_effectiveThreshold:0.00})";
            UpdateReferencePath();
            RaiseChanged();

            StatusDetail = "Loading camera and recognition models…";
            RaiseChanged();

            await EnsureOrtLoadableAsync(_cts.Token);

            _engine = new FaceRecognitionEngine();
            await Task.Run(() => _engine.Initialize(templates, _effectiveThreshold), _cts.Token);

            StatusDetail = _mode == "first"
                ? $"Step {_poseIndex + 1} of {_poseQueue.Count}: {FacePoseCatalog.Label(CurrentPoseId() ?? "front")}"
                : "Keep looking at the camera.";
            RaiseChanged();

            await RunPreviewAndMatchAsync(_cts.Token);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            StatusTitle = "Face check error";
            StatusDetail = FormatOrtLoadError(ex);
            RaiseChanged();
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// Smooth webcam UI (~15 fps) independent of ORT match cadence (~2/sec by default).
    /// </summary>
    private async Task RunPreviewAndMatchAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var previewTask = PreviewLoopAsync(linked.Token);
        try
        {
            await MatchLoopAsync(linked.Token);
        }
        finally
        {
            linked.Cancel();
            try { await previewTask; }
            catch (OperationCanceledException) { /* expected */ }
            catch { /* ignore preview teardown */ }
        }
    }

    private async Task PreviewLoopAsync(CancellationToken ct)
    {
        var delayMs = ResolvePreviewDelayMs();
        while (!ct.IsCancellationRequested && !_allowClose)
        {
            try
            {
                if (_engine != null && _engine.TryGrabPreview(out var mat) && mat != null)
                {
                    var handlers = PreviewFrame;
                    if (handlers == null)
                    {
                        mat.Dispose();
                    }
                    else
                    {
                        try { handlers.Invoke(mat); }
                        catch { mat.Dispose(); }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* drop bad frames */ }

            try { await Task.Delay(delayMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Default 15 fps preview. <c>ALPHA_FACE_PREVIEW_FPS</c> (5–30).</summary>
    internal static int ResolvePreviewDelayMs()
    {
        var fps = 15;
        try
        {
            var env = Environment.GetEnvironmentVariable("ALPHA_FACE_PREVIEW_FPS");
            if (int.TryParse(env, out var n) && n > 0)
                fps = Math.Clamp(n, 5, 30);
        }
        catch { /* defaults */ }
        return Math.Max(33, 1000 / fps);
    }

    /// <summary>
    /// Probe ORT before touching its types (a failed <c>NativeMethods</c> type init is cached
    /// for the process). When antivirus is the likely cause, ask the LocalSystem DLP service
    /// to re-apply product exclusions and restore our quarantined files, then probe again.
    /// </summary>
    private async Task EnsureOrtLoadableAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;

        var probe = OrtNativeBootstrap.ProbeWindowsLoadError(out var avSuspected);
        if (probe is null || !avSuspected) return;

        var title = StatusTitle;
        var detail = StatusDetail;
        StatusTitle = "Repairing antivirus access…";
        StatusDetail = "Antivirus blocked the face engine. Restoring the tracker's own files…";
        RaiseChanged();

        for (var attempt = 0; attempt < 2 && probe is not null; attempt++)
        {
            await DlpPipeClient.SendAsync(
                new { op = "EnsureAvSelfProtection" },
                connectTimeoutMs: 5_000,
                responseTimeoutMs: 120_000,
                ct: ct);

            // Defender restores asynchronously; give the file a moment to reappear.
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            OrtNativeBootstrap.ResetForRetry();
            probe = OrtNativeBootstrap.ProbeWindowsLoadError(out _);
        }

        if (probe is not null)
            throw new InvalidOperationException(probe);

        StatusTitle = title;
        StatusDetail = detail;
        RaiseChanged();
    }

    private static string FormatOrtLoadError(Exception ex)
    {
        var probe = OrtNativeBootstrap.ProbeWindowsLoadError();
        if (!string.IsNullOrWhiteSpace(probe))
            return probe!;

        if (ex is TypeInitializationException tie)
        {
            var inner = tie.InnerException;
            while (inner != null)
            {
                if (inner is DllNotFoundException or BadImageFormatException)
                    return inner.Message;
                if (!string.IsNullOrWhiteSpace(inner.Message))
                    return inner.Message;
                inner = inner.InnerException;
            }
        }

        return ex.InnerException?.Message ?? ex.Message;
    }

    private async Task MatchLoopAsync(CancellationToken ct)
    {
        if (_engine == null || _session == null || _store == null) return;
        _poseFailStreak = 0;
        _frontVerifiedThisRun = false;

        // Let the preview loop stash at least one frame before first ORT pass.
        await Task.Delay(120, ct);

        while (!ct.IsCancellationRequested)
        {
            var poseFilter = _mode == "first" && _poseIndex < _poseQueue.Count
                ? _poseQueue[_poseIndex]
                : null;
            var result = await Task.Run(() => _engine.CompareLatest(poseFilter), ct);
            FrameProcessed?.Invoke(result);

            if (result.Matched)
            {
                var poseId = CurrentPoseId();
                if (IsFrontPose(poseId) || _mode == "simple")
                    _frontVerifiedThisRun = true;
                await RecordLocalAsync(result, "verified", poseId, ct);
                _engine.ResetMatchStreak();
                _poseFailStreak = 0;

                if (_mode == "first")
                {
                    if (await AdvanceFirstOrFinishAsync(ct))
                        return;
                    continue;
                }

                StatusTitle = "Verified";
                StatusDetail = $"Welcome. Score {result.Score:0.000}";
                _allowClose = true;
                FaceVerifySuccessGate.MarkSuccess(_session.EmployeeId);
                RaiseChanged();
                Completed?.Invoke();
                return;
            }

            if (result.Detail is "no_face" or "multiple_faces" or "low_quality" or "pad_static" or "failed"
                or "align_failed" or "no_templates" or "too_far" or "too_close"
                || result.Detail.StartsWith("detect_error", StringComparison.Ordinal))
            {
                if (result.Detail.StartsWith("streak_", StringComparison.Ordinal)
                    || result.Detail == "spacing")
                {
                    StatusDetail = $"Confirming… {result.MatchStreak}/3  score {result.Score:0.000}";
                }
                else
                {
                    StatusDetail = result.Detail switch
                    {
                        "no_face" => "No face detected — center your face.",
                        "multiple_faces" => "Only one person in frame.",
                        "pad_static" => "Move slightly — static image rejected.",
                        "low_quality" => "Move closer / improve lighting.",
                        "too_far" => "Too far — sit 50–70 cm from the camera.",
                        "too_close" => "Too close — sit 50–70 cm from the camera.",
                        _ =>
                            $"Not matched (score {result.Score:0.000}). {(_mode == "first" ? FacePoseCatalog.Label(CurrentPoseId() ?? "front") : "Keep looking.")}",
                    };
                }

                RaiseChanged();

                if (result.Detail is "failed" or "no_face" or "align_failed" or "no_templates"
                    or "pad_static" or "multiple_faces" or "low_quality" or "too_far" or "too_close")
                {
                    _poseFailStreak++;
                    await RecordLocalAsync(result, result.Detail, CurrentPoseId(), ct);

                    if (_mode == "first"
                        && _frontVerifiedThisRun
                        && _poseFailStreak >= FirstPoseSkipAfterFails
                        && !IsFrontPose(CurrentPoseId()))
                    {
                        StatusDetail = $"Skipping difficult angle ({FacePoseCatalog.Label(CurrentPoseId() ?? "")}).";
                        RaiseChanged();
                        if (await AdvanceFirstOrFinishAsync(ct))
                            return;
                        continue;
                    }
                }
            }
            else if (result.Detail.StartsWith("streak_", StringComparison.Ordinal)
                     || result.Detail == "spacing")
            {
                StatusDetail = $"Confirming… {result.MatchStreak}/3  score {result.Score:0.000}";
                RaiseChanged();
            }

            await Task.Delay(ResolveLoopDelayMs(), ct);
        }
    }

    /// <summary>
    /// Pause between camera match attempts. Default 500 ms (~2/sec) so weak
    /// CPUs are not pinned; was 250 ms (~4/sec). <c>ALPHA_FACE_VERIFY_LOOP_MS</c>.
    /// </summary>
    internal static int ResolveLoopDelayMs()
    {
        const int fallback = 500;
        try
        {
            var env = Environment.GetEnvironmentVariable("ALPHA_FACE_VERIFY_LOOP_MS");
            if (int.TryParse(env, out var ms) && ms > 0)
                return Math.Clamp(ms, 250, 2000);
        }
        catch { /* defaults */ }
        return fallback;
    }

    private async Task<bool> AdvanceFirstOrFinishAsync(CancellationToken ct)
    {
        if (_session == null || _store == null) return false;
        _poseIndex++;
        _poseFailStreak = 0;
        _engine?.ResetMatchStreak();
        if (_poseIndex < _poseQueue.Count)
        {
            StatusTitle = "First-time face check (local only)";
            StatusDetail =
                $"Step {_poseIndex + 1} of {_poseQueue.Count}: {FacePoseCatalog.Label(_poseQueue[_poseIndex])}";
            UpdateReferencePath();
            RaiseChanged();
            await Task.Delay(500, ct);
            return false;
        }

        if (!_frontVerifiedThisRun)
        {
            StatusTitle = "Front angle required";
            StatusDetail = "Could not verify front pose. Look straight at the camera and retry.";
            RaiseChanged();
            return true;
        }

        await _store.MarkFirstVerifyCompleteAsync(_session.EmployeeId, ct);
        StatusTitle = "Verified";
        StatusDetail = "First local test saved on this PC only.";
        _allowClose = true;
        FaceVerifySuccessGate.MarkSuccess(_session.EmployeeId);
        RaiseChanged();
        Completed?.Invoke();
        return true;
    }

    private async Task RecordLocalAsync(
        FaceMatchResult result,
        string resultCode,
        string? poseId,
        CancellationToken ct)
    {
        if (_store == null || _session == null) return;
        try
        {
            await _store.MarkVerifyAsync(
                _session.EmployeeId,
                resultCode,
                failed: resultCode is not "verified",
                ct);
        }
        catch { /* local only */ }

        // First-mode: never enqueue for server sync.
        if (_mode == "first")
            return;

        try
        {
            await _store.EnqueueVerifyEventAsync(new LocalFaceVerifyEvent
            {
                Id = Guid.NewGuid().ToString(),
                EmployeeId = _session.EmployeeId,
                MachineId = _session.MachineId,
                Mode = _mode,
                Trigger = string.IsNullOrWhiteSpace(_session.Trigger) ? "boot" : _session.Trigger,
                PoseId = poseId ?? "",
                Result = resultCode == "verified" ? "verified" : "failed",
                Score = result.Score,
                Threshold = _effectiveThreshold,
                Detail = result.Detail,
                CreatedAt = DateTime.UtcNow.ToString("O"),
            }, ct);
        }
        catch { /* local queue */ }
    }

    private string? CurrentPoseId() =>
        _mode == "first" && _poseIndex < _poseQueue.Count ? _poseQueue[_poseIndex] : null;

    private void UpdateReferencePath()
    {
        ReferenceImagePath = "";
        var pose = CurrentPoseId();
        if (string.IsNullOrWhiteSpace(pose)) return;
        var sample = _samples.FirstOrDefault(s =>
            string.Equals(s.PoseId, pose, StringComparison.OrdinalIgnoreCase)
            && File.Exists(s.LocalPath));
        if (sample != null)
            ReferenceImagePath = sample.LocalPath;
    }

    private static bool IsFrontPose(string? poseId) =>
        string.Equals(poseId, "front", StringComparison.OrdinalIgnoreCase)
        || string.Equals(poseId, "front_return", StringComparison.OrdinalIgnoreCase);

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>
    /// Soft-lock path: download missing ONNX packages with progress + ETA, then continue verify.
    /// Credentials come from tracker SQLite + AppConfig (session deliberately omits the token).
    /// </summary>
    private async Task<bool> EnsureModelsDownloadedAsync(FaceVerifySession session, CancellationToken ct)
    {
        StatusTitle = "Downloading face models";
        StatusDetail = "Preparing download…";
        ShowDownloadProgress = true;
        DownloadIndeterminate = true;
        DownloadFraction = 0;
        DownloadMeta = "";
        RaiseChanged();

        if (!TryResolveDownloadCreds(session, out var apiBase, out var token, out var recUrl))
        {
            StatusTitle = "Models not ready";
            StatusDetail = "Keep the tracker online once so models sync, then retry.";
            ShowDownloadProgress = false;
            RaiseChanged();
            return false;
        }

        try
        {
            using var api = new FaceApiClient(apiBase, token);
            var downloadSession = new FaceVerifySession
            {
                EmployeeId = session.EmployeeId,
                EmployeeName = session.EmployeeName,
                DeviceToken = token,
                ApiBase = apiBase,
                MachineId = session.MachineId,
                MatchThreshold = session.MatchThreshold,
                RecModelUrl = recUrl,
            };
            var progress = new Progress<FaceDownloadProgress>(p =>
            {
                StatusDetail = p.Step;
                DownloadMeta = string.IsNullOrEmpty(p.EtaText)
                    ? p.MbLine
                    : (string.IsNullOrEmpty(p.MbLine) ? p.EtaText! : $"{p.MbLine}  ·  {p.EtaText}");
                if (p.Fraction is { } f)
                {
                    DownloadFraction = Math.Clamp(f, 0, 1);
                    DownloadIndeterminate = false;
                }
                if (p.IsFailed)
                {
                    StatusTitle = "Download failed";
                    StatusDetail = p.Error ?? p.Step;
                }
                RaiseChanged();
            });
            var downloader = new FaceModelDownloader();
            await downloader.EnsureModelsAsync(downloadSession, api, progress, ct);

            ShowDownloadProgress = false;
            DownloadFraction = 1;
            DownloadIndeterminate = false;
            StatusDetail = "Face models ready";
            RaiseChanged();
            return File.Exists(FacePaths.DetPath) && File.Exists(FacePaths.RecPath);
        }
        catch (Exception ex)
        {
            StatusTitle = "Download failed";
            StatusDetail = ex.Message;
            ShowDownloadProgress = false;
            RaiseChanged();
            return false;
        }
    }

    private static bool TryResolveDownloadCreds(
        FaceVerifySession session,
        out string apiBase,
        out string token,
        out string? recUrl)
    {
        apiBase = "";
        token = "";
        recUrl = session.RecModelUrl;

        if (!string.IsNullOrWhiteSpace(session.DeviceToken) && !string.IsNullOrWhiteSpace(session.ApiBase))
        {
            apiBase = session.ApiBase.TrimEnd('/');
            token = session.DeviceToken;
            return true;
        }

        try
        {
            var config = AppConfig.FromEnv();
            apiBase = (config.ServerUrl ?? "").TrimEnd('/');
            recUrl ??= config.FaceBuffaloRecUrl;

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = TrackerDeviceTokenReader.CandidatePaths(local, profile, config.DbPath);
            if (TrackerDeviceTokenReader.TryPickNewest(paths, out var tok, out _, out var sourcePath, out _)
                && !string.IsNullOrWhiteSpace(tok)
                && !string.IsNullOrWhiteSpace(sourcePath))
            {
                token = tok!;
                // Prefer device_token column when present.
                try
                {
                    using var conn = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "SELECT COALESCE(device_token, token) FROM employee_info " +
                        "WHERE length(COALESCE(device_token, token, '')) > 0 LIMIT 1";
                    var v = cmd.ExecuteScalar() as string;
                    if (!string.IsNullOrWhiteSpace(v)) token = v!;
                }
                catch { /* keep tok */ }
            }

            return !string.IsNullOrWhiteSpace(apiBase) && !string.IsNullOrWhiteSpace(token);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Poll local biometric.db until tracker fulfills <see cref="FaceEmbeddingRequest"/> or timeout.
    /// </summary>
    private async Task<List<LocalFaceTemplate>> WaitForTemplatesAsync(string employeeId, CancellationToken ct)
    {
        if (_store == null) return new List<LocalFaceTemplate>();
        for (var i = 0; i < 60 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(1000, ct);
            var templates = (await _store.GetTemplatesAsync(employeeId, ct)).ToList();
            var profile = await _store.GetProfileAsync(employeeId, ct);
            var modelOk = profile == null
                || string.IsNullOrWhiteSpace(profile.ModelId)
                || string.Equals(profile.ModelId, FaceModelIds.BuffaloL, StringComparison.OrdinalIgnoreCase);
            if (templates.Count > 0 && modelOk)
            {
                FaceEmbeddingRequest.TryConsume(out _, out _); // clear leftover request
                return templates.Where(t =>
                    string.IsNullOrWhiteSpace(t.ModelId)
                    || string.Equals(t.ModelId, FaceModelIds.BuffaloL, StringComparison.OrdinalIgnoreCase)
                ).ToList();
            }
            // Keep request file alive so tracker can pick it up after late login.
            if (!FaceEmbeddingRequest.IsPending())
                FaceEmbeddingRequest.Write(employeeId, reason: "missing_retry");
            StatusDetail = $"Waiting for face sync… ({i + 1}s)";
            RaiseChanged();
        }
        return (await _store.GetTemplatesAsync(employeeId, ct)).ToList();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _engine?.Dispose();
        _store?.Dispose();
    }
}

/// <summary>Supervisor-visible success marker (fail-closed if missing).</summary>
public static class FaceVerifySuccessGate
{
    public static string SuccessPath => Path.Combine(FacePaths.RootDirectory, "last-success.json");

    public static void MarkSuccess(string employeeId)
    {
        try
        {
            FacePaths.EnsureDirectories();
            var json =
                $"{{\"employeeId\":\"{employeeId}\",\"at\":\"{DateTime.UtcNow:O}\",\"ok\":true}}";
            var tmp = SuccessPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SuccessPath, overwrite: true);
        }
        catch { /* best-effort */ }
    }

    public static void Clear()
    {
        try { if (File.Exists(SuccessPath)) File.Delete(SuccessPath); } catch { /* ignore */ }
    }

    public static bool HasRecentSuccess(string employeeId, TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(SuccessPath)) return false;
            var text = File.ReadAllText(SuccessPath);
            if (!text.Contains(employeeId, StringComparison.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(SuccessPath);
            return DateTime.UtcNow - info.LastWriteTimeUtc <= maxAge;
        }
        catch
        {
            return false;
        }
    }
}
