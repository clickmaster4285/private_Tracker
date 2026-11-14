namespace client.Services.FaceVerify;

/// <summary>
/// Avalonia-free verification controller for the WinForms soft-locker.
/// Zero network: never constructs FaceApiClient; first-mode never enqueues events.
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
    private double _effectiveThreshold = 0.85;
    private IReadOnlyList<LocalFaceSample> _samples = Array.Empty<LocalFaceSample>();
    private bool _allowClose;
    private bool _running;

    public string StatusTitle { get; private set; } = "Face verification";
    public string StatusDetail { get; private set; } = "Preparing…";
    public string ReferenceImagePath { get; private set; } = "";
    public bool AllowClose => _allowClose;
    public string Mode => _mode;
    public double EffectiveThreshold => _effectiveThreshold;

    public event Action? Changed;
    public event Action? Completed;
    public event Action<FaceMatchResult>? FrameProcessed;

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

            if (!File.Exists(FacePaths.DetPath) || !File.Exists(FacePaths.RecPath))
            {
                StatusTitle = "Models not ready";
                StatusDetail = "Keep the tracker online once so models sync, then retry.";
                RaiseChanged();
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

            var baseTh = Math.Max(0.85, _session.MatchThreshold);
            _effectiveThreshold = _mode == "first"
                ? Math.Min(0.95, baseTh + 0.05)
                : baseTh;

            StatusDetail += $" (threshold {_effectiveThreshold:0.00})";
            UpdateReferencePath();
            RaiseChanged();

            StatusDetail = "Loading camera and recognition models…";
            RaiseChanged();

            _engine = new FaceRecognitionEngine();
            await Task.Run(() => _engine.Initialize(templates, _effectiveThreshold), _cts.Token);

            StatusDetail = _mode == "first"
                ? $"Step {_poseIndex + 1} of {_poseQueue.Count}: {FacePoseCatalog.Label(CurrentPoseId() ?? "front")}"
                : "Keep looking at the camera.";
            RaiseChanged();

            await LoopAsync(_cts.Token);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            StatusTitle = "Face check error";
            StatusDetail = ex.Message;
            RaiseChanged();
        }
        finally
        {
            _running = false;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        if (_engine == null || _session == null || _store == null) return;
        _poseFailStreak = 0;
        _frontVerifiedThisRun = false;

        while (!ct.IsCancellationRequested)
        {
            var poseFilter = _mode == "first" && _poseIndex < _poseQueue.Count
                ? _poseQueue[_poseIndex]
                : null;
            var result = await Task.Run(() => _engine.CaptureAndCompare(poseFilter), ct);
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

            await Task.Delay(250, ct);
        }
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
