namespace client.Core;

/// <summary>
/// When the Windows silent updater (or Linux GUI download) has a newer release
/// in flight, heavy tracker work yields so download/apply get CPU and bandwidth.
/// Flag file: <c>%ProgramData%\&lt;APP_MUTEX&gt;\updates\update_priority</c> (Windows)
/// or LocalAppData <c>updates/update_priority</c> (Linux/macOS GUI path).
/// </summary>
public static class UpdatePriority
{
    private static readonly object Gate = new();
    private static bool _cached;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>True while an upgrade should preempt sync / live / screenshots / collectors.</summary>
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

    /// <summary>Force the next <see cref="ShouldYield"/> read to hit disk.</summary>
    public static void Invalidate()
    {
        lock (Gate) _cachedAt = DateTime.MinValue;
    }

    public static void SetLocal(string version, string reason)
    {
        try
        {
            var path = PriorityFlagPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{DateTime.UtcNow:o}\n{version.Trim()}\n{reason.Trim()}\n");
            Invalidate();
        }
        catch { /* ignore */ }
    }

    public static void ClearLocal()
    {
        try
        {
            var path = PriorityFlagPath();
            if (File.Exists(path)) File.Delete(path);
            Invalidate();
        }
        catch { /* ignore */ }
    }

    private static bool ReadActive()
    {
        try
        {
            // In-progress apply always wins (Windows service flag).
            var inProgress = Path.Combine(UpdatesRoot(), "update_in_progress");
            if (File.Exists(inProgress))
                return true;

            var priority = PriorityFlagPath();
            if (!File.Exists(priority))
                return false;

            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(priority) > TimeSpan.FromHours(2))
            {
                try { File.Delete(priority); } catch { /* ignore */ }
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string UpdatesRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, AppInfo.DataFolderName, "updates");
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, AppInfo.DataFolderName, "updates");
    }

    private static string PriorityFlagPath() => Path.Combine(UpdatesRoot(), "update_priority");
}
