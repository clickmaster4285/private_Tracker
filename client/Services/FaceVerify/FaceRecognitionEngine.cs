using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace client.Services.FaceVerify;

public sealed class FaceMatchResult
{
    /// <summary>True only after consecutive-frame + motion PAD gates pass.</summary>
    public bool Matched { get; init; }
    public double Score { get; init; }
    public string Detail { get; init; } = "";
    public Mat? PreviewBgr { get; init; }
    public int MatchStreak { get; init; }
    /// <summary>This frame alone cleared the cosine threshold (before streak/PAD).</summary>
    public bool FrameAboveThreshold { get; init; }
}

/// <summary>
/// Windows webcam → ORT SCRFD (det_10g) → LMEDS align 112 → buffalo_l w600k_r50 → cosine vs local templates.
/// Must stay numerically aligned with server/face_worker (face-preprocess-v2-buffalo-l).
/// </summary>
public sealed class FaceRecognitionEngine : IDisposable
{
    private static readonly Point2f[] ArcFace5 =
    {
        new(38.2946f, 51.6963f),
        new(73.5318f, 51.5014f),
        new(56.0252f, 71.7366f),
        new(41.5493f, 92.3655f),
        new(70.7299f, 92.2041f),
    };

    private static readonly int[] FeatStrides = { 8, 16, 32 };
    private const int NumAnchors = 2;
    private const int DetInput = 640;
    private const float DetThresh = 0.5f;
    /// <summary>Lower SCRFD score when frame mean luminance is dim.</summary>
    private const float DetThreshDim = 0.35f;
    private const float QualityScoreFloor = 0.65f;
    private const float QualityScoreFloorDim = 0.55f;
    /// <summary>Mean gray &lt; this → CLAHE + dim det/quality floors.</summary>
    private const double DimLumaMean = 70.0;
    private const float NmsThresh = 0.4f;
    private const int MinFacePx = 60;
    private const int RequiredMatchStreak = 3;
    private const double MinMotionPx = 6.0;
    private const int MinFrameSpacingMs = 180;
    /// <summary>~50–70 cm org band: face height / frame height (laptop webcam FOV).</summary>
    private const float DistanceMinFrac = 0.25f;
    private const float DistanceMaxFrac = 0.48f;

    private VideoCapture? _cam;
    private InferenceSession? _det;
    private InferenceSession? _rec;
    private string _detInput = "input.1";
    private string _recInput = "input.1";
    private string[] _detOutputs = Array.Empty<string>();
    private bool _useKps = true;
    private readonly Dictionary<(int, int, int), float[,]> _centerCache = new();
    private readonly List<(string? PoseId, float[] Vector)> _templates = new();
    private double _threshold = 0.8;
    private bool _disposed;
    private int _matchStreak;
    private double _motionAccum;
    private Point2f? _prevCenter;
    private DateTime _lastStreakUtc = DateTime.MinValue;

    public bool IsReady =>
        _cam != null && _cam.IsOpened() && _rec != null && _det != null && _templates.Count > 0;

    public bool ModelsReady => _rec != null && _det != null;

    public void Initialize(IReadOnlyList<LocalFaceTemplate> templates, double threshold)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Face verify is Windows-only.");

        _threshold = Math.Max(0.8, threshold);
        _templates.Clear();
        foreach (var t in templates)
        {
            if (t.Vector is { Length: > 0 })
                _templates.Add((t.PoseId, L2Normalize(t.Vector)));
        }

        LoadModels();

        _cam?.Dispose();
        _cam = new VideoCapture(0, VideoCaptureAPIs.DSHOW);
        if (!_cam.IsOpened())
        {
            _cam.Dispose();
            _cam = new VideoCapture(0);
        }
        if (_cam == null || !_cam.IsOpened())
            throw new InvalidOperationException("Could not open webcam.");
        TryBoostCameraLowLight(_cam);
    }

    /// <summary>Load SCRFD + buffalo_l recognition without opening the webcam.</summary>
    public void LoadModels()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Face verify is Windows-only.");
        if (!File.Exists(FacePaths.DetPath))
            throw new FileNotFoundException("buffalo_l det_10g missing", FacePaths.DetPath);
        if (!File.Exists(FacePaths.RecPath))
            throw new FileNotFoundException("buffalo_l w600k_r50 missing", FacePaths.RecPath);

        var threads = Math.Max(1, Environment.ProcessorCount);
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = threads,
            InterOpNumThreads = threads,
        };
