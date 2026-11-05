using Microsoft.Extensions.Logging;
using System.Text;

namespace client.Services;

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string path)
    {
        builder.AddProvider(new FileLoggerProvider(path));
        return builder;
    }
}

public class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string path)
    {
        // NEVER throw from this constructor. The installed .deb lives in a
        // root-owned, read-only dir (/usr/share/alpha-ai-tracker); opening a
        // log file there used to throw UnauthorizedAccessException and abort
        // the whole app before the GUI opened ("open and instantly close").
        // Program.cs now resolves a writable path, but this stays defensive:
        // try the requested path → user data dir → temp dir → null sink.
        _writer = OpenWriter(path);
    }

    private static StreamWriter OpenWriter(string requestedPath)
    {
        foreach (var candidate in CandidatePaths(requestedPath))
        {
            try
            {
                var dir = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Each process start replaces the previous run. The file used to
                // append forever (LocalAppData\dotnetrunlog.txt grew across days
                // of restarts). FileShare.Read keeps a second process from
                // truncating a log this process still has open.
                var stream = new FileStream(candidate, FileMode.Create, FileAccess.Write, FileShare.Read);
                return new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            }
            catch
            {
                // Path not writable (read-only install dir, permission denied,
                // disk full, ...) — try the next candidate.
            }
        }

        // Last resort: a no-op sink so logging never throws and never crashes
        // app startup. Surface this to stderr so it is at least visible.
        try
        {
            Console.Error.WriteLine(
                $"[FileLoggerProvider] Could not open any log file for writing (requested: {requestedPath}). "
                + "Logging to a null sink.");
        }
        catch { }
        return StreamWriter.Null;
    }

    private static IEnumerable<string> CandidatePaths(string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            yield return requested;

        // User-writable data dir (same place the SQLite db / native-messaging
        // socket live) — writable on every platform regardless of install dir.
        string? userData = null;
        try
        {
            userData = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlphaAITracker")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "alpha-ai-tracker");
        }
        catch { }
        if (!string.IsNullOrEmpty(userData))
            yield return Path.Combine(userData, "dotnetrunlog.txt");

        // System temp dir — always writable as a last resort.
        string? tempLog = null;
        try { tempLog = Path.Combine(Path.GetTempPath(), "alpha-ai-tracker.log"); }
        catch { }
        if (!string.IsNullOrEmpty(tempLog))
            yield return tempLog;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _lock);

    public void Dispose()
    {
        try { _writer.Flush(); } catch { }
        try { _writer.Dispose(); } catch { }
    }
}

public class FileLogger(string categoryName, StreamWriter writer, object lockObj) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var msg = formatter(state, exception);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var level = logLevel.ToString().PadRight(11);
        var line = $"[{timestamp}] [{level}] [{categoryName}] {msg}";

        if (exception != null)
            line += $"\n{exception}";

        lock (lockObj)
        {
            writer.WriteLine(line);
        }
    }
}
