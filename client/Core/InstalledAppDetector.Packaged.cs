using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;
using client.Core.Models;

namespace client.Core;

/// <summary>
/// Packaged (MSIX / Store) apps on Windows — Calculator, Photos, Paint, Notepad (Win11),
/// Snipping Tool, Settings, Terminal, Store, Clock, Camera, Media Player… These never have
/// a Start-menu .lnk, so the shortcut scan cannot see them. Source of truth is the OS:
/// the per-user AppModel package repository (registry) plus each package's own
/// AppxManifest.xml. An app is a GUI app exactly when its manifest lists it in the Start
/// app list (AppListEntry != "none") with an Executable — no product names involved.
/// Runs in the user's process (no PowerShell — that is SYSTEM-only under app lockdown).
/// </summary>
public partial class InstalledAppDetector
{
    private const string PackageRepoKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    [SupportedOSPlatform("windows")]
    private void ScanPackagedApps()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            using var repo = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PackageRepoKey);
            if (repo is null)
                return;
            foreach (var fullName in repo.GetSubKeyNames())
            {
                try
                {
                    using var pkgKey = repo.OpenSubKey(fullName);
                    var root = pkgKey?.GetValue("PackageRootFolder") as string;
                    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                        continue;
                    AddPackagedApps(fullName, root);
                }
                catch
                {
                    // One broken package must not stop the scan.
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch { }
    }

    [SupportedOSPlatform("windows")]
    private void AddPackagedApps(string fullName, string root)
    {
        var manifestPath = Path.Combine(root, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
            return;
        var doc = XDocument.Load(manifestPath);
        var pkg = doc.Root;
        if (pkg is null)
            return;

        // Frameworks / resource packages are libraries, not apps.
        var props = Child(pkg, "Properties");
        if (IsTrue(Child(props, "Framework")?.Value) || IsTrue(Child(props, "ResourcePackage")?.Value))
            return;

        var identity = Child(pkg, "Identity");
        var pkgName = identity?.Attribute("Name")?.Value ?? "";
        if (string.IsNullOrWhiteSpace(pkgName))
            return;
        var version = identity?.Attribute("Version")?.Value ?? "";
        var family = PackageFamilyName(fullName, pkgName);
        var publisher = ResolveResource(fullName, pkgName, Child(props, "PublisherDisplayName")?.Value);
        var pkgDisplay = ResolveResource(fullName, pkgName, Child(props, "DisplayName")?.Value);

        var apps = Child(pkg, "Applications")?.Elements().Where(e => e.Name.LocalName == "Application")
                   ?? Enumerable.Empty<XElement>();
        foreach (var app in apps)
        {
            var exe = app.Attribute("Executable")?.Value;
            if (string.IsNullOrWhiteSpace(exe) || exe.Contains('$'))
                continue; // no launchable binary (or a build-time placeholder)
            var visual = app.Elements().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
            if (visual is null)
                continue;
            if (string.Equals(visual.Attribute("AppListEntry")?.Value, "none", StringComparison.OrdinalIgnoreCase))
                continue; // hidden from Start — background/host component

            var name = ResolveResource(fullName, pkgName, visual.Attribute("DisplayName")?.Value);
            if (string.IsNullOrWhiteSpace(name))
                name = pkgDisplay;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var exePath = Path.Combine(root, exe.Replace('/', '\\'));
            var binary = Path.GetFileNameWithoutExtension(exePath);
            if (string.IsNullOrWhiteSpace(binary))
                continue;
            // Same display name already found (shortcut / registry) — keep that row,
            // just give it an icon if it has none.
            var existing = _installedApps.FirstOrDefault(a =>
                string.Equals(a.AppName, name, StringComparison.OrdinalIgnoreCase));
            var icon = WindowsAppIcons.FromPngFile(PickLogo(root, visual.Attribute("Square44x44Logo")?.Value))
                       is { Length: > 0 } logo
                ? logo
                : WindowsAppIcons.FromExe(exePath);
            if (existing is not null)
            {
                if (string.IsNullOrEmpty(existing.IconPng))
                    existing.IconPng = icon;
                continue;
            }

            _knownApps.Add(name);
            _knownApps.Add(binary);
            if (!_binaryToDisplayName.ContainsKey(binary))
                _binaryToDisplayName[binary] = name;

            _installedApps.Add(new InstalledApplication
            {
                AppName = name.Trim(),
                BinaryName = binary,
                // AppUserModelID: stable across package version updates (catalog identity).
                DesktopId = family + "!" + (app.Attribute("Id")?.Value ?? "App"),
                AppVersion = version,
                Publisher = publisher,
                // Version-specific path; the DLP service derives the version-independent
                // package family from …\WindowsApps\<package>\ for AppLocker Appx rules.
                InstallPath = exePath,
                ChangeType = "installed",
                IconPng = icon,
                DetectedAt = DateTime.UtcNow,
            });
        }
    }

    /// <summary>Fill missing icons for desktop rows from their EXE (shortcut target / registry exe).</summary>
    [SupportedOSPlatform("windows")]
    private void FillDesktopAppIcons()
    {
        foreach (var app in _installedApps)
        {
            if (!string.IsNullOrEmpty(app.IconPng))
                continue;
            var exe = app.InstallPath;
            if (!string.IsNullOrWhiteSpace(exe) && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                exe = ExecutableMetadata.ResolveExePath(exe, "");
            app.IconPng = WindowsAppIcons.FromExe(exe);
        }
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static bool IsTrue(string? v) => string.Equals(v?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Name_Version_Arch_ResourceId_PublisherId → Name_PublisherId.</summary>
    internal static string PackageFamilyName(string fullName, string name)
    {
        var idx = fullName.LastIndexOf('_');
        return idx > 0 && idx < fullName.Length - 1 ? name + "_" + fullName[(idx + 1)..] : name;
    }

    /// <summary>
    /// Pick the best Start-list logo variant for a ~32 px list row. Manifest names the
    /// unqualified file ("Assets\X.png"); the package ships qualified variants
    /// ("X.targetsize-32_altform-unplated.png", "X.scale-100.png"…). High-contrast
    /// variants are skipped.
    /// </summary>
    internal static string? PickLogo(string root, string? logo)
    {
        if (string.IsNullOrWhiteSpace(logo))
            return null;
        try
        {
            var basePath = Path.Combine(root, logo.Replace('/', '\\'));
            var dir = Path.GetDirectoryName(basePath);
            if (dir is null || !Directory.Exists(dir))
                return null;
            var stem = Path.GetFileNameWithoutExtension(basePath);
            var files = Directory.EnumerateFiles(dir, stem + "*.png")
                .Where(f => !Path.GetFileName(f).Contains("contrast", StringComparison.OrdinalIgnoreCase))
                .ToList();
            string[] prefs =
            [
                ".targetsize-32_altform-unplated.png", ".targetsize-32.png",
                ".targetsize-48_altform-unplated.png", ".targetsize-48.png",
                ".targetsize-24_altform-unplated.png", ".targetsize-24.png",
                ".scale-100.png", ".scale-125.png", ".scale-150.png", ".scale-200.png",
            ];
            foreach (var suffix in prefs)
            {
                var hit = files.FirstOrDefault(f => f.EndsWith(stem + suffix, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                    return hit;
            }
            return File.Exists(basePath) ? basePath : files.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Resolve "ms-resource:…" through the OS (SHLoadIndirectString); literals pass through.</summary>
    [SupportedOSPlatform("windows")]
    private static string ResolveResource(string fullName, string pkgName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var v = value.Trim();
        if (!v.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return v;
        var key = v["ms-resource:".Length..];
        var candidates = new List<string>();
        if (key.StartsWith("//", StringComparison.Ordinal))
            candidates.Add("ms-resource:" + key);
        else if (key.StartsWith('/'))
            candidates.Add($"ms-resource://{pkgName}{key}");
        else if (key.Contains('/'))
            candidates.Add($"ms-resource://{pkgName}/{key}");
        else
            candidates.Add($"ms-resource://{pkgName}/Resources/{key}");
        candidates.Add($"ms-resource://{pkgName}/{key.TrimStart('/')}");

        foreach (var uri in candidates)
        {
            var sb = new StringBuilder(512);
            if (SHLoadIndirectString($"@{{{fullName}?{uri}}}", sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0)
                return sb.ToString();
        }
        return string.Empty;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int cchOutBuf, IntPtr reserved);
}
