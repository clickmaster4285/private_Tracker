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
        ("ALPHA_DLP_PORT_BLOCK", "true"),
        ("ALPHA_DLP_DELETE_BLOCK", "true"),
        ("ALPHA_DLP_FILE_TRANSFER_BLOCK", "true"),
        ("ALPHA_DLP_LOGIN_REBOOT", "true"),
        ("ALPHA_DLP_LOGIN_REBOOT_DELAY_SEC", "5"),
        ("ALPHA_DLP_POLICY_POLL_SEC", "300"),
        ("ALPHA_DLP_ENFORCE_TICK_SEC", "60"),
        ("ALPHA_DLP_APP_SWEEP_SEC", "120"),
        ("ALPHA_DLP_DNS_PIN_SEC", "10"),
        ("ALPHA_DLP_TCP_KILL_SEC", "30"),
        ("ALPHA_DLP_PORT_GUARD_SEC", "15"),
        ("ALPHA_DLP_DELETE_RECHECK_SEC", "600"),
        ("ALPHA_DLP_ALERT_SYNC_SEC", "60"),
        ("ALPHA_DLP_WATCHDOG", "true"),
        ("ALPHA_DLP_WATCHDOG_POLL_SEC", "8"),
        ("ALPHA_AV_ENFORCE", "true"),
        ("ALPHA_AV_GUARD_SEC", "30"),
        ("ALPHA_AV_STATUS_SEC", "300"),
        ("ALPHA_AV_QUICK_SCAN_HOURS", "24"),
        ("ALPHA_UPDATE_ENABLED", "true"),
        ("ALPHA_UPDATE_AUTO_INSTALL", "true"),
        ("ALPHA_UPDATE_CHECK_INTERVAL_SEC", "60"),
        ("ALPHA_UPDATE_DOWNLOAD_JITTER_SEC", "3600"),
        ("ALPHA_DLP_PRODUCTION_PROFILE", "true"),
        ("ALPHA_DLP_APPLOCKER_INTERIM_OK", "true"),
    ];

    public static void Apply()
    {
        EnsureShippedServiceEnvDefaults();
        LoadServiceEnv(overwrite: false);
        LoadDotEnvNearBinary();
        NormalizeServerUrlInProcessAndServiceEnv();
        TryFillFromDeviceAuthFile();
        TryFillDeviceTokenFromTrackerDb();
        // Re-normalize after credential fill — device.auth may still carry https://.
        NormalizeServerUrlInProcessAndServiceEnv();
    }

    /// <summary>
    /// Normalize and seed <c>ALPHA_SERVER_URL</c> into <c>service.env</c> so
    /// lockdown can carve out the API before DeviceAuth exists (fail-closed).
    /// Preserves <c>https://</c> for nginx TLS — never downgrades to HTTP.
    /// </summary>
    private static void NormalizeServerUrlInProcessAndServiceEnv()
    {
        var raw = Environment.GetEnvironmentVariable("ALPHA_SERVER_URL") ?? "";
        var normalized = ServerUrlNormalizer.Normalize(raw);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (!string.Equals(raw.Trim().TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("ALPHA_SERVER_URL", normalized);
            Console.WriteLine(
                $"[DlpService] ALPHA_SERVER_URL normalized: {normalized}");
        }
        else
        {
            Environment.SetEnvironmentVariable("ALPHA_SERVER_URL", normalized);
        }

        // Seed once; overwrite only when healing blank / unsupported scheme.
        var fileNeedsRewrite = false;
        try
        {
            var path = Path.Combine(DeviceCredentialStore.DefaultDir, "service.env");
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("ALPHA_SERVER_URL=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var existing = t["ALPHA_SERVER_URL=".Length..].Trim().Trim('"').Trim('\'');
                    fileNeedsRewrite = ServerUrlNormalizer.NeedsRewrite(existing);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DlpService] Warning: could not read service.env URL: {ex.Message}");
        }

        _ = EnsureKey(
            "ALPHA_SERVER_URL",
            normalized,
            overwrite: ServerUrlNormalizer.NeedsRewrite(raw) || fileNeedsRewrite);
    }

    /// <summary>
    /// After a new policy version is saved: merge missing ship defaults into
    /// <c>service.env</c> and reload. Does <b>not</b> overwrite local kill-switches from a
    /// nearby bake <c>.env</c> — that used to force <c>ALPHA_DLP_NETWORK_LOCKDOWN=false</c>
    /// onto installed PCs and clear firewall lockdown even when the web turned internet block on.
    /// </summary>
    public static IReadOnlyList<string> SyncServiceEnvAfterPolicyApply()
    {
        var changed = new List<string>();
        changed.AddRange(EnsureShippedServiceEnvDefaults());
        // Older installs omitted these keys; heal to ship defaults so published policy can apply.
        changed.AddRange(EnsureKey("ALPHA_DLP_PORT_BLOCK", "true", overwrite: true));
        changed.AddRange(EnsureKey("ALPHA_DLP_NETWORK_LOCKDOWN", "true", overwrite: true));
        LoadServiceEnv(overwrite: true);
        return changed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> EnsureKey(string key, string value, bool overwrite)
    {
        try
        {
            var path = Path.Combine(DeviceCredentialStore.DefaultDir, "service.env");
            Directory.CreateDirectory(DeviceCredentialStore.DefaultDir);
            UpsertEnvFile(path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value },
                overwriteExisting: overwrite, out var written);
            return written;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DlpService] Warning: could not ensure {key}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> UpsertKeysFromNearbyDotEnv(IReadOnlyList<string> keys)
    {
        try
        {
            string? dotEnvPath = null;
            foreach (var p in CandidateEnvPaths())
            {
                if (!File.Exists(p)) continue;
                dotEnvPath = p;
                break;
            }
            if (dotEnvPath == null) return Array.Empty<string>();

            var wanted = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(dotEnvPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line[..eq].Trim();
                if (!wanted.Contains(k)) continue;
                var v = line[(eq + 1)..].Trim().Trim('"').Trim('\'');
                values[k] = v;
            }
            if (values.Count == 0) return Array.Empty<string>();

            var path = Path.Combine(DeviceCredentialStore.DefaultDir, "service.env");
            UpsertEnvFile(path, values, overwriteExisting: true, out var written);
            if (written.Count > 0)
            {
                Console.WriteLine(
                    $"[DlpService] Upserted {written.Count} key(s) into service.env from {dotEnvPath}: {string.Join(", ", written)}");
            }
            return written;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DlpService] Warning: could not upsert from .env: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static void UpsertEnvFile(
        string path,
        IReadOnlyDictionary<string, string> values,
        bool overwriteExisting,
        out List<string> written)
    {
        written = new List<string>();
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.TrimEnd('\r');
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    lines.Add(line);
                    continue;
                }
                var eq = trimmed.IndexOf('=');
                if (eq <= 0)
                {
                    lines.Add(line);
                    continue;
                }
                var key = trimmed[..eq].Trim();
                if (values.TryGetValue(key, out var newVal))
                {
                    seen.Add(key);
                    if (overwriteExisting)
                    {
                        lines.Add($"{key}={newVal}");
                        written.Add(key);
                    }
                    else
                    {
                        lines.Add(line);
                    }
                }
                else
                {
                    lines.Add(line);
                }
            }
        }
        foreach (var (key, val) in values)
        {
            if (seen.Contains(key)) continue;
            lines.Add($"{key}={val}");
            written.Add(key);
        }
        if (written.Count == 0) return;
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
    }

    /// <summary>
    /// Manual/auto update replaces the service exe but used to leave a stale
    /// <c>service.env</c> without new keys (e.g. <c>ALPHA_UPDATE_*</c>).
    /// Append missing ship defaults so new knobs activate
    /// on the next service start without wiping existing values.
    /// Returns the keys that were appended.
    /// </summary>
    private static IReadOnlyList<string> EnsureShippedServiceEnvDefaults()
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

            if (append.Count == 0) return Array.Empty<string>();

            var prefix = existing.Length > 0 && !existing.EndsWith('\n') && !existing.EndsWith("\r\n")
                ? "\r\n"
                : "";
            File.AppendAllText(path, prefix + string.Join("\r\n", append) + "\r\n");
            var keys = append.Select(l => l.Split('=')[0]).ToList();
            Console.WriteLine(
                $"[DlpService] Merged {keys.Count} missing key(s) into {path}: {string.Join(", ", keys)}");
            return keys;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DlpService] Warning: could not merge service.env defaults: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static void LoadServiceEnv(bool overwrite)
    {
        var path = Path.Combine(DeviceCredentialStore.DefaultDir, "service.env");
        if (!File.Exists(path)) return;
        ParseAndSet(File.ReadAllText(path), overwrite);
        Console.WriteLine($"[DlpService] Loaded env from {path} (overwrite={overwrite})");
    }

    private static void LoadDotEnvNearBinary()
    {
        foreach (var path in CandidateEnvPaths())
        {
            if (!File.Exists(path)) continue;
            ParseAndSet(File.ReadAllText(path), overwrite: false);
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

    private static void ParseAndSet(string content, bool overwrite)
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
            if (!overwrite && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                continue;
            Environment.SetEnvironmentVariable(key, val);
        }
    }

    private static void TryFillFromDeviceAuthFile()
    {
        if (!DeviceCredentialStore.TryLoad(out var url, out var tok)) return;

        var normalizedUrl = ServerUrlNormalizer.Normalize(url);
        var currentUrl = Environment.GetEnvironmentVariable("ALPHA_SERVER_URL") ?? "";
        if (!string.IsNullOrWhiteSpace(normalizedUrl) &&
            (string.IsNullOrWhiteSpace(currentUrl) || ServerUrlNormalizer.NeedsRewrite(currentUrl)))
            Environment.SetEnvironmentVariable("ALPHA_SERVER_URL", normalizedUrl);

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

        if (TrackerTokenProbe.TryReadNewest(out var tok, out var dbPath))
        {
            Environment.SetEnvironmentVariable("ALPHA_DEVICE_TOKEN", tok);
            Console.WriteLine(
                $"[DlpService] ALPHA_DEVICE_TOKEN filled from {dbPath} (len={tok.Length}). " +
                "If policy pull returns 401, re-login the tracker.");
            return;
        }

        Console.WriteLine(
            "[DlpService] No device_token yet (device.auth / tracker SQLite). " +
            "Workers will wait until the tracker logs in.");
    }
}
