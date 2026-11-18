using Microsoft.Data.Sqlite;

namespace AlphaAITracker.Service;

/// <summary>
/// LocalSystem cannot use the interactive user's usual APIs, but it can read
/// <c>%LocalAppData%\…\data\*.db</c> under each profile. Prefer this when
/// <c>device.auth</c> is missing or stale (401 on policy / antivirus sync).
/// </summary>
internal static class TrackerTokenProbe
{
    /// <summary>
    /// Newest non-empty <c>employee_info.device_token</c> across candidate tracker DBs.
    /// </summary>
    public static bool TryReadNewest(out string token, out string sourcePath)
    {
        token = "";
        sourcePath = "";
        DateTime bestWrite = DateTime.MinValue;

        foreach (var dbPath in CandidateDbPaths())
        {
            if (!File.Exists(dbPath)) continue;
            try
            {
                var write = File.GetLastWriteTimeUtc(dbPath);
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT device_token FROM employee_info WHERE device_token IS NOT NULL AND length(device_token) > 0 LIMIT 1";
                var tok = (cmd.ExecuteScalar() as string)?.Trim() ?? "";
                if (tok.Length == 0) continue;
                if (string.IsNullOrEmpty(token) || write >= bestWrite)
                {
                    token = tok;
                    sourcePath = dbPath;
                    bestWrite = write;
                }
            }
            catch
            {
                /* try next profile / db */
            }
        }

        return token.Length > 0;
    }

    private static IEnumerable<string> CandidateDbPaths()
    {
        var configured = Environment.GetEnvironmentVariable("ALPHA_DB_PATH");
        var dbNames = new List<string> { "aat_verify.db", "alpha_tracker.db" };
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var name = Path.GetFileName(configured.Trim().Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(name) && !dbNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                dbNames.Insert(0, name);
            if (Path.IsPathRooted(configured) && File.Exists(configured))
                yield return configured;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = ServiceIdentity.DataFolderName;
        var linuxPkg = ServiceIdentity.PackageName;
        foreach (var name in dbNames)
        {
            yield return Path.Combine(local, folder, "data", name);
            if (OperatingSystem.IsLinux())
                yield return Path.Combine(local, linuxPkg, "data", name);
        }

        if (OperatingSystem.IsLinux())
        {
            if (Directory.Exists("/home"))
            {
                foreach (var profile in Directory.EnumerateDirectories("/home"))
                {
                    foreach (var name in dbNames)
                    {
                        yield return Path.Combine(profile, ".local", "share", linuxPkg, "data", name);
                        yield return Path.Combine(profile, ".local", "share", folder, "data", name);
                    }
                }
            }
            yield break;
        }

        string usersRoot;
        try
        {
            usersRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".."));
        }
        catch
        {
            usersRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users");
        }

        if (!Directory.Exists(usersRoot)) yield break;

        foreach (var profile in Directory.EnumerateDirectories(usersRoot))
        {
            var leaf = Path.GetFileName(profile);
            if (leaf is "Public" or "Default" or "Default User" or "All Users") continue;
            foreach (var name in dbNames)
                yield return Path.Combine(profile, "AppData", "Local", folder, "data", name);
        }
    }
}
