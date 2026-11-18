using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace AlphaAITracker.Service;

/// <summary>
/// Signed WDAC / App Control for Business base + supplemental policy deployment.
/// Production on Windows Pro requires organization-signed <c>.cip</c> under
/// <c>%ProgramData%\&lt;mutex&gt;\dlp\wdac\</c>. Enforcing is proven only when that
/// CIP fingerprint matches the applied marker — SAC / SiPolicy.p7b heuristics are
/// never treated as org WDAC enforce (they falsely cleared AppProcessGuard).
/// </summary>
internal static class WdacEnforcer
{
    public sealed record Result(bool Changed, bool Applied, string Detail, bool SecureBootOk = false);

    private static string WdacDir => Path.Combine(DlpDataPaths.DlpRoot, "wdac");
    private static string BasePolicyXml => Path.Combine(WdacDir, "base-policy.xml");
    private static string BasePolicyCip => Path.Combine(WdacDir, "base-policy.cip");
    private static string SupplementalDir => Path.Combine(WdacDir, "supplemental");
    private static string LastFingerprintPath => Path.Combine(WdacDir, "applied.fp");

    public static bool HasOrgCip => File.Exists(BasePolicyCip);

    public static string? OrgCipFingerprint()
    {
        try
        {
            if (!File.Exists(BasePolicyCip))
                return null;
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BasePolicyCip)))
                .ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    public static Result Apply(IReadOnlyList<AllowAppEntry> allowApps)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os");

        try
        {
            Directory.CreateDirectory(WdacDir);
            Directory.CreateDirectory(SupplementalDir);

            var secureBoot = TryReadSecureBoot() == true;
            var hvci = IsHvciEnabled();

            // Write / refresh the managed supplemental catalog from published allow apps
            // so a signed base policy can reference it after IT signs the CIP.
            var supplementalXml = BuildSupplementalPolicyXml(allowApps);
            var supplementalPath = Path.Combine(SupplementalDir, "allow-apps.supplemental.xml");
            var prev = File.Exists(supplementalPath) ? File.ReadAllText(supplementalPath) : "";
            if (!string.Equals(prev, supplementalXml, StringComparison.Ordinal))
                File.WriteAllText(supplementalPath, supplementalXml, Encoding.UTF8);

            if (!File.Exists(BasePolicyCip) && !File.Exists(BasePolicyXml))
            {
                // Seed an unsigned template base policy for IT to sign with the org HSM.
                if (!File.Exists(BasePolicyXml))
                    File.WriteAllText(BasePolicyXml, BuildBasePolicyTemplateXml(), Encoding.UTF8);
                return new Result(
                    prev != supplementalXml,
                    false,
                    "wdac_unsigned_pending;place_signed_cip;secureboot=" +
                    (secureBoot ? "1" : "0") + ";hvci=" + (hvci ? "1" : "0"),
                    secureBoot);
            }

            if (!File.Exists(BasePolicyCip))
            {
                return new Result(false, false,
                    "wdac_cip_missing;sign_base_policy_xml;secureboot=" +
                    (secureBoot ? "1" : "0"),
                    secureBoot);
            }

            if (!secureBoot)
            {
                return new Result(false, false,
                    "wdac_requires_secure_boot",
                    secureBoot);
            }

            var fp = OrgCipFingerprint() ?? "";
            if (fp.Length < 12)
                return new Result(false, false, "wdac_cip_hash_failed", secureBoot);

            var last = File.Exists(LastFingerprintPath)
                ? File.ReadAllText(LastFingerprintPath).Trim()
                : "";
            if (string.Equals(fp, last, StringComparison.OrdinalIgnoreCase)
                && IsOrgCipEnforcing(fp))
            {
                return new Result(false, true, "wdac_verified;fp=" + fp[..12], secureBoot);
            }

            // CiTool is present on Windows 11 / Server 2022+; fall back to
            // Import-CIPolicy + Invoke-CimMethod on older builds.
            var deploy = DeployCip(BasePolicyCip);
            if (!deploy.Ok)
                return new Result(false, false, deploy.Detail, secureBoot);

            File.WriteAllText(LastFingerprintPath, fp);
            var enforcing = IsOrgCipEnforcing(fp);
            return new Result(true, enforcing,
                enforcing ? "wdac_enforcing;fp=" + fp[..12] : "wdac_deployed_not_enforcing",
                secureBoot);
        }
        catch (Exception ex)
        {
            return new Result(false, false, "wdac_error:" + Truncate(ex.Message, 180));
        }
    }

    public static Result Clear()
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os");
        try
        {
            if (File.Exists(LastFingerprintPath))
                File.Delete(LastFingerprintPath);
            // Do not silently remove org-signed CIP files — IT owns those.
            return new Result(true, false, "wdac_fingerprint_cleared");
        }
        catch (Exception ex)
        {
            return new Result(false, false, "wdac_clear_failed:" + Truncate(ex.Message, 120));
        }
    }

    /// <summary>
    /// Legacy name — returns true only when the org CIP fingerprint is applied AND
    /// Secure Boot is confirmed on. A throw or unknown Secure Boot state is not
    /// tamper-resistant. Never treats SAC / SiPolicy.p7b alone as org WDAC enforce.
    /// </summary>
    public static bool IsWdacEnforcing() => IsOrgCipEnforcing(OrgCipFingerprint());

    public static bool IsOrgCipEnforcing(string? expectedFp) =>
        IsOrgCipEnforcing(expectedFp, TryReadSecureBoot());

    /// <summary>
    /// File fingerprint match only. Secure Boot is applied by <see cref="IsOrgCipEnforcing(string?, bool?)"/>.
    /// </summary>
    internal static bool OrgCipFingerprintMatches(string? expectedFp)
    {
        if (string.IsNullOrWhiteSpace(expectedFp) || expectedFp.Length < 12)
            return false;
        try
        {
            if (!File.Exists(BasePolicyCip))
                return false;
            if (!File.Exists(LastFingerprintPath))
                return false;
            var last = File.ReadAllText(LastFingerprintPath).Trim();
            if (!string.Equals(last, expectedFp, StringComparison.OrdinalIgnoreCase))
                return false;
            var live = OrgCipFingerprint();
            return string.Equals(live, expectedFp, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tamper resistance requires the org CIP fingerprint and Secure Boot == true.
    /// Off, missing, or a thrown/unknown query is not enforcing.
    /// </summary>
    internal static bool IsOrgCipEnforcing(string? expectedFp, bool? secureBoot)
    {
        if (secureBoot != true)
            return false;
        return OrgCipFingerprintMatches(expectedFp);
    }

    public static string BlockRulesDetail =>
        File.Exists(Path.Combine(WdacDir, "recommended-blocks.merged"))
            ? "wdac_block_rules=verified"
            : "wdac_block_rules=unverified";

    /// <summary>
    /// True when this SKU is Windows Pro (not Enterprise / Education / Server).
    /// Pro still runs AppLocker when the build/KB probe says enforcement is possible.
    /// Production on Pro requires a signed org CIP; that is not "skip AppLocker".
    /// </summary>
    public static bool IsWindowsProSku()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var edition = (key?.GetValue("EditionID") as string) ?? "";
            var product = (key?.GetValue("ProductName") as string) ?? "";
            if (edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
                || edition.Contains("Education", StringComparison.OrdinalIgnoreCase)
                || edition.Contains("Server", StringComparison.OrdinalIgnoreCase)
                || product.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
                || product.Contains("Education", StringComparison.OrdinalIgnoreCase))
                return false;
            return edition.Contains("Professional", StringComparison.OrdinalIgnoreCase)
                   || edition.Equals("Pro", StringComparison.OrdinalIgnoreCase)
                   || product.Contains("Windows 10 Pro", StringComparison.OrdinalIgnoreCase)
                   || product.Contains("Windows 11 Pro", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when UEFI Secure Boot is on, false when it is off, null when the query
    /// throws or the value is missing. Null is not tamper-resistant.
    /// </summary>
    internal static bool? TryReadSecureBoot()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key is null)
                return null;
            var v = key.GetValue("UEFISecureBootEnabled");
            if (v is int i)
                return i == 1;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsHvciEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            var v = key?.GetValue("Enabled");
            return v is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }

    private static (bool Ok, string Detail) DeployCip(string cipPath)
    {
        var ciTool = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "CiTool.exe");
        if (File.Exists(ciTool))
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = ciTool,
                    Arguments = $"--update-policy \"{cipPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (p == null)
                    return (false, "citool_start_failed");
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(60_000);
                if (p.ExitCode == 0)
                    return (true, "citool_ok");
                return (false, "citool_failed:" + Truncate((stderr + " " + stdout).Trim(), 200));
            }
            catch (Exception ex)
            {
                return (false, "citool_error:" + Truncate(ex.Message, 120));
            }
        }

        // Fallback: copy into CodeIntegrity Active (requires reboot on some builds).
        try
        {
            var active = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "CodeIntegrity", "CiPolicies", "Active");
            Directory.CreateDirectory(active);
            var dest = Path.Combine(active, "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}.cip");
            File.Copy(cipPath, dest, overwrite: true);
            return (true, "cip_copied_reboot_may_be_required");
        }
        catch (Exception ex)
        {
            return (false, "cip_copy_failed:" + Truncate(ex.Message, 120));
        }
    }

    private static string BuildSupplementalPolicyXml(IReadOnlyList<AllowAppEntry> apps)
    {
        var rules = new XElement("Rules");
        var id = 1;
        foreach (var app in apps)
        {
            var path = (app.Path ?? "").Trim();
            if (path.Length == 0)
                continue;
            if (AppAllowEnforcer.IsUnsafeUserWritableAllow(path))
                continue;
            rules.Add(new XElement("FileRule",
                new XAttribute("Id", "ID_ALLOW_PATH_" + id++),
                new XAttribute("FriendlyName", "AlphaAI allow " + Truncate(path, 60)),
                new XAttribute("FileName", "*"),
                new XAttribute("Action", "Allow"),
                new XElement("FilePath", path)));
        }

        var doc = new XDocument(
            new XElement("SiPolicy",
                new XAttribute("PolicyType", "Supplemental"),
                new XElement("PolicyID", "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}"),
                new XElement("BasePolicyID", "{A0000000-0000-4000-8000-000000000001}"),
                new XElement("VersionEx", "10.0.0.0"),
                new XElement("PlatformID", "{2A1A7B4A-2C3D-4E5F-8A9B-0C1D2E3F4A5B}"),
                rules));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildBasePolicyTemplateXml()
    {
        // Template only — must be signed with the organization WDAC signing cert / HSM
        // before BasePolicyCip exists and Apply will enforce. Paths are named product
        // binaries (not whole Program Files wildcards).
        var product = ServiceIdentity.DisplayName;
        var doc = new XDocument(
            new XElement("SiPolicy",
                new XAttribute("PolicyType", "Base Policy"),
                new XElement("PolicyID", "{A0000000-0000-4000-8000-000000000001}"),
                new XElement("VersionEx", "10.0.0.0"),
                new XElement("PlatformID", "{2A1A7B4A-2C3D-4E5F-8A9B-0C1D2E3F4A5B}"),
                new XElement("Rules",
                    new XElement("Rule",
                        new XAttribute("Id", "ID_ALLOW_WINDOWS"),
                        new XAttribute("FriendlyName", "Allow Windows"),
                        new XAttribute("Action", "Allow"),
                        new XElement("FilePath", "%SystemRoot%\\*")),
                    new XElement("Rule",
                        new XAttribute("Id", "ID_ALLOW_PRODUCT_CLIENT"),
                        new XAttribute("FriendlyName", "Allow Alpha AI Tracker client"),
                        new XAttribute("Action", "Allow"),
                        new XElement("FilePath", "%ProgramFiles%\\" + product + "\\client.exe")),
                    new XElement("Rule",
                        new XAttribute("Id", "ID_ALLOW_PRODUCT_SERVICE"),
                        new XAttribute("FriendlyName", "Allow Alpha AI Tracker service"),
                        new XAttribute("Action", "Allow"),
                        new XElement("FilePath",
                            "%ProgramFiles%\\" + product + "\\" + ServiceIdentity.ServiceName + ".exe")))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max];
}
