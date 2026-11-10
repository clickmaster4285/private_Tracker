using System.Text.Json;
using System.Text.Json.Serialization;

namespace client.Services.TimeDateWarning;

/// <summary>
/// Handoff payload between the headless tracker (<see cref="TimeDateWarningWorker"/>)
/// and the <c>--timedatewarning</c> Avalonia subprocess. Lives under LocalAppData /
/// ~/.local/share — never under the install dir.
/// </summary>
public sealed class TimeDateWarningState
{
    public string CheckedAt { get; set; } = "";
    public string? ServerUtc { get; set; }
    public string? LocalUtc { get; set; }
    public double SkewSeconds { get; set; }
    public string? OsTimeZoneId { get; set; }
    public string? ExpectedTimeZoneId { get; set; }
    public string? ExpectedLocalNow { get; set; }
    public string? OsLocalNow { get; set; }
    public string? LocationSource { get; set; }
    public string[] Reasons { get; set; } = Array.Empty<string>();
    /// <summary>Set by the warning subprocess on Dismiss / window close.</summary>
    public string? DismissedAt { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DataDirectory
    {
        get
        {
            var root = OperatingSystem.IsWindows()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AlphaAITracker")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "alpha-ai-tracker");
            return Path.Combine(root, "timedatewarning");
        }
    }

    public static string StatePath => Path.Combine(DataDirectory, "state.json");

    public static string MutexName => $@"Global\{client.Core.AppInfo.AppMutex}-timedatewarning";

    public static void Write(TimeDateWarningState state)
    {
        Directory.CreateDirectory(DataDirectory);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, StatePath, overwrite: true);
    }

    public static TimeDateWarningState? TryRead()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<TimeDateWarningState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void MarkDismissed()
    {
        var state = TryRead() ?? new TimeDateWarningState();
        state.DismissedAt = DateTime.UtcNow.ToString("O");
        Write(state);
    }

    /// <summary>
    /// True when another <c>--timedatewarning</c> process already holds the sibling mutex.
    /// </summary>
    public static bool IsWarningProcessRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var existing))
            {
                existing.Dispose();
                return true;
            }
            return false;
        }
        catch
        {
            // Contested / abandoned — treat as running to avoid a second window storm.
            return true;
        }
    }
}
