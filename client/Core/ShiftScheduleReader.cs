using client.Configuration;
using client.Services;
using Microsoft.Data.Sqlite;

namespace client.Core;

/// <summary>
/// Reads the signed-in Windows user's cached shift (<c>employee_schedule</c>) and company
/// holidays from that user's own tracker SQLite. Shared by the face soft-lock (unlock gate)
/// and the tracker's end-of-shift sign-out.
/// </summary>
public static class ShiftScheduleReader
{
    public static ShiftUnlockGate.Schedule? Load(string employeeId, AppConfig config)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = TrackerDeviceTokenReader.CandidatePaths(local, profile, config.DbPath);
            if (!TrackerDeviceTokenReader.TryPickNewest(paths, out _, out _, out var dbPath, out _)
                || string.IsNullOrWhiteSpace(dbPath))
                return null;

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            string? tz, pattern;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT timezone, weekly_pattern FROM employee_schedule WHERE employee_id = $id LIMIT 1";
                cmd.Parameters.AddWithValue("$id", employeeId);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                tz = r.IsDBNull(0) ? null : r.GetString(0);
                pattern = r.IsDBNull(1) ? null : r.GetString(1);
            }

            var holidays = new HashSet<string>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT holiday_date FROM company_holidays";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0)) continue;
                    var d = r.GetString(0).Trim();
                    holidays.Add(d.Length > 10 ? d[..10] : d);
                }
            }
            return new ShiftUnlockGate.Schedule(tz ?? "", ShiftUnlockGate.ParsePattern(pattern), holidays);
        }
        catch
        {
            return null;
        }
    }
}
