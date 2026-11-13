using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace client.Services.FaceVerify;

public sealed class FaceMatchResult
{
    public bool Matched { get; init; }
    public double Score { get; init; }
    public string Detail { get; init; } = "";
    public Mat? PreviewBgr { get; init; }
}

/// <summary>
/// Windows webcam → OpenCvSharp FaceDetectorYN (YuNet) → align 112 → AdaFace ONNX → cosine vs local templates.
/// Detection matches server face_worker (cv2.FaceDetectorYN); recognition stays ONNX Runtime.
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

    private const float ScoreThreshold = 0.5f;
    private const float NmsThreshold = 0.3f;
    private const int TopK = 5000;
    private const int MinFacePx = 60;

    private VideoCapture? _cam;
    private FaceDetectorYN? _detector;
    private Size _detectorInputSize;
    private InferenceSession? _adaface;
    private string _adaInput = "input";
    private readonly List<float[]> _templates = new();
    private double _threshold = 0.4;
    private bool _disposed;

    public bool IsReady =>
        _cam != null && _cam.IsOpened() && _adaface != null && _templates.Count > 0;

    public void Initialize(IReadOnlyList<LocalFaceTemplate> templates, double threshold)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Face verify is Windows-only.");

        _threshold = threshold;
        _templates.Clear();
        foreach (var t in templates)
        {
            if (t.Vector is { Length: > 0 })
                _templates.Add(L2Normalize(t.Vector));
        }

        if (!File.Exists(FacePaths.YuNetPath))
            throw new FileNotFoundException("YuNet model missing", FacePaths.YuNetPath);
        if (!File.Exists(FacePaths.AdaFacePath))
            throw new FileNotFoundException("AdaFace model missing", FacePaths.AdaFacePath);

        // Detector is created lazily with the live frame size (OpenCvSharp 4.13
        // FaceDetectorYN.Create takes inputSize; no SetInputSize on this package).
        _detector?.Dispose();
        _detector = null;
        _detectorInputSize = default;

        var opts = new SessionOptions();
        opts.AppendExecutionProvider_CPU();
        _adaface?.Dispose();
        _adaface = new InferenceSession(FacePaths.AdaFacePath, opts);
        _adaInput = _adaface.InputMetadata.Keys.First();

        _cam?.Dispose();
        _cam = new VideoCapture(0, VideoCaptureAPIs.DSHOW);
        if (!_cam.IsOpened())
        {
            _cam.Dispose();
            _cam = new VideoCapture(0);
        }
        if (_cam == null || !_cam.IsOpened())
            throw new InvalidOperationException("Could not open webcam.");
    }

    private void EnsureDetector(Size frameSize)
    {
        if (_detector != null && _detectorInputSize == frameSize)
            return;
        _detector?.Dispose();
        _detector = FaceDetectorYN.Create(
            FacePaths.YuNetPath,
            config: "",
            frameSize,
            scoreThreshold: ScoreThreshold,
            nmsThreshold: NmsThreshold,
            topK: TopK);
        _detectorInputSize = frameSize;
    }

    public FaceMatchResult CaptureAndCompare()
    {
        try
        {
            if (_cam == null || _adaface == null)
                return new FaceMatchResult { Detail = "engine_not_ready" };

            using var frame = new Mat();
            if (!_cam.Read(frame) || frame.Empty())
                return new FaceMatchResult { Detail = "camera_frame_empty" };

            EnsureDetector(new Size(frame.Width, frame.Height));
            var preview = frame.Clone();
            var faces = DetectFaces(frame);
            if (faces.Count < 1)
                return new FaceMatchResult { PreviewBgr = preview, Detail = "no_face", Score = 0 };

            var best = faces.OrderByDescending(f => f.W * f.H).First();
            var aligned = AlignFace112(frame, best);
            if (aligned == null)
                return new FaceMatchResult { PreviewBgr = preview, Detail = "align_failed", Score = 0 };

            using (aligned)
            {
                var emb = Embed(aligned);
                if (emb == null)
                    return new FaceMatchResult { PreviewBgr = preview, Detail = "embed_failed", Score = 0 };

                var score = 0.0;
                foreach (var t in _templates)
                {
                    var s = Dot(emb, t);
                    if (s > score) score = s;
                }

                var matched = score >= _threshold;
                Cv2.Rectangle(preview,
                    new Rect((int)best.X, (int)best.Y, (int)best.W, (int)best.H),
                    matched ? Scalar.LimeGreen : Scalar.OrangeRed, 2);

                return new FaceMatchResult
                {
                    Matched = matched,
                    Score = score,
                    Detail = matched ? "verified" : "failed",
                    PreviewBgr = preview,
                };
            }
        }
        catch (Exception ex)
        {
            return new FaceMatchResult
            {
                Detail = "detect_error:" + ex.GetType().Name,
                Score = 0,
            };
        }
    }

    private sealed class YuNetFace
    {
        public float X, Y, W, H;
        public Point2f Le, Re, Nt, Lm, Rm;
        public float Score;
    }

    private List<YuNetFace> DetectFaces(Mat bgr)
    {
        var list = new List<YuNetFace>();
        if (_detector == null || bgr.Empty() || bgr.Width < 16 || bgr.Height < 16)
            return list;

        using var faces = new Mat();
        _detector.Detect(bgr, faces);
        if (faces.Empty())
            return list;

        var rows = faces.Rows;
        var cols = faces.Cols;
        if (rows < 1 || cols < 15)
            return list;

        for (var i = 0; i < rows; i++)
        {
            // OpenCV FaceDetectorYN row: x,y,w,h, re_x,re_y, le_x,le_y, nose, rm, lm, score
            var x = faces.At<float>(i, 0);
            var y = faces.At<float>(i, 1);
            var w = faces.At<float>(i, 2);
            var h = faces.At<float>(i, 3);
            if (Math.Max(w, h) < MinFacePx)
                continue;

            var score = faces.At<float>(i, 14);
            if (score < ScoreThreshold)
                continue;

            list.Add(new YuNetFace
            {
                X = x,
                Y = y,
                W = w,
                H = h,
                Re = new Point2f(faces.At<float>(i, 4), faces.At<float>(i, 5)),
                Le = new Point2f(faces.At<float>(i, 6), faces.At<float>(i, 7)),
                Nt = new Point2f(faces.At<float>(i, 8), faces.At<float>(i, 9)),
                Rm = new Point2f(faces.At<float>(i, 10), faces.At<float>(i, 11)),
                Lm = new Point2f(faces.At<float>(i, 12), faces.At<float>(i, 13)),
                Score = score,
            });
        }

        return list;
    }

    private static Mat? AlignFace112(Mat bgr, YuNetFace face)
    {
        var src = new[] { face.Le, face.Re, face.Nt, face.Lm, face.Rm };
        using var m = Cv2.EstimateAffinePartial2D(
            InputArray.Create(src),
            InputArray.Create(ArcFace5));
        if (m == null || m.Empty())
            return null;
        var outMat = new Mat();
        Cv2.WarpAffine(bgr, outMat, m, new Size(112, 112), InterpolationFlags.Linear,
            BorderTypes.Constant, Scalar.All(0));
        return outMat;
    }

    private float[]? Embed(Mat faceBgr112)
    {
        if (_adaface == null) return null;
        using var rgb = new Mat();
        Cv2.CvtColor(faceBgr112, rgb, ColorConversionCodes.BGR2RGB);
        var tensor = new DenseTensor<float>(new[] { 1, 3, 112, 112 });
        for (var y = 0; y < 112; y++)
        for (var x = 0; x < 112; x++)
        {
            var p = rgb.At<Vec3b>(y, x);
            tensor[0, 0, y, x] = (p.Item0 / 255f - 0.5f) / 0.5f;
            tensor[0, 1, y, x] = (p.Item1 / 255f - 0.5f) / 0.5f;
            tensor[0, 2, y, x] = (p.Item2 / 255f - 0.5f) / 0.5f;
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_adaInput, tensor) };
        using var results = _adaface.Run(inputs);
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
        _detector?.Dispose();
        _adaface?.Dispose();
    }
}
