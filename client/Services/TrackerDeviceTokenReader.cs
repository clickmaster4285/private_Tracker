using Microsoft.Data.Sqlite;
using client.Core;

namespace client.Services;

/// <summary>
/// Reads the interactive user's tracker SQLite for a device token.
/// Does not scan other Windows profiles. An empty token is not a result.
/// </summary>
public static class TrackerDeviceTokenReader
{
    public static IReadOnlyList<string> CandidatePaths(
        string localAppData, string userProfile, string? configuredDbPath)
    {
        var list = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }
            if (!IsUnderCurrentUser(full, localAppData, userProfile)) return;
            if (list.Contains(full, PathComparer)) return;
            list.Add(full);
        }

        // Windows: %LocalAppData%\AlphaAITracker (APP_MUTEX / DataFolderName).
        // Linux/macOS: ~/.local/share/alpha-ai-tracker (PACKAGE_NAME) — same as
        // Program.ResolveDbPath. Also probe DataFolderName as a legacy fallback.
        var dataFolders = OperatingSystem.IsWindows()
            ? new[] { AppInfo.DataFolderName }
            : new[] { AppInfo.PackageName, AppInfo.DataFolderName };

        if (!string.IsNullOrWhiteSpace(configuredDbPath))
        {
            var configured = configuredDbPath.Trim();
            if (!Path.IsPathRooted(configured))
            {
                configured = Path.Combine(localAppData, dataFolders[0], configured);
            }
            Add(configured);
        }

        foreach (var folder in dataFolders)
        {
            var dataDir = Path.Combine(localAppData, folder, "data");
            Add(Path.Combine(dataDir, "aat_verify.db"));
            Add(Path.Combine(dataDir, "alpha_tracker.db"));
        }

        // Defense in depth when LocalApplicationData is not ~/.local/share.
        if (!OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(userProfile))
        {
            var xdgData = Path.Combine(userProfile, ".local", "share", AppInfo.PackageName, "data");
            Add(Path.Combine(xdgData, "aat_verify.db"));
            Add(Path.Combine(xdgData, "alpha_tracker.db"));
        }

        return list;
    }

    public static bool IsUnderCurrentUser(string fullPath, string localAppData, string userProfile)
    {
        var full = Path.GetFullPath(fullPath);
        if (StartsWithDir(full, localAppData)) return true;
        if (StartsWithDir(full, userProfile)) return true;
        return false;
    }

    /// <summary>
    /// Opens each existing candidate read-only. Returns the newest file that has a
    /// non-empty <c>device_token</c>. Missing, locked, and corrupt files are named in
    /// <paramref name="diagnostic"/> and skipped.
    /// </summary>
    public static bool TryPickNewest(
        IEnumerable<string> paths,
        out string token,
        out string? employeeId,
        out string? sourcePath,
        out string diagnostic)
    {
        token = "";
        employeeId = null;
        sourcePath = null;
        var notes = new List<string>();
        DateTime bestWrite = DateTime.MinValue;

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                notes.Add(path + " missing");
                continue;
            }

            if (!TryRead(path, out var found, out var emp, out var error))
            {
                notes.Add(path + ": " + error);
                continue;
            }

            if (string.IsNullOrWhiteSpace(found))
            {
                notes.Add(path + " has no device token");
                continue;
            }

            var write = File.GetLastWriteTimeUtc(path);
            if (sourcePath != null && write <= bestWrite)
                continue;

            token = found.Trim();
            employeeId = emp;
            sourcePath = path;
            bestWrite = write;
        }

        diagnostic = string.Join("; ", notes);
        return !string.IsNullOrWhiteSpace(token);
    }

    public static bool TryRead(string dbPath, out string? token, out string? employeeId, out string error)
    {
        token = null;
        employeeId = null;
        error = "";
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT device_token, employee_id FROM employee_info " +
                "WHERE device_token IS NOT NULL AND length(device_token) > 0 LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                error = "no device token row";
                return false;
            }

            token = reader.IsDBNull(0) ? null : reader.GetString(0);
            employeeId = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(token))
            {
                error = "empty device token";
                token = null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            token = null;
            return false;
        }
    }

    private static bool StartsWithDir(string fullPath, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        string root;
        try { root = Path.GetFullPath(dir); }
        catch { return false; }
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, PathComparison)
            || string.Equals(fullPath, Path.GetFullPath(dir), PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
