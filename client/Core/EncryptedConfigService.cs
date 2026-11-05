using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace client.Core;

/// <summary>
/// Handles AES-256-GCM encryption/decryption of .env config files.
///
/// Key derivation:
/// - Transport key: SHA256("AlphaAITracker:TransportKey:v1") — fixed, hardcoded.
///   Used for build-time encryption. Distributes config.enc safely in installer.
/// - Machine key: SHA256("AlphaAITracker:MachineKey:v1:" + stableMachineId)
///   Used after first launch. Binds config to a specific machine.
///
/// Machine ID resolution order:
///   1. OS-level stable identifier:
///      - Linux:   /etc/machine-id (or /var/lib/dbus/machine-id)
///      - Windows: HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid
///      - macOS:   IOPlatformUUID from ioreg
///   2. If OS-level ID is missing: a persisted .machine-id file
///      (stored in the user config directory, generated on first launch)
///
/// File format: [nonce (12 bytes)][tag (16 bytes)][ciphertext...]
/// Total overhead: 28 bytes.
/// </summary>
public static class EncryptedConfigService
{
    // ─── Key seeds (hardcoded — never in .env, never in config files) ───
    private const string TransportKeySeed = "AlphaAITracker:TransportKey:v1";
    private const string MachineKeyPrefix = "AlphaAITracker:MachineKey:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static byte[]? _transportKey;
    private static byte[]? _machineKey;
    private static string? _fallbackMachineIdDir;

    /// <summary>
    /// Set the directory where the fallback .machine-id file can be stored
    /// when no OS-level machine identifier is available.
    /// This should be set to the user config directory before first use.
    /// </summary>
    public static string? FallbackMachineIdDir
    {
        set => _fallbackMachineIdDir = value;
    }

    /// <summary>
    /// Fixed transport key — same for all builds using this codebase version.
    /// Only used to decrypt config.enc at first launch.
    /// After first launch, config is re-encrypted with machine-derived key.
    /// </summary>
    public static byte[] TransportKey
    {
        get
        {
            _transportKey ??= SHA256.HashData(Encoding.UTF8.GetBytes(TransportKeySeed));
            return _transportKey;
        }
    }

    /// <summary>
    /// Machine-derived key from stable platform identifier.
    /// Returns a 256-bit (32-byte) key.
    /// </summary>
    public static byte[] GetMachineKey()
    {
        if (_machineKey != null) return _machineKey;

        var machineId = GetStableMachineId();
        if (string.IsNullOrEmpty(machineId))
        {
            // Try fallback: generate and persist a machine ID
            machineId = GetOrCreateFallbackMachineId();
        }

        if (string.IsNullOrEmpty(machineId))
        {
            throw new InvalidOperationException(
                "Cannot derive machine key: no stable machine identifier found.\n" +
                "  Linux:   /etc/machine-id required (run: cat /etc/machine-id)\n" +
                "  Windows: MachineGuid registry key required\n" +
                "  macOS:   IOPlatformUUID from ioreg required\n" +
                "  (Also tried fallback .machine-id file)");
        }

        _machineKey = SHA256.HashData(Encoding.UTF8.GetBytes(MachineKeyPrefix + machineId));
        return _machineKey;
    }

    /// <summary>
    /// Reset cached keys (useful for testing or re-detection after OS restore).
    /// </summary>
    public static void ResetCache()
    {
        _machineKey = null;
        _transportKey = null;
    }

    /// <summary>
    /// Encrypt plaintext string to encrypted binary format.
    /// Output: [nonce (12)][tag (16)][ciphertext...]
    /// </summary>
    public static byte[] Encrypt(string plaintext, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != 32)
            throw new ArgumentException("Key must be 32 bytes (256-bit)", nameof(key));

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[NonceSize + TagSize + plainBytes.Length];

