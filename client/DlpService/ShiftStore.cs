using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// The PC's single store of employee shifts, owned and written only by this service.
/// One file per employee under <c>%ProgramData%\…\dlp\shifts\</c> holding the last
/// <c>GET /schedules/me</c> answer and when it was downloaded. It survives restarts and
/// server outages, so Windows logon hours, the face lock and the end-of-shift check (both read
/// it through the pipe op <c>GetShift</c>) all follow the newest shift the PC ever received.
/// </summary>
internal static class ShiftStore
{
    internal sealed record Entry(string EmployeeId, DateTimeOffset FetchedAt, bool NoShift, string? ScheduleJson);

    private sealed class FileShape
    {
        public string EmployeeId { get; set; } = "";
        public DateTimeOffset FetchedAt { get; set; }
        public bool NoShift { get; set; }
        public string? ScheduleJson { get; set; }
    }

    private static readonly object Gate = new();

    /// <summary>Overridable for tests.</summary>
    internal static string Directory { get; set; } = Path.Combine(DlpDataPaths.DlpRoot, "shifts");

    /// <summary>Save the server's answer: raw schedule JSON, or <c>null</c> = no shift assigned.</summary>
    public static void Save(string employeeId, string? scheduleJson, DateTimeOffset? fetchedAt = null)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return;
        var shape = new FileShape
        {
            EmployeeId = employeeId,
            FetchedAt = fetchedAt ?? DateTimeOffset.UtcNow,
            NoShift = scheduleJson == null,
            ScheduleJson = scheduleJson,
        };
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = PathFor(employeeId);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(shape));
                File.Move(tmp, path, overwrite: true);
            }
            catch { /* next download retries */ }
        }
    }

    public static Entry? Load(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return null;
        lock (Gate)
        {
            try
            {
                var path = PathFor(employeeId);
                if (!File.Exists(path)) return null;
                var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path));
                if (shape == null || !string.Equals(shape.EmployeeId, employeeId, StringComparison.Ordinal))
                    return null;
                return new Entry(shape.EmployeeId, shape.FetchedAt, shape.NoShift, shape.ScheduleJson);
            }
            catch
            {
                return null; // corrupt → treated as "nothing stored"
            }
        }
    }

    private static string PathFor(string employeeId)
    {
        var safe = new string(employeeId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return Path.Combine(Directory, safe + ".json");
    }
}
