using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client.Core;
using client.Services.FaceVerify;
using OpenCvSharp;

namespace client.ViewModels;

public partial class FaceVerifyViewModel : ViewModelBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private FaceBiometricStore? _store;
    private FaceApiClient? _api;
    private FaceRecognitionEngine? _engine;
    private FaceVerifySession? _session;
    private bool _allowClose;
    private bool _pipelineRunning;
    private int _failReports;
    private int _poseFailStreak;
    private bool _frontVerifiedThisRun;
    private string _mode = "simple";
    private IReadOnlyList<string> _poseQueue = Array.Empty<string>();
    private int _poseIndex;
    private IReadOnlyList<LocalFaceSample> _samples = Array.Empty<LocalFaceSample>();

    /// <summary>Auto-advance first-verify after this many consecutive fails on a non-front pose.</summary>
    private const int FirstPoseSkipAfterFails = 20;

    [ObservableProperty] private string _appName = AppInfo.DisplayName;
    [ObservableProperty] private string _statusTitle = "Face verification";
    [ObservableProperty] private string _statusDetail = "Preparing…";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _showRetry;
    [ObservableProperty] private bool _waitingForHr;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private Bitmap? _referenceImage;
    [ObservableProperty] private bool _showReference;

    public bool AllowClose => _allowClose;
    public event Action? RequestClose;

    public async Task StartAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            StatusDetail = "Face verification is Windows-only.";
            return;
        }

        _session = FaceVerifySession.TryRead();
        if (_session == null || string.IsNullOrWhiteSpace(_session.EmployeeId))
        {
            StatusTitle = "Cannot verify";
            StatusDetail = "Missing login session. Sign in again.";
            return;
        }

        StatusDetail = $"Hello {_session.EmployeeName}. Preparing face check…";
        await RunPipelineAsync();
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (_pipelineRunning) return;
        ShowRetry = false;
        WaitingForHr = false;
        await RunPipelineAsync();
    }

    private async Task RunPipelineAsync()
    {
        if (_session == null) return;
        _pipelineRunning = true;
        IsBusy = true;
        ShowRetry = false;
        WaitingForHr = false;
        ShowProgress = false;
        ShowReference = false;
        var ct = _cts.Token;
        try
        {
            StatusTitle = "Face verification";
            StatusDetail = "Loading local face data…";
            _store?.Dispose();
            _store = new FaceBiometricStore();
            await _store.InitializeAsync(ct);

            if (!File.Exists(FacePaths.DetPath) || !File.Exists(FacePaths.RecPath))
            {
                StatusTitle = "Models not ready";
                StatusDetail = "Open the tracker once while online so face models can sync, then tap Check again.";
                ShowRetry = true;
                return;
            }

            var templates = (await _store.GetTemplatesAsync(_session.EmployeeId, ct)).ToList();
            _samples = await _store.GetSamplesAsync(_session.EmployeeId, ct);
            if (templates.Count == 0)
            {
                WaitingForHr = true;
                StatusTitle = "No local face data";
                StatusDetail = "Meet HR to enroll, then keep the tracker online once so this PC can sync your face package.";
                ShowRetry = true;
                return;
            }

            var firstDone = await _store.IsFirstVerifyCompleteAsync(_session.EmployeeId, ct);
            if (!firstDone)
            {
                _mode = "first";
                _poseQueue = FacePoseCatalog.OrderedPoseIds(templates.Select(t => t.PoseId));
                if (_poseQueue.Count == 0)
                    _poseQueue = new[] { "front" };
                _poseIndex = 0;
                StatusTitle = "First-time face check";
                StatusDetail = $"Step 1 of {_poseQueue.Count}: {FacePoseCatalog.Label(_poseQueue[0])}";
            }
            else
            {
                _mode = "simple";
                _poseQueue = Array.Empty<string>();
                _poseIndex = 0;
                StatusTitle = "Look at the camera";
                StatusDetail = "Quick verification — keep your face centered.";
            }

            if (!string.IsNullOrWhiteSpace(_session.DeviceToken) && !string.IsNullOrWhiteSpace(_session.ApiBase))
            {
                _api?.Dispose();
                _api = new FaceApiClient(_session.ApiBase, _session.DeviceToken);
            }

            var threshold = _mode == "first"
                ? Math.Min(0.95, _session.MatchThreshold + 0.05)
                : _session.MatchThreshold;

            _engine?.Dispose();
            _engine = new FaceRecognitionEngine();
            _engine.Initialize(templates, threshold);
            await BeginCameraVerifyAsync(ct);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            StatusTitle = "Face check error";
            StatusDetail = $"{ex.Message} Tracking keeps running.";
            ShowRetry = true;
            await QueueEventAsync("model_error", null, ex.Message, null, ct);
        }
        finally
        {
            _pipelineRunning = false;
            IsBusy = false;
            ShowProgress = false;
        }
    }

    private async Task BeginCameraVerifyAsync(CancellationToken ct)
    {
        if (_engine == null || _session == null || _store == null) return;
        _failReports = 0;
        _poseFailStreak = 0;
        _frontVerifiedThisRun = false;
        UpdateReferenceForCurrentPose();

        while (!ct.IsCancellationRequested)
        {
            FaceMatchResult result;
            try
            {
                var poseFilter = _mode == "first" && _poseIndex < _poseQueue.Count
                    ? _poseQueue[_poseIndex]
                    : null;
                result = await Task.Run(() => _engine.CaptureAndCompare(poseFilter), ct);
            }
            catch (Exception ex)
            {
                await QueueEventAsync("camera_error", null, ex.Message, CurrentPoseId(), ct);
                StatusDetail = $"{ex.Message} Tracking keeps running.";
                ShowRetry = true;
                return;
            }

            if (result.PreviewBgr != null)
            {
                var bmp = MatToBitmap(result.PreviewBgr);
                result.PreviewBgr.Dispose();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Preview?.Dispose();
                    Preview = bmp;
                });
            }

            if (result.Matched)
            {
                var poseId = CurrentPoseId();
                if (IsFrontPose(poseId))
                    _frontVerifiedThisRun = true;
                await QueueEventAsync("verified", result.Score, null, poseId, ct);
                await _store.MarkVerifyAsync(_session.EmployeeId, "verified", failed: false, ct);
                _poseFailStreak = 0;

                if (_mode == "first")
                {
                    if (await AdvanceFirstPoseOrFinishAsync(ct))
                        return;
                    continue;
                }

                StatusTitle = "Verified";
                StatusDetail = $"Welcome back. Score {result.Score:0.000}";
                _allowClose = true;
                await Task.Delay(900, ct);
                RequestClose?.Invoke();
                return;
            }

            if (result.Detail.StartsWith("detect_error", StringComparison.Ordinal))
            {
                StatusDetail = "Camera processing hiccup — retrying…";
                await Task.Delay(1000, ct);
                continue;
            }

            StatusDetail = result.Detail == "no_face"
                ? "No face detected — center your face in the frame."
                : $"Not matched yet (score {result.Score:0.000}). {(_mode == "first" ? FacePoseCatalog.Label(CurrentPoseId() ?? "front") : "Keep looking at the camera.")}";

            if (result.Detail is "failed" or "no_face" or "align_failed" or "no_templates")
            {
                _failReports++;
                _poseFailStreak++;
                if (_failReports == 1 || _failReports % 15 == 0)
                {
                    await _store.MarkVerifyAsync(_session.EmployeeId, result.Detail, failed: true, ct);
                    await QueueEventAsync("failed", result.Score, result.Detail, CurrentPoseId(), ct);
                }

                // Escape hatch: skip a stuck non-front pose after N consecutive fails
                // (requires front already verified this run so soft-lock cannot bypass identity).
                if (_mode == "first"
                    && _frontVerifiedThisRun
                    && _poseFailStreak >= FirstPoseSkipAfterFails
                    && !IsFrontPose(CurrentPoseId()))
                {
                    var skipped = CurrentPoseId();
                    await QueueEventAsync(
                        "skipped_pose",
                        result.Score,
                        $"auto_skip_after_{FirstPoseSkipAfterFails}_fails",
                        skipped,
                        ct);
                    StatusDetail = $"Skipping difficult angle ({FacePoseCatalog.Label(skipped ?? "")}). Continuing…";
                    _poseFailStreak = 0;
                    if (await AdvanceFirstPoseOrFinishAsync(ct))
                        return;
                    continue;
                }
            }

            await Task.Delay(1000, ct);
        }
    }

    /// <summary>
    /// Advances the first-verify pose queue. Returns true when the wizard finished and closed.
    /// </summary>
    private async Task<bool> AdvanceFirstPoseOrFinishAsync(CancellationToken ct)
    {
        if (_session == null || _store == null) return false;
        _poseIndex++;
        _poseFailStreak = 0;
        if (_poseIndex < _poseQueue.Count)
        {
            StatusTitle = "First-time face check";
            StatusDetail =
                $"Step {_poseIndex + 1} of {_poseQueue.Count}: {FacePoseCatalog.Label(_poseQueue[_poseIndex])}";
            UpdateReferenceForCurrentPose();
            await Task.Delay(700, ct);
            return false;
        }

        if (!_frontVerifiedThisRun)
        {
            StatusTitle = "Front angle required";
            StatusDetail = "Could not verify the front pose. Tap Check again and look straight at the camera.";
            ShowRetry = true;
            return true;
        }

        await _store.MarkFirstVerifyCompleteAsync(_session.EmployeeId, ct);
        StatusTitle = "Verified";
        StatusDetail = "All angles matched. Welcome.";
        _allowClose = true;
        await Task.Delay(900, ct);
        RequestClose?.Invoke();
        return true;
    }

    private static bool IsFrontPose(string? poseId) =>
        string.Equals(poseId, "front", StringComparison.OrdinalIgnoreCase)
        || string.Equals(poseId, "front_return", StringComparison.OrdinalIgnoreCase);

    private string? CurrentPoseId() =>
        _mode == "first" && _poseIndex < _poseQueue.Count ? _poseQueue[_poseIndex] : null;

    private void UpdateReferenceForCurrentPose()
    {
        try
        {
            ReferenceImage?.Dispose();
            ReferenceImage = null;
            ShowReference = false;
            var pose = CurrentPoseId();
            if (string.IsNullOrWhiteSpace(pose)) return;
            var sample = _samples.FirstOrDefault(s =>
                string.Equals(s.PoseId, pose, StringComparison.OrdinalIgnoreCase)
                && File.Exists(s.LocalPath));
            if (sample == null) return;
            using var fs = File.OpenRead(sample.LocalPath);
            ReferenceImage = new Bitmap(fs);
            ShowReference = true;
        }
        catch
        {
            ShowReference = false;
        }
    }

    private async Task QueueEventAsync(
        string result,
        double? score,
        string? detail,
        string? poseId,
        CancellationToken ct)
    {
        if (_store == null || _session == null) return;
        var ev = new LocalFaceVerifyEvent
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = _session.EmployeeId,
            MachineId = _session.MachineId,
            Mode = _mode,
            Trigger = string.IsNullOrWhiteSpace(_session.Trigger) ? "boot" : _session.Trigger,
            PoseId = poseId ?? "",
            Result = result,
            Score = score,
            Threshold = _session.MatchThreshold,
            Detail = detail ?? "",
            CreatedAt = DateTime.UtcNow.ToString("O"),
        };
        try { await _store.EnqueueVerifyEventAsync(ev, ct); }
        catch { /* local queue must not crash UI */ }

        if (_api == null) return;
        try
        {
            await _api.VerifyEventAsync(
                result,
                _session.MachineId,
                score,
                _session.MatchThreshold,
                detail,
                ct,
                ev.Id,
                _mode,
                ev.Trigger,
                poseId,
                ev.CreatedAt);
            await _store.MarkEventsSyncedAsync(new[] { ev.Id }, ct);
        }
        catch
        {
            // Offline-first: leave queued for tracker warm-up drain.
        }
    }

    private static Bitmap MatToBitmap(Mat bgr)
    {
        Cv2.ImEncode(".bmp", bgr, out var bytes);
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _engine?.Dispose();
        _api?.Dispose();
        _store?.Dispose();
        Preview?.Dispose();
        ReferenceImage?.Dispose();
    }
}
