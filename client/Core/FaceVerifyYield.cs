namespace client.Core;

/// <summary>
/// While the Windows soft-locker (<c>--face-verify</c>) is open, heavy tracker work
/// yields CPU/bandwidth (collectors, sync, screenshots, live stream, browser a11y).
/// DLP enforcement is never parked. Flag: LocalAppData …/face/verify_yield.
/// </summary>
public static class FaceVerifyYield
{
    private static readonly object Gate = new();
    private static bool _cached;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(1);

    public static bool ShouldYield
    {
        get
        {
            lock (Gate)
            {
                if (DateTime.UtcNow - _cachedAt < CacheTtl)
                    return _cached;
                _cached = ReadActive();
                _cachedAt = DateTime.UtcNow;
                return _cached;
            }
        }
    }

    public static void Invalidate()
    {
        lock (Gate) _cachedAt = DateTime.MinValue;
    }

    public static void Set(string reason)
    {
        try
        {
            var path = FlagPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{DateTime.UtcNow:o}\n{reason.Trim()}\n");
            Invalidate();
        }
        catch { /* ignore */ }
    }

    public static void Clear()
    {
        try
        {
            var path = FlagPath();
            if (File.Exists(path)) File.Delete(path);
            Invalidate();
        }
        catch { /* ignore */ }
    }

    private static bool ReadActive()
    {
        try
        {
            var path = FlagPath();
            if (!File.Exists(path))
                return false;
            // Soft-lock should not leave yield forever if the process crashed.
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(2))
            {
                try { File.Delete(path); } catch { /* ignore */ }
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FlagPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlphaAITracker",
            "face");
        return Path.Combine(root, "verify_yield");
    }
}
