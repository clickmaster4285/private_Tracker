using System.Text.RegularExpressions;
using client.Core.Models;

namespace client.Core;

/// <summary>
/// Single-source-of-truth software classification. Runs AFTER InstalledAppDetector and
/// PackageDetector have both discovered raw entries. Responsibilities:
///
///   1. Generate a stable identity for every raw entry (via SoftwareIdentityResolver).
///   2. Dedup: an entry discovered by both detectors collapses to one record. GUI apps
///      (those with a .desktop / .app / registry entry) win and go to installed_applications;
///      their matching package entry is dropped. This fixes the Firefox-snap case where the
///      snap appeared in installed_packages instead of installed_applications.
///   3. Classify each surviving entry into exactly one category (SoftwareCategoryResolver).
///
/// The classifier does NOT perform discovery itself — it only normalizes and routes the
/// output of the existing detectors, preserving all current discovery logic.
/// </summary>
public static class SoftwareClassifier
{
    /// <summary>
    /// Partition raw discovered apps + packages into deduplicated classified sets.
    /// Apps returned are GUI applications (installed_applications); packages returned
    /// are CLI tools/runtimes/libraries (installed_packages). No software appears in both.
    /// </summary>
    public static (IReadOnlyList<InstalledApplication> apps, IReadOnlyList<InstalledPackage> packages) Classify(
        IReadOnlyList<InstalledApplication> rawApps,
        IReadOnlyList<InstalledPackage> rawPackages)
    {
        // Windows: collapse the two discovery sources (Start Menu shortcut + registry Uninstall
        // entry) into ONE row per software and route CLI/runtime launchers to packages.
        rawApps = CollapseWindowsDuplicateApps(rawApps);

        var appByIdentity = new Dictionary<string, InstalledApplication>(StringComparer.Ordinal);
        foreach (var app in rawApps)
        {
            var identity = SoftwareIdentityResolver.ResolveAppIdentity(app);
            // Classify the app using its Categories metadata (no hardcoded name lists).
            var category = SoftwareCategoryResolver.ResolveFromCategories(app.Categories, app.IsBrowser);
            // Only GUI categories belong in installed_applications; if somehow a non-GUI
            // entry reached the detector, it is dropped here (will be re-evaluated as a package).
            // Registry-sourced entries (ChangeType="installed") are a trusted app source: many
            // ARP entries have no InstallLocation/DisplayIcon, so keep them even without a binary.
            if (category == SoftwareCategoryResolver.Unknown && string.IsNullOrEmpty(app.BinaryName) &&
                !string.Equals(app.ChangeType, "installed", StringComparison.OrdinalIgnoreCase))
                continue;
            appByIdentity[identity] = app;
        }

        // Build a quick lookup of app identities to suppress matching package entries.
        var appIdentitySet = new HashSet<string>(appByIdentity.Keys, StringComparer.Ordinal);
        // Also build a name-based suppression set: if a package name equals an app binary/app name,
        // the package is the same software (snap/flatpak GUI app) and must be dropped.
        var appNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in appByIdentity.Values)
        {
            if (!string.IsNullOrEmpty(a.BinaryName)) appNames.Add(a.BinaryName);
            if (!string.IsNullOrEmpty(a.AppName)) appNames.Add(a.AppName);
            if (!string.IsNullOrEmpty(a.DesktopId))
            {
                // snap desktop_id "firefox_firefox" → package name "firefox" matches
                var baseId = a.DesktopId.Split('_')[0];
                if (!string.IsNullOrEmpty(baseId)) appNames.Add(baseId);
            }
        }

