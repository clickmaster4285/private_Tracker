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
        if (_session == null || string.IsNullOrWhiteSpace(_session.DeviceToken))
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
        var ct = _cts.Token;
        try
        {
            ShowProgress = true;
            ProgressValue = 0;
            StatusTitle = "Downloading models";
            StatusDetail = "Downloading face models into this PC’s local cache…";
            ProgressText = "";
            _store?.Dispose();
            _store = new FaceBiometricStore();
            await _store.InitializeAsync(ct);
            _api?.Dispose();
            _api = new FaceApiClient(_session.ApiBase, _session.DeviceToken);

            var downloader = new FaceModelDownloader();
            var progress = new Progress<(string step, double? fraction)>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressText = p.step;
                    if (p.fraction is { } f) ProgressValue = Math.Clamp(f, 0, 1);
                });
            });
            await downloader.EnsureModelsAsync(_session, _api, progress, ct);

            // 1) Local cache → 2) server fetch → 3) HR message + poll (never blocks tracker/sync).
            var templates = (await _store.GetTemplatesAsync(_session.EmployeeId, ct)).ToList();
            if (templates.Count == 0)
            {
                StatusTitle = "Syncing face profile";
                StatusDetail = "Downloading your enrolled face templates from the server…";
                ProgressText = "";

                FaceEmbeddingsResponse emb;
                try
                {
                    emb = await _api.GetEmbeddingsAsync(ct);
                }
                catch (Exception ex)
                {
                    await ReportAsync("model_error", null, $"embeddings_fetch: {ex.Message}", ct);
                    StatusTitle = "Could not reach server";
                    StatusDetail = "Tracking keeps running. Check the network, then tap Check again.";
                    ShowRetry = true;
                    return;
                }

                if (emb.Profile == null || emb.Embeddings == null || emb.Embeddings.Count == 0
                    || string.Equals(emb.Status, "none", StringComparison.OrdinalIgnoreCase))
                {
                    await EnterWaitingForHrAsync(ct);
                    return;
                }

                templates = await PersistEmbeddingsAsync(emb, ct);
            }

            if (templates.Count == 0)
            {
                await EnterWaitingForHrAsync(ct);
                return;
            }

            await BeginCameraVerifyAsync(templates, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusTitle = "Face check error";
            StatusDetail = $"{ex.Message} Tracking and sync keep running in the background.";
            ShowRetry = true;
            try { await ReportAsync("model_error", null, ex.Message, ct); } catch { /* ignore */ }
        }
        finally
        {
            IsBusy = false;
            _pipelineRunning = false;
            ShowProgress = false;
        }
    }

    private async Task EnterWaitingForHrAsync(CancellationToken ct)
    {
        if (_session == null || _store == null || _api == null) return;

        StatusTitle = "Face not enrolled";
        StatusDetail = "Meet HR to enroll your face verification";
        ProgressText = "";
        ShowProgress = false;
        WaitingForHr = true;
        ShowRetry = true;
        await ReportAsync("no_profile", null, "no active embeddings", ct);
        await _store.ClearEmployeeAsync(_session.EmployeeId, ct);

        // Keep modal open; poll server so enrollment appears without restart.
        // Soft gate: tracker/sync are other processes / continue independently.
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(45), ct);
            try
            {
                var emb = await _api.GetEmbeddingsAsync(ct);
                if (emb.Profile == null || emb.Embeddings == null || emb.Embeddings.Count == 0
                    || string.Equals(emb.Status, "none", StringComparison.OrdinalIgnoreCase))
                {
                    StatusDetail = "Meet HR to enroll your face verification";
                    continue;
                }

                WaitingForHr = false;
                ShowRetry = false;
                var templates = await PersistEmbeddingsAsync(emb, ct);
                if (templates.Count == 0) continue;
                await BeginCameraVerifyAsync(templates, ct);
                return;
            }
            catch
            {
                // Network blip — keep waiting; never kill tracking.
                StatusDetail = "Meet HR to enroll your face verification (will retry when the server is reachable)";
            }
        }
    }

    private async Task<List<LocalFaceTemplate>> PersistEmbeddingsAsync(
        FaceEmbeddingsResponse emb,
        CancellationToken ct)
    {
        if (_session == null || _store == null || _api == null || emb.Profile == null || emb.Embeddings == null)
            return new List<LocalFaceTemplate>();

        var profile = new LocalFaceProfile
        {
            EmployeeId = _session.EmployeeId,
            ProfileId = emb.Profile.Id,
            Status = emb.Profile.Status,
            ProfileVersion = emb.Profile.ProfileVersion,
            EnrollmentVersion = emb.Profile.EnrollmentVersion,
            ModelId = emb.Profile.ModelId,
            ModelVersion = emb.Profile.ModelVersion,
            PreprocessVersion = emb.Profile.PreprocessVersion,
            EmbeddingDims = emb.Profile.EmbeddingDims,
            EmbeddingCount = emb.Embeddings.Count,
        };
        var templates = emb.Embeddings
            .Where(e => e.Vector is { Length: > 0 })
            .Select(e => new LocalFaceTemplate
            {
                Id = string.IsNullOrWhiteSpace(e.Id) ? Guid.NewGuid().ToString("N") : e.Id,
                EmployeeId = _session.EmployeeId,
                ProfileId = e.ProfileId,
                SampleId = e.SampleId,
                PoseId = e.PoseId,
                ModelId = e.ModelId,
                ModelVersion = e.ModelVersion,
                PreprocessVersion = e.PreprocessVersion,
                EmbeddingDims = e.EmbeddingDims,
                QualityScore = e.QualityScore,
                Vector = e.Vector!,
            })
            .ToList();

        await _store.ReplaceProfileAsync(profile, templates, ct);
        try
        {
            await _api.SyncAckAsync(profile.ProfileVersion, profile.ModelVersion, _session.MachineId, ct);
            await _store.MarkSyncAckAsync(_session.EmployeeId, profile.ProfileVersion, profile.ModelVersion, ct);
        }
        catch { /* ack is best-effort */ }

        return templates;
    }

    private async Task BeginCameraVerifyAsync(List<LocalFaceTemplate> templates, CancellationToken ct)
    {
        if (_session == null || _store == null) return;

        StatusTitle = "Look at the camera";
        StatusDetail = "Hold still until we verify your face.";
        ShowProgress = false;
        WaitingForHr = false;

        _engine?.Dispose();
        _engine = new FaceRecognitionEngine();
        try
        {
            _engine.Initialize(templates, _session.MatchThreshold);
        }
        catch (Exception ex)
        {
            var kind = ex.Message.Contains("webcam", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("camera", StringComparison.OrdinalIgnoreCase)
                ? "camera_error"
                : "model_error";
            await ReportAsync(kind, null, ex.Message, ct);
            StatusTitle = "Camera / model error";
            StatusDetail = $"{ex.Message} Tracking keeps running.";
            ShowRetry = true;
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            FaceMatchResult result;
            try
            {
                result = await Task.Run(() => _engine.CaptureAndCompare(), ct);
            }
            catch (Exception ex)
            {
                await ReportAsync("camera_error", null, ex.Message, ct);
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
                // Offline-first: persist local verify, then best-effort server audit.
                StatusTitle = "Verified";
                StatusDetail = $"Welcome back. Score {result.Score:0.000}";
                await _store.MarkVerifyAsync(_session.EmployeeId, "verified", failed: false, ct);
                try { await ReportAsync("verified", result.Score, null, ct); }
                catch { /* audit must not block close */ }
                _allowClose = true;
                await Task.Delay(900, ct);
                RequestClose?.Invoke();
                return;
            }

            if (result.Detail.StartsWith("detect_error", StringComparison.Ordinal))
            {
                StatusDetail = "Camera processing hiccup — retrying… Tracking keeps running.";
                await Task.Delay(1000, ct);
                continue;
            }

            StatusDetail = result.Detail == "no_face"
                ? "No face detected — center your face in the frame."
                : $"Not matched yet (score {result.Score:0.000}). Keep looking at the camera.";

            if (result.Detail is "failed" or "no_face" or "align_failed")
            {
                _failReports++;
                if (_failReports == 1 || _failReports % 15 == 0)
                {
                    await _store.MarkVerifyAsync(_session.EmployeeId, result.Detail, failed: true, ct);
                    try { await ReportAsync("failed", result.Score, result.Detail, ct); }
                    catch { /* best-effort */ }
                }
            }

            // Below-normal process + ~1s cadence so tracker/sync keep CPU/NIC.
            await Task.Delay(1000, ct);
        }
    }

    private async Task ReportAsync(string result, double? score, string? detail, CancellationToken ct)
    {
        if (_api == null || _session == null) return;
        try
        {
            await _api.VerifyEventAsync(result, _session.MachineId, score, _session.MatchThreshold, detail, ct);
        }
        catch
        {
            // Offline-first: server audit is best-effort only.
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
    }
}
