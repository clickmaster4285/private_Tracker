using System.Security.Cryptography;
using System.Text;
using client.Core.Models;

namespace client.Core;

/// <summary>
/// Generates a stable software identity for deduplication across discovery sources
/// (InstalledAppDetector vs PackageDetector) and across package managers.
///
/// Identity inputs (first non-empty wins, by platform):
///   Linux : desktop_id  (the .desktop filename, e.g. "firefox_firefox")
///   macOS : bundle id   (CFBundleIdentifier, e.g. "org.mozilla.firefox")
///   Windows: registry uninstall key name
/// Fallback: normalized install path + binary name.
///
/// The identity is a SHA-256 of "platform|primarySignal|installPath-normalized" so the same
/// software discovered via two sources collapses to one row, and a re-install at a different
/// path is treated as a new identity (correct — it is a distinct installation).
/// </summary>
public static class SoftwareIdentityResolver
{
    /// <summary>Compute a stable identity key for an installed application.</summary>
    public static string ResolveAppIdentity(InstalledApplication app)
    {
        var platform = GetPlatformToken();
        var primary = FirstNonEmpty(app.DesktopId, app.Categories, app.BinaryName, app.AppName);
        var install = NormalizePath(app.InstallPath);
        return Hash($"{platform}|app|{primary}|{install}");
    }

    /// <summary>Compute a stable identity key for an installed package.</summary>
    public static string ResolvePackageIdentity(InstalledPackage pkg)
    {
        var platform = GetPlatformToken();
        var primary = FirstNonEmpty(pkg.PackageName, pkg.InstallPath);
        var manager = pkg.SourceManager ?? string.Empty;
        return Hash($"{platform}|pkg|{manager}|{primary}");
    }

    private static string GetPlatformToken() =>
        OperatingSystem.IsWindows() ? "win" :
        OperatingSystem.IsMacOS() ? "mac" : "lin";

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim().ToLowerInvariant();
        return string.Empty;
    }

    /// <summary>Normalize an install path for stable comparison (lowercase, strip trailing separators, resolve ~).</summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var p = path.Trim();
            if (p.StartsWith('~'))
                p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[1..].TrimStart('/'));
            p = Path.GetFullPath(p).TrimEnd('/', '\\');
            return p.ToLowerInvariant();
        }
        catch
        {
            return path.Trim().TrimEnd('/', '\\').ToLowerInvariant();
        }
    }

    private static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