#if FACE_WINFORMS_LOCKER
        try
        {
            opts.AppendExecutionProvider_DML(0);
            Console.Error.WriteLine("[face] ORT EP=DirectML (+CPU fallback), threads=" + threads);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[face] DirectML unavailable ({ex.Message}); CPU only, threads={threads}");
        }
#endif
        opts.AppendExecutionProvider_CPU();

        _det?.Dispose();
        _rec?.Dispose();
        _det = new InferenceSession(FacePaths.DetPath, opts);
        _rec = new InferenceSession(FacePaths.RecPath, opts);
        _detInput = _det.InputMetadata.Keys.First();
        _recInput = _rec.InputMetadata.Keys.First();
        _detOutputs = _det.OutputMetadata.Keys.ToArray();
        _useKps = _detOutputs.Length == 9;
        _centerCache.Clear();
    }

    /// <summary>Best-effort auto-exposure / gain for dim offices (DirectShow props vary by webcam).</summary>
    private static void TryBoostCameraLowLight(VideoCapture cam)
    {
        try
        {
            // Many DSHOW drivers: 0.75 = auto exposure on.
            cam.Set(VideoCaptureProperties.AutoExposure, 0.75);
            cam.Set(VideoCaptureProperties.Brightness, Math.Max(cam.Get(VideoCaptureProperties.Brightness), 0.55));
            cam.Set(VideoCaptureProperties.Gain, Math.Max(cam.Get(VideoCaptureProperties.Gain), 0.4));
            cam.Set(VideoCaptureProperties.Contrast, Math.Max(cam.Get(VideoCaptureProperties.Contrast), 0.45));
        }
        catch
        {
            // Driver may reject any property — ignore.
        }
    }

    public float[]? EmbedImageFile(string imagePath)
    {
        if (_rec == null || _det == null)
            LoadModels();
        using var bgr = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (bgr.Empty())
            return null;
        var faces = DetectFaces(bgr);
        if (faces.Count < 1)
            return null;
        var best = faces.OrderByDescending(f => f.W * f.H).First();
        using var aligned = AlignFace112(bgr, best);
        if (aligned == null)
            return null;
        return Embed(aligned);
    }

    public void ResetMatchStreak()
    {
        _matchStreak = 0;
        _motionAccum = 0;
        _prevCenter = null;
        _lastStreakUtc = DateTime.MinValue;
    }

    public FaceMatchResult CaptureAndCompare(string? poseId = null)
    {
        try
        {
            if (_cam == null || _rec == null || _det == null)
                return new FaceMatchResult { Detail = "engine_not_ready" };

            using var frame = new Mat();
            if (!_cam.Read(frame) || frame.Empty())
                return new FaceMatchResult { Detail = "camera_frame_empty" };

            var preview = frame.Clone();
            var dim = IsDimFrame(frame);
            // Detect on CLAHE-enhanced copy when dim; align/embed stay on raw for enrollment parity.
            var faces = DetectFaces(frame, dim);
            if (faces.Count < 1)
            {
                ResetMatchStreak();
                return new FaceMatchResult { PreviewBgr = preview, Detail = "no_face", Score = 0 };
            }
            if (faces.Count > 1)
            {
                ResetMatchStreak();
                return new FaceMatchResult { PreviewBgr = preview, Detail = "multiple_faces", Score = 0 };
            }

            var best = faces[0];
            var dist = CheckFaceDistance(best, frame.Height);
            if (dist != null)
            {
                ResetMatchStreak();
                Cv2.Rectangle(preview,
                    new Rect((int)best.X, (int)best.Y, (int)best.W, (int)best.H),
                    Scalar.Orange, 2);
                return new FaceMatchResult { PreviewBgr = preview, Detail = dist, Score = 0 };
            }

            var aligned = AlignFace112(frame, best);
            if (aligned == null)
            {
                ResetMatchStreak();
                return new FaceMatchResult { PreviewBgr = preview, Detail = "align_failed", Score = 0 };
            }

            using (aligned)
            {
                var qualityFloor = dim ? QualityScoreFloorDim : QualityScoreFloor;
                if (best.Score < qualityFloor || Math.Max(best.W, best.H) < MinFacePx * 1.2)
                {
                    ResetMatchStreak();
                    return new FaceMatchResult { PreviewBgr = preview, Detail = "low_quality", Score = 0 };
                }

                var emb = Embed(aligned);
                if (emb == null)
                {
                    ResetMatchStreak();
                    return new FaceMatchResult { PreviewBgr = preview, Detail = "embed_failed", Score = 0 };
                }

                var candidates = SelectTemplates(poseId);
                if (candidates.Count == 0)
                    return new FaceMatchResult { PreviewBgr = preview, Detail = "no_templates", Score = 0 };

                var score = 0.0;
                foreach (var t in candidates)
                {
                    var s = Dot(emb, t);
                    if (s > score) score = s;
                }

                var above = score >= _threshold;
                var center = new Point2f(best.X + best.W / 2f, best.Y + best.H / 2f);
                var now = DateTime.UtcNow;

                if (!above)
                {
                    ResetMatchStreak();
                    Cv2.Rectangle(preview,
                        new Rect((int)best.X, (int)best.Y, (int)best.W, (int)best.H),
                        Scalar.OrangeRed, 2);
                    return new FaceMatchResult
                    {
                        Matched = false,
                        Score = score,
                        Detail = "failed",
                        PreviewBgr = preview,
                        FrameAboveThreshold = false,
                        MatchStreak = 0,
                    };
                }

                if (_lastStreakUtc != DateTime.MinValue
                    && (now - _lastStreakUtc).TotalMilliseconds < MinFrameSpacingMs)
                {
                    Cv2.Rectangle(preview,
                        new Rect((int)best.X, (int)best.Y, (int)best.W, (int)best.H),
                        Scalar.Orange, 2);
                    return new FaceMatchResult
                    {
                        Matched = false,
                        Score = score,
                        Detail = "spacing",
                        PreviewBgr = preview,
                        FrameAboveThreshold = true,
                        MatchStreak = _matchStreak,
                    };
                }

                if (_prevCenter is { } prev)
                {
                    var dx = center.X - prev.X;
                    var dy = center.Y - prev.Y;
                    _motionAccum += Math.Sqrt(dx * dx + dy * dy);
                }
                _prevCenter = center;
                _lastStreakUtc = now;
                _matchStreak++;

                var padOk = _matchStreak < RequiredMatchStreak
                    || _motionAccum >= MinMotionPx;
                var matched = _matchStreak >= RequiredMatchStreak && padOk;
                if (_matchStreak >= RequiredMatchStreak && !padOk)
                {
                    ResetMatchStreak();
                    Cv2.Rectangle(preview,
                        new Rect((int)best.X, (int)best.Y, (int)best.W, (int)best.H),
                        Scalar.OrangeRed, 2);
                    return new FaceMatchResult
                    {
                        Matched = false,
                        Score = score,
                        Detail = "pad_static",
                        PreviewBgr = preview,
                        FrameAboveThreshold = true,
                        MatchStreak = 0,
                    };
                }

                Cv2.Rectangle(preview,
                    new Rect((int)best.X, (int)best.Y, (int)best.W, (int)best.H),
                    matched ? Scalar.LimeGreen : Scalar.Orange, 2);

                return new FaceMatchResult
                {
                    Matched = matched,
                    Score = score,
                    Detail = matched ? "verified" : $"streak_{_matchStreak}/{RequiredMatchStreak}",
                    PreviewBgr = preview,
                    FrameAboveThreshold = true,
                    MatchStreak = _matchStreak,
                };
            }
        }
        catch (Exception ex)
        {
            ResetMatchStreak();
            return new FaceMatchResult
            {
                Detail = "detect_error:" + ex.GetType().Name,
                Score = 0,
            };
        }
    }

    /// <summary>
    /// Org policy 50–70 cm approximated by face-height fraction of the frame
    /// (same band as server face_worker <c>check_face_distance</c>).
    /// </summary>
    private static string? CheckFaceDistance(ScrfdFace face, int imgH)
    {
        if (imgH <= 0) return "too_far";
        var minFrac = DistanceMinFrac;
        var maxFrac = DistanceMaxFrac;
        try
        {
            var envMin = Environment.GetEnvironmentVariable("ALPHA_FACE_DISTANCE_MIN_FRAC");
            var envMax = Environment.GetEnvironmentVariable("ALPHA_FACE_DISTANCE_MAX_FRAC");
            if (float.TryParse(envMin, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lo))
                minFrac = Math.Clamp(lo, 0.05f, 0.9f);
            if (float.TryParse(envMax, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var hi))
                maxFrac = Math.Clamp(hi, minFrac + 0.05f, 0.95f);
        }
        catch { /* defaults */ }

        var frac = face.H / imgH;
        if (frac < minFrac) return "too_far";
        if (frac > maxFrac) return "too_close";
        return null;
    }

    private List<float[]> SelectTemplates(string? poseId)
    {
        if (!string.IsNullOrWhiteSpace(poseId))
        {
            return _templates
                .Where(t => string.Equals(t.PoseId, poseId, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Vector)
                .ToList();
        }

        var front = _templates
            .Where(t => t.PoseId is "front" or "front_return")
            .Select(t => t.Vector)
            .ToList();
        if (front.Count > 0) return front;
        return _templates.Select(t => t.Vector).ToList();
    }

    private sealed class ScrfdFace
    {
        public float X, Y, W, H;
        public Point2f Le, Re, Nt, Lm, Rm;
        public float Score;
    }

    private static bool IsDimFrame(Mat bgr)
    {
        if (bgr.Empty()) return false;
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        return Cv2.Mean(gray).Val0 < DimLumaMean;
    }

    /// <summary>CLAHE on L channel — geometry unchanged so landmarks map to the raw frame.</summary>
    private static Mat EnhanceClaheBgr(Mat bgr)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        var channels = Cv2.Split(lab);
        try
        {
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8));
            clahe.Apply(channels[0], channels[0]);
            using var merged = new Mat();
            Cv2.Merge(channels, merged);
            var outBgr = new Mat();
            Cv2.CvtColor(merged, outBgr, ColorConversionCodes.Lab2BGR);
            return outBgr;
        }
        finally
        {
            foreach (var c in channels) c.Dispose();
        }
    }

    private List<ScrfdFace> DetectFaces(Mat bgr, bool? dimOverride = null)
    {
        if (_det == null || bgr.Empty() || bgr.Width < 16 || bgr.Height < 16)
            return new List<ScrfdFace>();

        var dim = dimOverride ?? IsDimFrame(bgr);
        var detThresh = dim ? DetThreshDim : DetThresh;
        Mat work = bgr;
        Mat? enhanced = null;
        try
        {
            if (dim)
            {
                enhanced = EnhanceClaheBgr(bgr);
                work = enhanced;
            }

            return DetectFacesCore(work, detThresh);
        }
        finally
        {
            enhanced?.Dispose();
        }
    }

    private List<ScrfdFace> DetectFacesCore(Mat bgr, float detThresh)
    {
        var list = new List<ScrfdFace>();
        if (_det == null || bgr.Empty() || bgr.Width < 16 || bgr.Height < 16)
            return list;

        var imgH = bgr.Height;
        var imgW = bgr.Width;
        var imRatio = (float)imgH / imgW;
        const float modelRatio = 1f;
        int newWidth, newHeight;
        if (imRatio > modelRatio)
        {
            newHeight = DetInput;
            newWidth = Math.Max(1, (int)(newHeight / imRatio));
        }
        else
        {
            newWidth = DetInput;
            newHeight = Math.Max(1, (int)(newWidth * imRatio));
        }
        var detScale = (float)newHeight / imgH;

        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new OpenCvSharp.Size(newWidth, newHeight));
        using var detImg = new Mat(DetInput, DetInput, MatType.CV_8UC3, Scalar.All(0));
        resized.CopyTo(detImg[new Rect(0, 0, newWidth, newHeight)]);

        var tensor = new DenseTensor<float>(new[] { 1, 3, DetInput, DetInput });
        for (var y = 0; y < DetInput; y++)
        for (var x = 0; x < DetInput; x++)
        {
            var p = detImg.At<Vec3b>(y, x);
            // InsightFace SCRFD: RGB, (x - 127.5) / 128
            tensor[0, 0, y, x] = (p.Item2 - 127.5f) / 128f;
            tensor[0, 1, y, x] = (p.Item1 - 127.5f) / 128f;
            tensor[0, 2, y, x] = (p.Item0 - 127.5f) / 128f;
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_detInput, tensor) };
        using var results = _det.Run(inputs);
        var outs = results.Select(r => r.AsEnumerable<float>().ToArray()).ToArray();

        var scoresList = new List<float>();
        var boxesList = new List<float[]>();
        var kpsList = new List<float[]>();

        for (var idx = 0; idx < FeatStrides.Length; idx++)
        {
            var stride = FeatStrides[idx];
            var scores = outs[idx];
            var bboxPreds = outs[idx + 3];
            float[]? kpsPreds = _useKps ? outs[idx + 6] : null;

            var height = DetInput / stride;
            var width = DetInput / stride;
            var anchors = GetAnchorCenters(height, width, stride);

            var num = scores.Length;
            for (var i = 0; i < num; i++)
            {
                if (scores[i] < detThresh) continue;
                var bi = i * 4;
                var x1 = anchors[i, 0] - bboxPreds[bi] * stride;
                var y1 = anchors[i, 1] - bboxPreds[bi + 1] * stride;
                var x2 = anchors[i, 0] + bboxPreds[bi + 2] * stride;
                var y2 = anchors[i, 1] + bboxPreds[bi + 3] * stride;
                scoresList.Add(scores[i]);
                boxesList.Add(new[] { x1, y1, x2, y2 });
                if (kpsPreds != null)
                {
                    var ki = i * 10;
                    var kps = new float[10];
                    for (var k = 0; k < 5; k++)
                    {
                        kps[k * 2] = anchors[i, 0] + kpsPreds[ki + k * 2] * stride;
                        kps[k * 2 + 1] = anchors[i, 1] + kpsPreds[ki + k * 2 + 1] * stride;
                    }
                    kpsList.Add(kps);
                }
            }
        }

        if (scoresList.Count == 0)
            return list;

        var order = Enumerable.Range(0, scoresList.Count).OrderByDescending(i => scoresList[i]).ToArray();
        var dets = new List<float[]>();
        var kpsOrdered = new List<float[]>();
        foreach (var i in order)
        {
            var b = boxesList[i];
            dets.Add(new[] { b[0], b[1], b[2], b[3], scoresList[i] });
            if (kpsList.Count > i)
                kpsOrdered.Add(kpsList[i]);
        }

        var keep = Nms(dets, NmsThresh);
        foreach (var i in keep)
        {
            var d = dets[i];
            var x1 = d[0] / detScale;
            var y1 = d[1] / detScale;
            var x2 = d[2] / detScale;
            var y2 = d[3] / detScale;
            var w = Math.Max(0f, x2 - x1);
            var h = Math.Max(0f, y2 - y1);
            if (Math.Max(w, h) < MinFacePx) continue;

            Point2f le, re, nt, lm, rm;
            if (i < kpsOrdered.Count)
            {
                var k = kpsOrdered[i];
                le = new Point2f(k[0] / detScale, k[1] / detScale);
                re = new Point2f(k[2] / detScale, k[3] / detScale);
                nt = new Point2f(k[4] / detScale, k[5] / detScale);
                lm = new Point2f(k[6] / detScale, k[7] / detScale);
                rm = new Point2f(k[8] / detScale, k[9] / detScale);
            }
            else
            {
                var cx = x1 + w * 0.5f;
                var cy = y1 + h * 0.5f;
                le = new Point2f(cx - w * 0.2f, cy - h * 0.15f);
                re = new Point2f(cx + w * 0.2f, cy - h * 0.15f);
                nt = new Point2f(cx, cy);
                lm = new Point2f(cx - w * 0.15f, cy + h * 0.2f);
                rm = new Point2f(cx + w * 0.15f, cy + h * 0.2f);
            }

            list.Add(new ScrfdFace
            {
                X = x1, Y = y1, W = w, H = h,
                Le = le, Re = re, Nt = nt, Lm = lm, Rm = rm,
                Score = d[4],
            });
        }

        return list;
    }

    private float[,] GetAnchorCenters(int height, int width, int stride)
    {
        var key = (height, width, stride);
        if (_centerCache.TryGetValue(key, out var cached))
            return cached;

        var n = height * width * NumAnchors;
        var centers = new float[n, 2];
        var idx = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var a = 0; a < NumAnchors; a++)
        {
            centers[idx, 0] = x * stride;
            centers[idx, 1] = y * stride;
            idx++;
        }
        _centerCache[key] = centers;
        return centers;
    }

    private static List<int> Nms(List<float[]> dets, float thresh)
    {
        var keep = new List<int>();
        var order = Enumerable.Range(0, dets.Count).OrderByDescending(i => dets[i][4]).ToList();
        var areas = dets.Select(d => (d[2] - d[0] + 1) * (d[3] - d[1] + 1)).ToArray();
        while (order.Count > 0)
        {
            var i = order[0];
            keep.Add(i);
            var next = new List<int>();
            for (var j = 1; j < order.Count; j++)
            {
                var k = order[j];
                var xx1 = Math.Max(dets[i][0], dets[k][0]);
                var yy1 = Math.Max(dets[i][1], dets[k][1]);
                var xx2 = Math.Min(dets[i][2], dets[k][2]);
                var yy2 = Math.Min(dets[i][3], dets[k][3]);
                var w = Math.Max(0f, xx2 - xx1 + 1);
                var h = Math.Max(0f, yy2 - yy1 + 1);
                var inter = w * h;
                var ovr = inter / (areas[i] + areas[k] - inter);
                if (ovr <= thresh)
                    next.Add(k);
            }
            order = next;
        }
        return keep;
    }

    private static Mat? AlignFace112(Mat bgr, ScrfdFace face)
    {
        var src = new[] { face.Le, face.Re, face.Nt, face.Lm, face.Rm };
        using var inliers = new Mat();
        using var m = Cv2.EstimateAffinePartial2D(
            InputArray.Create(src),
            InputArray.Create(ArcFace5),
            inliers,
            RobustEstimationAlgorithms.LMEDS);
        if (m == null || m.Empty())
            return null;
        var outMat = new Mat();
        Cv2.WarpAffine(bgr, outMat, m, new OpenCvSharp.Size(112, 112), InterpolationFlags.Linear,
            BorderTypes.Constant, Scalar.All(0));
        return outMat;
    }

    private float[]? Embed(Mat faceBgr112)
    {
        if (_rec == null) return null;
        using var rgb = new Mat();
        Cv2.CvtColor(faceBgr112, rgb, ColorConversionCodes.BGR2RGB);
        var tensor = new DenseTensor<float>(new[] { 1, 3, 112, 112 });
        for (var y = 0; y < 112; y++)
        for (var x = 0; x < 112; x++)
        {
            var p = rgb.At<Vec3b>(y, x);
            // InsightFace ArcFace: (x - 127.5) / 127.5
            tensor[0, 0, y, x] = (p.Item0 - 127.5f) / 127.5f;
            tensor[0, 1, y, x] = (p.Item1 - 127.5f) / 127.5f;
            tensor[0, 2, y, x] = (p.Item2 - 127.5f) / 127.5f;
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_recInput, tensor) };
        using var results = _rec.Run(inputs);
        var output = results.First().AsEnumerable<float>().ToArray();
        return L2Normalize(output);
    }

    private static float[] L2Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var n = Math.Sqrt(sum);
        if (n < 1e-12) return (float[])v.Clone();
        var o = new float[v.Length];
        for (var i = 0; i < v.Length; i++) o[i] = (float)(v[i] / n);
        return o;
    }

    private static double Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double s = 0;
        for (var i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cam?.Dispose();
        _det?.Dispose();
        _rec?.Dispose();
    }
}
