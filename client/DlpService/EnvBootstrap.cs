using Microsoft.Data.Sqlite;

namespace AlphaAITracker.Service;

/// <summary>
/// Bootstrap: load service.env / nearby .env (does not overwrite already-set vars),
/// then fill ALPHA_DEVICE_TOKEN from device.auth or tracker SQLite (all user profiles
/// when running as LocalSystem).
/// </summary>
internal static class EnvBootstrap
{
    /// <summary>
    /// Ship defaults merged into <c>service.env</c> on upgrade when the key is absent.
    /// Never overwrites an existing value (admin / lab edits win).
    /// </summary>
    private static readonly (string Key, string Value)[] ShippedServiceEnvDefaults =
    [
        ("ALPHA_DLP_ENABLED", "true"),
        ("ALPHA_DLP_USB_BLOCK", "true"),
        ("ALPHA_DLP_WEB_BLOCK", "true"),
        ("ALPHA_DLP_NETWORK_LOCKDOWN", "true"),
        ("ALPHA_DLP_APP_BLOCK", "true"),
        ("ALPHA_DLP_DELETE_BLOCK", "true"),
        ("ALPHA_DLP_LOGIN_REBOOT", "true"),
        ("ALPHA_DLP_POLICY_POLL_SEC", "300"),
        ("ALPHA_DLP_ALERT_SYNC_SEC", "60"),
        ("ALPHA_DLP_WATCHDOG", "true"),
        ("ALPHA_DLP_WATCHDOG_POLL_SEC", "8"),
        ("ALPHA_UPDATE_ENABLED", "true"),
        ("ALPHA_UPDATE_AUTO_INSTALL", "true"),
        ("ALPHA_UPDATE_CHECK_INTERVAL_SEC", "60"),
    ];

    public static void Apply()
    {
        EnsureShippedServiceEnvDefaults();
        LoadServiceEnv();
        LoadDotEnvNearBinary();
        TryFillFromDeviceAuthFile();
        TryFillDeviceTokenFromTrackerDb();
    }

    /// <summary>
    /// Manual/auto update replaces the service exe but used to leave a stale
    /// <c>service.env</c> without new keys (e.g. <c>ALPHA_UPDATE_*</c>).
    /// Append missing ship defaults so new knobs activate
    /// on the next service start without wiping existing values.
    /// </summary>
    private static void EnsureShippedServiceEnvDefaults()
    {
        try
        {
            var dir = DeviceCredentialStore.DefaultDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "service.env");
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in existing.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq > 0)
                    present.Add(line[..eq].Trim());
            }

            var append = new List<string>();
            foreach (var (key, value) in ShippedServiceEnvDefaults)
            {
                if (present.Contains(key)) continue;
                append.Add($"{key}={value}");
            }

            // Pipe name is identity-derived — only seed when missing.
            var pipeKey = "ALPHA_DLP_PIPE_NAME";
            if (!present.Contains(pipeKey))
                append.Add($"{pipeKey}={ServiceIdentity.DlpPipeName}");

            if (append.Count == 0) return;

            var prefix = existing.Length > 0 && !existing.EndsWith('\n') && !existing.EndsWith("\r\n")
                ? "\r\n"
                : "";
            File.AppendAllText(path, prefix + string.Join("\r\n", append) + "\r\n");
            Console.WriteLine(
                $"[DlpService] Merged {append.Count} missing key(s) into {path}: {string.Join(", ", append.Select(l => l.Split('=')[0]))}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DlpService] Warning: could not merge service.env defaults: {ex.Message}");
        }
    }

    private static void LoadServiceEnv()
    {
        var path = Path.Combine(DeviceCredentialStore.DefaultDir, "service.env");
        if (!File.Exists(path)) return;
        ParseAndSet(File.ReadAllText(path));
        Console.WriteLine($"[DlpService] Loaded env from {path}");
    }

    private static void LoadDotEnvNearBinary()
    {
        foreach (var path in CandidateEnvPaths())
        {
            if (!File.Exists(path)) continue;
            ParseAndSet(File.ReadAllText(path));
            Console.WriteLine($"[DlpService] Loaded env from {path}");
            return;
        }
    }

    private static IEnumerable<string> CandidateEnvPaths()
    {
        var baseDir = AppContext.BaseDirectory;
        // Installed: {app}\Service\.env (optional)
        yield return Path.Combine(baseDir, ".env");
        // Lab: bin/Release/net10.0 → DlpService → client
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".env"));
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".env"));
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.Combine(cwd, ".env");
        yield return Path.GetFullPath(Path.Combine(cwd, "..", ".env"));
    }

    private static void ParseAndSet(string content)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (val.Length >= 2 && ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                val = val[1..^1];
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, val);
        }
    }

    private static void TryFillFromDeviceAuthFile()
    {
        if (!DeviceCredentialStore.TryLoad(out var url, out var tok)) return;

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALPHA_SERVER_URL")) &&
            !string.IsNullOrWhiteSpace(url))
            Environment.SetEnvironmentVariable("ALPHA_SERVER_URL", url);

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALPHA_DEVICE_TOKEN")) &&
            !string.IsNullOrWhiteSpace(tok))
        {
            Environment.SetEnvironmentVariable("ALPHA_DEVICE_TOKEN", tok);
            Console.WriteLine(
                $"[DlpService] ALPHA_DEVICE_TOKEN filled from {DeviceCredentialStore.DefaultPath} (len={tok.Length}).");
        }
    }

    private static void TryFillDeviceTokenFromTrackerDb()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALPHA_DEVICE_TOKEN")))
            return;

        foreach (var dbPath in CandidateTrackerDbPaths())
        {
            if (!File.Exists(dbPath)) continue;
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT device_token FROM employee_info WHERE device_token IS NOT NULL AND length(device_token) > 0 LIMIT 1";
                var tok = cmd.ExecuteScalar() as string;
                if (string.IsNullOrWhiteSpace(tok)) continue;

                Environment.SetEnvironmentVariable("ALPHA_DEVICE_TOKEN", tok.Trim());
                Console.WriteLine(
                    $"[DlpService] ALPHA_DEVICE_TOKEN filled from {dbPath} (len={tok.Trim().Length}). " +
                    "If policy pull returns 401, re-login the tracker.");
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DlpService] Warning: could not read {dbPath}: {ex.Message}");
            }
        }

        Console.WriteLine(
            "[DlpService] No device_token yet (device.auth / tracker SQLite). " +
            "Workers will wait until the tracker logs in.");
    }

    private static IEnumerable<string> CandidateTrackerDbPaths()
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

        // Interactive user (dotnet run / double-click as admin).
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
            // Root daemon: scan /home/*/.local/share/<package|mutex>/data/
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

        // LocalSystem: scan every profile under C:\Users\*\AppData\Local\...
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
