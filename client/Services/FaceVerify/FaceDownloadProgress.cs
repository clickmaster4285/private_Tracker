using System.Diagnostics;

namespace client.Services.FaceVerify;

/// <summary>
/// Progress for face ONNX model downloads (detector + recognition).
/// Fraction is overall 0..1 across both packages when a multi-step downloader reports it;
/// otherwise it is the current file fraction.
/// </summary>
public sealed class FaceDownloadProgress
{
    public string Step { get; init; } = "";
    public double? Fraction { get; init; }
    public long BytesRead { get; init; }
    public long? BytesTotal { get; init; }
    public string? EtaText { get; init; }
    public bool IsComplete { get; init; }
    public bool IsFailed { get; init; }
    public string? Error { get; init; }

    public string MbLine
    {
        get
        {
            var readMb = BytesRead / (1024.0 * 1024.0);
            if (BytesTotal is > 0)
                return $"{readMb:0.0} / {BytesTotal.Value / (1024.0 * 1024.0):0.0} MB";
            if (BytesRead > 0)
                return $"{readMb:0.0} MB downloaded";
            return "";
        }
    }

    public static FaceDownloadProgress Status(string step, double? fraction = null) => new()
    {
        Step = step,
        Fraction = fraction,
    };

    public static FaceDownloadProgress Complete(string step = "Face models ready") => new()
    {
        Step = step,
        Fraction = 1,
        IsComplete = true,
        EtaText = null,
    };

    public static FaceDownloadProgress Failed(string error) => new()
    {
        Step = "Download failed",
        IsFailed = true,
        Error = error,
    };
}

/// <summary>
/// Tracks byte rate for one HTTP download and formats ETA text.
/// </summary>
public sealed class FaceDownloadEtaTracker
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastReportMs;
    private readonly string _label;
    private readonly int _fileIndex; // 0 = det, 1 = rec
    private readonly int _fileCount;

    public FaceDownloadEtaTracker(string label, int fileIndex = 0, int fileCount = 1)
    {
        _label = label;
        _fileIndex = Math.Max(0, fileIndex);
        _fileCount = Math.Max(1, fileCount);
    }

    /// <summary>Returns null when throttled (caller should skip Report).</summary>
    public FaceDownloadProgress? TryReport(long read, long? total, bool force = false)
    {
        var ms = _sw.ElapsedMilliseconds;
        if (!force && ms - _lastReportMs < 250 && !(total is > 0 && read >= total.Value))
            return null;
        _lastReportMs = ms;
        return Build(read, total);
    }

    private FaceDownloadProgress Build(long read, long? total)
    {
        string? eta = null;
        double? fileFrac = null;
        if (total is > 0)
        {
            fileFrac = Math.Clamp((double)read / total.Value, 0, 1);
            var elapsedSec = Math.Max(_sw.Elapsed.TotalSeconds, 0.001);
            var bps = read / elapsedSec;
            if (bps > 1024 && read > 64 * 1024)
            {
                var remain = (total.Value - read) / bps;
                eta = FormatEta(remain);
            }
            else if (read > 0)
            {
                eta = "Estimating…";
            }
        }

        // Weight files equally for overall bar when multi-step.
        double? overall = null;
        if (fileFrac is not null)
            overall = (_fileIndex + fileFrac.Value) / _fileCount;
        else if (_fileCount > 1)
            overall = _fileIndex / (double)_fileCount;

        var mb = total is > 0
            ? $"{read / (1024.0 * 1024.0):0.0} / {total.Value / (1024.0 * 1024.0):0.0} MB"
            : (read > 0 ? $"{read / (1024.0 * 1024.0):0.0} MB" : "");
        var step = string.IsNullOrEmpty(mb)
            ? $"Downloading {_label}…"
            : $"Downloading {_label}… {mb}";
        if (!string.IsNullOrEmpty(eta))
            step = $"{step} · {eta}";

        return new FaceDownloadProgress
        {
            Step = step,
            Fraction = overall ?? fileFrac,
            BytesRead = read,
            BytesTotal = total,
            EtaText = eta,
        };
    }

    public static string FormatEta(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "Estimating…";
        if (seconds < 1.5) return "~1s left";
        if (seconds < 60) return $"~{(int)Math.Ceiling(seconds)}s left";
        var m = (int)(seconds / 60);
        var s = (int)Math.Ceiling(seconds % 60);
        if (m >= 60)
        {
            var h = m / 60;
            m %= 60;
            return m > 0 ? $"~{h}h {m}m left" : $"~{h}h left";
        }
        return s > 0 ? $"~{m}m {s}s left" : $"~{m}m left";
    }
}

/// <summary>
/// Process-wide publisher so Avalonia overlay / soft-lock can observe warm-up downloads.
/// </summary>
public static class FaceDownloadStatus
{
    private static readonly object Gate = new();
    private static FaceDownloadProgress? _current;
    public static event Action<FaceDownloadProgress>? Changed;

    public static FaceDownloadProgress? Current
    {
        get { lock (Gate) return _current; }
    }

    public static bool IsActive
    {
        get
        {
            lock (Gate)
                return _current is { IsComplete: false, IsFailed: false };
        }
    }

    public static void Publish(FaceDownloadProgress progress)
    {
        lock (Gate) _current = progress;
        try { Changed?.Invoke(progress); } catch { /* UI listeners must not break download */ }
    }

    public static void Clear()
    {
        lock (Gate) _current = null;
    }

    public static IProgress<FaceDownloadProgress> CreateProgressSink() =>
        new Progress<FaceDownloadProgress>(Publish);
}