        // Copy nonce to output
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);

        // Encrypt using AES-256-GCM
        using var aes = new AesGcm(key, TagSize);
        var plainSpan = plainBytes.AsSpan();
        var ctSpan = output.AsSpan(NonceSize + TagSize);
        var tagSpan = output.AsSpan(NonceSize, TagSize);
        aes.Encrypt(nonce, plainSpan, ctSpan, tagSpan, default /* associatedData */);

        return output;
    }

    /// <summary>
    /// Decrypt binary encrypted config to plaintext string.
    /// Accepts: [nonce (12)][tag (16)][ciphertext...]
    /// </summary>
    public static string Decrypt(byte[] encryptedData, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(encryptedData);
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != 32)
            throw new ArgumentException("Key must be 32 bytes (256-bit)", nameof(key));

        if (encryptedData.Length < NonceSize + TagSize)
            throw new ArgumentException(
                $"Encrypted data too short. Minimum {NonceSize + TagSize} bytes, got {encryptedData.Length}.",
                nameof(encryptedData));

        var nonce = encryptedData.AsSpan(0, NonceSize);
        var tag = encryptedData.AsSpan(NonceSize, TagSize);
        var ciphertext = encryptedData.AsSpan(NonceSize + TagSize);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, default /* associatedData */);

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// Attempt to decrypt with machine key first, then transport key.
    /// Returns (plaintext, usedMachineKey) — use true to indicate a previously-migrated install.
    /// </summary>
    public static (string plaintext, bool usedMachineKey) DecryptWithFallback(byte[] encryptedData)
    {
        // Try machine key first (most common case after first launch)
        try
        {
            return (Decrypt(encryptedData, GetMachineKey()), true);
        }
        catch (CryptographicException)
        {
            // Machine key failed — try transport key (first launch or new machine)
        }
        catch (InvalidOperationException)
        {
            // Machine ID not available even with fallback — try transport key
        }

        // Try transport key
        try
        {
            return (Decrypt(encryptedData, TransportKey), false);
        }
        catch (CryptographicException)
        {
            // Both failed — rethrow with context
        }

        throw new CryptographicException(
            "Failed to decrypt config.enc with any available key.\n" +
            "The config file may be corrupted, from a different machine, " +
            "or the transport key has changed.");
    }

    // ────────────────────────────────────────────────
    //  Machine Identifier
    // ────────────────────────────────────────────────

    /// <summary>
    /// Get a stable machine identifier that persists across reinstalls.
    /// Falls back to persisted .machine-id if OS-level ID is missing.
    /// </summary>
    public static string GetStableMachineId()
    {
        // 1. Try OS-level identifier
        string? id = null;

        if (OperatingSystem.IsLinux())
            id = GetLinuxMachineId();
        else if (OperatingSystem.IsWindows())
            id = GetWindowsMachineGuid();
        else if (OperatingSystem.IsMacOS())
            id = GetMacMachineId();

        if (!string.IsNullOrEmpty(id))
            return id;

        // 2. Fallback: try persisted .machine-id
        return GetPersistedMachineId() ?? string.Empty;
    }

    /// <summary>
    /// Try to get or create a fallback machine ID persisted in a file.
    /// Only called when no OS-level identifier is available.
    /// </summary>
    private static string GetOrCreateFallbackMachineId()
    {
        // First check if a persisted ID already exists
        var existing = GetPersistedMachineId();
        if (!string.IsNullOrEmpty(existing))
            return existing;

        // Generate a new one
        if (_fallbackMachineIdDir == null)
            return string.Empty;

        try
        {
            Directory.CreateDirectory(_fallbackMachineIdDir);
            var newId = Guid.NewGuid().ToString("N");
            var path = Path.Combine(_fallbackMachineIdDir, ".machine-id");
            File.WriteAllText(path, newId);
            return newId;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Read a previously-persisted .machine-id file.
    /// </summary>
    private static string? GetPersistedMachineId()
    {
        if (_fallbackMachineIdDir == null)
            return null;

        try
        {
            var path = Path.Combine(_fallbackMachineIdDir, ".machine-id");
            if (File.Exists(path))
            {
                var id = File.ReadAllText(path).Trim();
                if (id.Length >= 16) return id;
            }
        }
        catch
        {
            // Ignore IO errors
        }

        return null;
    }

    // ─── Platform-specific ID retrieval ───

    private static string GetLinuxMachineId()
    {
        var paths = new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" };
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var id = File.ReadAllText(path).Trim();
                    if (id.Length >= 32) return id[..32];
                }
            }
            catch { }
        }

        return string.Empty;
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsMachineGuid()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetMacMachineId()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/sbin/ioreg",
                Arguments = "-rd1 -c IOPlatformExpertDevice",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return string.Empty;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(TimeSpan.FromSeconds(5));

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Contains("IOPlatformUUID", StringComparison.OrdinalIgnoreCase))
                {
                    var eqIdx = trimmed.IndexOf('=', StringComparison.Ordinal);
                    if (eqIdx >= 0)
                    {
                        return trimmed[(eqIdx + 1)..].Trim().Trim('"');
                    }
                }
            }
        }
        catch { }

        return string.Empty;
    }
}
