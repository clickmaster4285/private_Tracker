using System.Security.Cryptography;
using client.Core;

namespace client.Configuration;

public static class EnvLoader
{
    private static string? _userConfigDir;

    /// <summary>
    /// Get the platform-specific user config directory where config.enc lives.
    /// This directory is ALWAYS user-writable, unlike the app install directory.
    /// Also sets the fallback machine-id directory on EncryptedConfigService.
    /// </summary>
    public static string UserConfigDir
    {
        get
        {
            if (_userConfigDir != null) return _userConfigDir;

            string baseDir;
            if (OperatingSystem.IsWindows())
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                _userConfigDir = Path.Combine(baseDir, "AlphaAITracker");
            }
            else if (OperatingSystem.IsMacOS())
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                _userConfigDir = Path.Combine(baseDir, "Library", "Application Support", "AlphaAITracker");
            }
            else // Linux
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (!string.IsNullOrEmpty(xdg))
                {
                    _userConfigDir = Path.Combine(xdg, "alpha-ai-tracker");
                }
                else
                {
                    baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    _userConfigDir = Path.Combine(baseDir, ".config", "alpha-ai-tracker");
                }
            }

            Directory.CreateDirectory(_userConfigDir);

            // Set fallback machine-id dir for EncryptedConfigService
            EncryptedConfigService.FallbackMachineIdDir = _userConfigDir;

            return _userConfigDir;
        }
    }

    /// <summary>
    /// Path to config.enc in the user config directory.
    /// </summary>
    public static string UserConfigPath => Path.Combine(UserConfigDir, "config.enc");

    /// <summary>
    /// Load environment variables. Priority order:
    ///   DEV (source-tree build, i.e. `dotnet run`):
    ///     1. Plaintext .env next to / above the assembly (authoritative — edits take
    ///        effect immediately; a stale user config.enc must NOT shadow it)
    ///   INSTALLED / PRODUCTION:
    ///     1. User config directory: ~/.config/alpha-ai-tracker/config.enc (Linux)
    ///                                %APPDATA%\AlphaAITracker\config.enc (Windows)
    ///                                ~/Library/Application Support/…/config.enc (macOS)
    ///     2. Next to the binary: {AppDir}/config.enc (install-time placement)
    ///     3. Plaintext .env (development fallback only)
    ///
    /// Upgrade propagation: every installer bakes the current .env → config.enc at
    /// build time. If the copy next to the binary contains DIFFERENT values than the
    /// user-config copy, the freshly shipped copy REPLACES the (possibly stale,
    /// machine-key-encrypted) user copy before loading. This is what makes a rebuilt
    /// installer actually take effect on a machine that already ran an older build —
    /// otherwise the old user copy would shadow every future config forever.
    /// </summary>
    public static void Load(string? customPath = null)
    {
        // ─── Phase 1: Explicit path (--config-enc CLI mode) ───
        if (customPath != null)
        {
            if (File.Exists(customPath))
            {
                if (customPath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
                    LoadFromEncrypted(customPath);
                else
                    LoadFromPlaintext(customPath);
            }
            return;
        }

        // ─── Phase 2: Development builds (dotnet run / dotnet build) ───
        // The plaintext .env in the source tree is the source of truth. It is resolved
        // BEFORE any config.enc so a stale encrypted copy in the user config dir cannot
        // override fresh edits. Installed builds never reach this branch because their
        // assembly dir (e.g. /usr/share/alpha-ai-tracker) is outside the source tree and
        // contains no .env.
        var devEnvPath = ResolvePlaintextPath();
        if (devEnvPath != null && IsSourceTreeBuild())
        {
            LoadFromPlaintext(devEnvPath);
            return;
        }

        // ─── Phase 3: Installed / production config.enc ───
        // The user-config copy is the primary location, but it must NEVER shadow a
        // config freshly shipped with this build. If the copy next to the binary
        // holds different values than the user copy, the shipped config wins — this
        // is the fix for "installed app still uses the old server URL / API key"
        // after rebuilding the installer (dotnet run reads .env so it always looked
        // fresh, while the installed build kept loading the old machine-key copy).
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var appConfigEnc = Path.Combine(appDir, "config.enc");

        if (File.Exists(appConfigEnc) && File.Exists(UserConfigPath)
            && ShippedConfigDiffers(appConfigEnc, UserConfigPath))
        {
            // A new config was baked into this build — replace the stale user copy.
            // If the user dir is not writable (rare; it was just created above), load
            // the shipped copy directly instead — it sits next to the binary and is
            // readable even in root-owned install dirs (/usr/share/…, Program Files).
            try
            {
                File.Copy(appConfigEnc, UserConfigPath, overwrite: true);
                System.Console.WriteLine(
                    "[EnvLoader] Replaced stale user config.enc with the freshly shipped one");
                LoadFromEncrypted(UserConfigPath);
                return;
            }
            catch
            {
                LoadFromEncrypted(appConfigEnc);
                return;
            }
        }

        // Try user config directory first (user-writable, primary location)
        if (File.Exists(UserConfigPath))
        {
            try
            {
                LoadFromEncrypted(UserConfigPath);
                return;
            }
            catch (CryptographicException)
            {
                // User copy is corrupt / undecryptable — self-heal from the shipped
                // copy rather than crashing the app.
                if (File.Exists(appConfigEnc))
                {
                    try { File.Copy(appConfigEnc, UserConfigPath, overwrite: true); } catch { }
                    if (File.Exists(UserConfigPath))
                    {
                        LoadFromEncrypted(UserConfigPath);
                        return;
                    }
                }
                throw;
            }
        }

        // Fallback: check next to the binary (install-time placement)
        if (File.Exists(appConfigEnc))
        {
            // First launch on this machine: copy from app dir to user config
            try
            {
                File.Copy(appConfigEnc, UserConfigPath, overwrite: false);
            }
            catch
            {
                // Can't copy (e.g., app dir is read-only source) — use app dir directly
            }
            if (File.Exists(UserConfigPath))
            {
                LoadFromEncrypted(UserConfigPath);
                return;
            }
            LoadFromEncrypted(appConfigEnc);
            return;
        }

        // ─── Phase 4: Last-resort plaintext .env (dev tree, no config.enc anywhere) ───
        if (devEnvPath != null && File.Exists(devEnvPath))
        {
            LoadFromPlaintext(devEnvPath);
        }
    }

    /// <summary>
    /// True when the assembly is running from a source-tree build output (bin/Debug or
    /// bin/Release). Installed builds live in OS install dirs (Program Files, /usr/share,
    /// /Applications) and are never considered a source-tree build, so they always use
    /// config.enc.
    /// </summary>
    private static bool IsSourceTreeBuild()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        return dir.Contains($"bin{Path.DirectorySeparatorChar}Debug", StringComparison.OrdinalIgnoreCase)
            || dir.Contains($"bin{Path.DirectorySeparatorChar}Release", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the config.enc shipped next to the binary decrypts to different
    /// values than the user-config copy — i.e. an updated build was installed and
    /// its freshly baked config has not reached the user config dir yet.
    /// Comparison is on decrypted CONTENT (not mtime), so machine clocks and the
    /// machine-key re-encryption of the user copy can never cause a false positive.
    /// </summary>
    private static bool ShippedConfigDiffers(string shippedPath, string userPath)
    {
        try
        {
            var shipped = EncryptedConfigService.DecryptWithFallback(File.ReadAllBytes(shippedPath));
            var user = EncryptedConfigService.DecryptWithFallback(File.ReadAllBytes(userPath));
            return !string.Equals(shipped.plaintext, user.plaintext, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            // Any decrypt failure (corrupt file, key mismatch) — don't clobber the
            // user copy on a guess; keep the previous behavior, but make the problem
            // diagnosable (e.g. a bad build artifact would otherwise silently keep
            // the old config forever).
            System.Console.Error.WriteLine(
                $"[EnvLoader] Warning: could not compare shipped config.enc ({shippedPath}) " +
                $"with user copy ({userPath}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Load and decrypt config.enc.
    /// On first launch (transport key succeeds): automatically migrates to machine-derived key.
    /// </summary>
    private static void LoadFromEncrypted(string encPath)
    {
        var encryptedData = File.ReadAllBytes(encPath);
        var (plaintext, usedMachineKey) = EncryptedConfigService.DecryptWithFallback(encryptedData);

        // If transport key was used (first launch on this machine), migrate to machine key
        if (!usedMachineKey)
        {
            MigrateToMachineKey(plaintext, encPath);
        }

        System.Console.WriteLine($"[EnvLoader] Loaded encrypted config from {encPath}");
        // Parse and set environment variables
        ParseAndSet(plaintext);
    }

    /// <summary>
    /// Re-encrypt config.enc with machine-derived key (always in user config dir).
    /// Securely wipes any leftover plaintext .env files.
    /// </summary>
    private static void MigrateToMachineKey(string plaintext, string encPath)
    {
        try
        {
            var machineKey = EncryptedConfigService.GetMachineKey();
            var reEncrypted = EncryptedConfigService.Encrypt(plaintext, machineKey);

            // Write to user config directory (always user-writable)
            var targetDir = UserConfigDir;
            var targetPath = Path.Combine(targetDir, "config.enc");

            // Atomic replace: write temp, then move (atomic on same volume)
            var tempPath = Path.Combine(targetDir, ".config.enc.tmp");
            File.WriteAllBytes(tempPath, reEncrypted);
            File.Move(tempPath, targetPath, overwrite: true);

            // If source was in a different location (e.g., app dir), delete it
            if (!string.Equals(encPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(encPath); } catch { }
            }

            // Secure-wipe any leftover plaintext .env
            SecureWipePlaintext(targetDir);
            var appDir = Path.GetDirectoryName(encPath);
            if (appDir != null && !string.Equals(appDir, targetDir, StringComparison.OrdinalIgnoreCase))
            {
                SecureWipePlaintext(appDir);
            }
        }
        catch (Exception ex)
        {
            // Log but don't crash — env vars are already in memory
            System.Console.Error.WriteLine(
                $"[EnvLoader] Warning: failed to migrate config to machine key: {ex.Message}");
        }
    }

    /// <summary>
    /// Securely overwrite and delete any .env file in the given directory.
    /// </summary>
    private static void SecureWipePlaintext(string dir)
    {
        try
        {
            var envPath = Path.Combine(dir, ".env");
            if (!File.Exists(envPath)) return;

            var fileInfo = new FileInfo(envPath);
            var length = fileInfo.Length;
            if (length > 0)
            {
                using var fs = new FileStream(envPath, FileMode.Open, FileAccess.Write);
                var buffer = new byte[Math.Min(length, 4096)];
                for (var pass = 0; pass < 3; pass++)
                {
                    fs.Seek(0, SeekOrigin.Begin);
                    RandomNumberGenerator.Fill(buffer);
                    var remaining = length;
                    while (remaining > 0)
                    {
                        var toWrite = (int)Math.Min(buffer.Length, remaining);
                        fs.Write(buffer, 0, toWrite);
                        remaining -= toWrite;
                    }
                    fs.Flush();
                }
            }

            File.Delete(envPath);
        }
        catch
        {
            // Best-effort — at least try regular delete
            try { if (File.Exists(Path.Combine(dir, ".env"))) File.Delete(Path.Combine(dir, ".env")); } catch { }
        }
    }

    /// <summary>
    /// Load plaintext .env file (development mode only).
    /// </summary>
    private static void LoadFromPlaintext(string envPath)
    {
        var content = File.ReadAllText(envPath);

        System.Console.WriteLine($"[EnvLoader] Loaded plaintext .env from {envPath}");

        if (!content.Contains("ALPHA_SERVER_URL", StringComparison.OrdinalIgnoreCase))
        {
            System.Console.Error.WriteLine(
                "[EnvLoader] Warning: .env does not contain ALPHA_SERVER_URL. " +
                "Login will use default http://localhost:8080");
        }

        ParseAndSet(content);
    }

    /// <summary>
    /// Parse key=value lines and set them as environment variables.
    /// </summary>
    private static void ParseAndSet(string content)
    {
        if (string.IsNullOrEmpty(content)) return;

        foreach (var rawLine in content.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eqIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex <= 0) continue;

            var key = line[..eqIndex].Trim();
            var value = line[(eqIndex + 1)..].Trim();

            // Strip surrounding quotes if present
            if (value.Length >= 2 &&
                ((value.StartsWith('\"') && value.EndsWith('\"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            if (!string.IsNullOrWhiteSpace(key))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    /// <summary>
    /// Encrypt a plaintext .env file to config.enc using the transport key.
    /// Used by the --encrypt-config CLI mode during build.
    /// </summary>
    public static void EncryptToFile(string inputPath, string outputPath)
    {
        var plaintext = File.ReadAllText(inputPath);
        var encrypted = EncryptedConfigService.Encrypt(plaintext, EncryptedConfigService.TransportKey);
        File.WriteAllBytes(outputPath, encrypted);
    }

    // ────────────────────────────────────────────────
    //  Plaintext .env resolution (development only)
    // ────────────────────────────────────────────────

    private static string? ResolvePlaintextPath()
    {
        // Walk up from assembly directory to find .env in project root (dotnet run dev mode)
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (var i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, ".env");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        return null;
    }
}