        var packages = new List<InstalledPackage>();
        var pkgIdentitySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pkg in rawPackages)
        {
            // Suppress a package if its name matches a discovered GUI app (one software = one identity).
            if (IsSuppressedByAppName(pkg.PackageName, appNames)) continue;
            var identity = SoftwareIdentityResolver.ResolvePackageIdentity(pkg);
            if (!pkgIdentitySet.Add(identity)) continue; // dedup identical packages
            // Classify the package (runtime/tool/library) and persist the canonical category.
            pkg.Category = SoftwareCategoryResolver.ResolveForPackage(pkg.PackageName, pkg.SourceManager, pkg.Category);
            packages.Add(pkg);
        }

        return (appByIdentity.Values.ToList(), packages);
    }

    /// <summary>
    /// Windows-only pre-pass: the same software is discovered twice — once from the Start Menu
    /// shortcut (clean name + binary, e.g. "Visual Studio Code" / Code) and once from the registry
    /// Uninstall key (noisy name + richer metadata, e.g. "Microsoft Visual Studio Code (User)"
    /// with version/publisher). This collapses them into ONE row: the shortcut's clean name wins,
    /// registry metadata is merged in. CLI/runtime launchers (node, git-bash, cmd…) are dropped
    /// here — they belong in installed_packages, exactly like Linux tools without a .desktop.
    /// </summary>
    private static IReadOnlyList<InstalledApplication> CollapseWindowsDuplicateApps(
        IReadOnlyList<InstalledApplication> apps)
    {
        if (!OperatingSystem.IsWindows() || apps.Count == 0) return apps;

        var byBinary = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
        var leftovers = new List<InstalledApplication>();

        foreach (var app in apps)
        {
            // Installer helper shortcuts ("… Uninstaller") are never applications.
            if (app.AppName.EndsWith(" Uninstaller", StringComparison.OrdinalIgnoreCase) ||
                app.AppName.EndsWith(" Uninstall", StringComparison.OrdinalIgnoreCase))
                continue;

            // Defensive CLI gate: if a shortcut/registry row ever carries a console-subsystem
            // executable (node, git-bash, cmd…), it is a CLI tool, not an app. The detectors
            // already enforce this at discovery; this catches stragglers structurally via the
            // PE Subsystem — no product names.
            if (!string.IsNullOrEmpty(app.InstallPath) &&
                ExecutableMetadata.GetSubsystem(app.InstallPath) == ExecutableMetadata.SubsystemWindowsCui)
                continue;

            if (!string.IsNullOrEmpty(app.BinaryName))
            {
                if (byBinary.TryGetValue(app.BinaryName, out var existing))
                    MergeAppMetadata(existing, app);
                else
                    byBinary[app.BinaryName] = app;
            }
            else
            {
                leftovers.Add(app);
            }
        }

        var merged = byBinary.Values.ToList();
        var keptNames = new HashSet<string>(merged.Select(a => a.AppName), StringComparer.OrdinalIgnoreCase);

        foreach (var app in leftovers)
        {
            var matchKey = StripVersionSuffix(app.AppName, out var versionLike);
            var target = merged.FirstOrDefault(m =>
                string.Equals(m.AppName, matchKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.AppName, app.AppName, StringComparison.OrdinalIgnoreCase));

            if (target != null)
            {
                MergeAppMetadata(target, app);
            }
            else if (keptNames.Add(app.AppName))
            {
                // Registry-only install (no Start Menu shortcut) — keep it. The name is
                // version-stripped ONLY when a version/edition suffix was actually removed
                // ("Zed Attack Proxy by Checkmarx 2.17.0" → "…Checkmarx"); a " - " truncation
                // is only ever a match key, never a rename, so names like "X - Y" survive.
                if (versionLike)
                    app.AppName = matchKey;
                merged.Add(app);
            }
        }

        return merged;
    }

    /// <summary>Copy registry metadata (version/publisher/install path) into the shortcut row.</summary>
    private static void MergeAppMetadata(InstalledApplication target, InstalledApplication source)
    {
        // Prefer the shortest display name — the clean base name over noisier variants
        // ("VLC media player" over "VLC media player - reset preferences and cache files",
        //  "Windows PowerShell" over "Windows PowerShell (x86)").
        if (!string.IsNullOrEmpty(source.AppName) && source.AppName.Length < target.AppName.Length)
            target.AppName = source.AppName;
        if (string.IsNullOrEmpty(target.AppVersion)) target.AppVersion = source.AppVersion;
        if (string.IsNullOrEmpty(target.Publisher)) target.Publisher = source.Publisher;
        if (string.IsNullOrEmpty(target.InstallPath)) target.InstallPath = source.InstallPath;
        if (string.IsNullOrEmpty(target.UninstallString)) target.UninstallString = source.UninstallString;
        if (string.IsNullOrEmpty(target.DesktopId)) target.DesktopId = source.DesktopId;
        target.IsBrowser |= source.IsBrowser;
        if (source.IsBrowser && !target.Categories.Contains("WebBrowser", StringComparison.OrdinalIgnoreCase))
            target.Categories = "WebBrowser";
    }

    /// <summary>
    /// Strip trailing version/edition noise so a registry name can match its shortcut name:
    /// "Advanced IP Scanner 2.5.1" → "Advanced IP Scanner", "PuTTY release 0.83" → "PuTTY",
    /// "VLC media player - reset preferences and cache files" → "VLC media player".
    /// versionLike is true only when a REAL version/edition token was removed (release/v/1.2.3)
    /// — the " - " truncation is a match key only and must never drive a rename.
    /// </summary>
    private static string StripVersionSuffix(string name, out bool versionLike)
    {
        versionLike = false;
        var n = name.Trim();
        if (n.Length < 6) return n;

        var m = Regex.Match(n, @"^(?<base>.+?)\s+(?:release|version)\s+v?\d[\d.]*\s*$", RegexOptions.IgnoreCase);
        if (m.Success) versionLike = true;
        if (!m.Success)
        {
            m = Regex.Match(n, @"^(?<base>.+?)\s+v\d[\d.]*\s*$", RegexOptions.IgnoreCase);
            if (m.Success) versionLike = true;
        }
        if (!m.Success)
        {
            m = Regex.Match(n, @"^(?<base>.+?)\s+\d+(?:\.\d+)+\s*(?:64\s?bit|x64|x86|32\s?bit)?\s*$", RegexOptions.IgnoreCase);
            if (m.Success) versionLike = true;
        }
        if (!m.Success)
            m = Regex.Match(n, @"^(?<base>.+?)\s+-\s+.+$");
        if (!m.Success) return n;

        var candidate = m.Groups["base"].Value.Trim();
        return candidate.Length >= 3 ? candidate : n;
    }

    /// <summary>
    /// True if a package name matches a discovered GUI app name — exact, or one is a
    /// substring of the other with BOTH sides ≥4 chars. This catches winget names like
    /// "Microsoft Visual Studio Code (User)" against the registry app "Visual Studio Code",
    /// while short tool names ("Git", "Go") are never over-suppressed.
    /// </summary>
    private static bool IsSuppressedByAppName(string packageName, HashSet<string> appNames)
    {
        if (appNames.Contains(packageName)) return true;
        if (packageName.Length < 4) return false;
        foreach (var appName in appNames)
        {
            if (appName.Length < 4) continue;
            if (appName.Contains(packageName, StringComparison.OrdinalIgnoreCase) ||
                packageName.Contains(appName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
