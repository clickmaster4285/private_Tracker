using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using client.Core.Models;

namespace client.Core;

public partial class PackageDetector : Abstractions.IPackageDetector
{
    private readonly HashSet<string> _knownPackages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _missingPerms = new();
    private readonly List<string> _permInstructions = new();
    private readonly List<InstalledPackage> _packages = new();
    private bool _initialized;
    private readonly object _lock = new();

    public IReadOnlyList<InstalledPackage> GetAllInstalledPackages()
    {
        EnsureInitialized();
        return _packages.ToList();
    }

    public IReadOnlySet<string> KnownPackageNames
    {
        get
        {
            EnsureInitialized();
            return _knownPackages;
        }
    }

    public bool IsKnownPackage(string packageName)
    {
        EnsureInitialized();
        return _knownPackages.Contains(packageName);
    }

    public IReadOnlyList<string> MissingPermissions
    {
        get
        {
            EnsureInitialized();
            return _missingPerms;
        }
    }

    public IReadOnlyList<string> PermissionGrantInstructions
    {
        get
        {
            EnsureInitialized();
            return _permInstructions;
        }
    }

    public void ForceRecheck()
    {
        lock (_lock)
        {
            _initialized = false;
            _knownPackages.Clear();
            _missingPerms.Clear();
            _permInstructions.Clear();
            _packages.Clear();
        }
        EnsureInitialized();
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            try
            {
                ScanPackageManagers();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PackageDetector init error: {ex.Message}");
            }

            _initialized = true;
        }
    }

    private void ScanPackageManagers()
    {
        // Language-specific package managers (run on all platforms)
        ScanNpmGlobal();
        ScanPip();

        // Platform-specific
        if (OperatingSystem.IsWindows())
        {
            ScanChocolatey();
            ScanWinget();
            ScanScoop();
            // MSI-installed dev tools/runtimes (Node.js, JDK, OpenSSL, Npcap) appear ONLY in
            // the registry — not in any package manager. Must run after the managers so it can
            // dedup against the packages they reported. The DriverStore scan then captures
            // hardware drivers from the OS driver database (the genuine analog of dpkg's
            // firmware/alsa packages).
            ScanRegistrySoftware();
            ScanDriverStore();
        }
        else if (OperatingSystem.IsLinux())
        {
            ScanDpkg();
            ScanSnap();
            ScanFlatpak();
        }
        else if (OperatingSystem.IsMacOS())
        {
            ScanBrew();
            ScanMacPorts();
        }
    }

    /// <summary>
    /// Build a ProcessStartInfo that can launch batch-shimmed CLIs (npm.cmd, scoop.cmd).
    /// Process.Start → CreateProcess only resolves .exe files, so on Windows these are
    /// invoked through cmd.exe /c — without this, npm/scoop scans die silently in the
    /// catch and global tools (freebuff, opencode-ai…) never reach installed_packages.
    /// </summary>
    private static ProcessStartInfo BuildCliStartInfo(string command, string arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command} {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        return new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    // ── npm global packages ──

    private void ScanNpmGlobal()
    {
        try
        {
            var psi = BuildCliStartInfo("npm", "list -g --json --depth=0");
            var output = ProcessFilter.RunProbe(psi, 15000);
            if (output == null) return;

            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps)) return;

            foreach (var dep in deps.EnumerateObject())
            {
                var pkg = ParseNpmDep(dep);
                if (pkg != null) AddPackage(pkg);
            }
        }
        catch (InvalidOperationException) { }
        catch (JsonException) { }
        catch { }
    }

    private static InstalledPackage? ParseNpmDep(JsonProperty dep)
    {
        try
        {
            var version = dep.Value.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            return new InstalledPackage
            {
                PackageName = dep.Name,
                Version = version,
                Category = CategorizeNpmPackage(dep.Name),
                SourceManager = "npm",
                Description = $"{dep.Name}@{version}",
                DetectedAt = DateTime.UtcNow,
            };
        }
        catch { return null; }
    }

    private static string CategorizeNpmPackage(string name)
    {
        // Structural conventions only: @types/* is npm's documented type-definition scope
        // (a library); everything else installed globally is a CLI tool/runtime by nature.
        var lower = name.ToLowerInvariant();
        if (lower is "node" or "nodejs") return "runtime";
        if (lower.StartsWith("@types/")) return "library";
        return "tool";
    }

    // ── pip packages ──

    private void ScanPip()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pip",
                Arguments = "list --format=json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var output = ProcessFilter.RunProbe(psi, 15000);
            if (output == null) return;

            using var doc = JsonDocument.Parse(output);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var version = item.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;

                AddPackage(new InstalledPackage
                {
                    PackageName = name,
                    Version = version,
                    Category = CategorizePipPackage(name),
                    SourceManager = "pip",
                    Description = $"{name}=={version}",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch (InvalidOperationException) { }
        catch (JsonException) { }
        catch { }
    }

    private static string CategorizePipPackage(string name)
    {
        // pip itself and the build bootstrap set are tools; everything else is a library.
        if (name.Equals("pip", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("setuptools", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("wheel", StringComparison.OrdinalIgnoreCase))
            return "tool";
        return "library";
    }

    // ── dpkg (Debian/Ubuntu) ──

    private void ScanDpkg()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dpkg-query",
                // The -f format MUST be double-quoted: .NET's Unix argument parser splits
                // on tabs as well as spaces, so an unquoted format with literal tab
                // characters made dpkg-query receive ${Version}/${Maintainer}/${Section}
                // as package-name PATTERNS — "no packages found matching ${Version}"
                // (3 stderr errors on every startup). Quoted, dpkg-query gets the whole
                // format as ONE argument and interprets the \t / \n escapes itself.
                Arguments = "-W -f=\"${Package}\\t${Version}\\t${Maintainer}\\t${Section}\\n\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var output = ProcessFilter.RunProbe(psi, 10000);
            if (output == null) return;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 1) continue;

                var name = parts[0].Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var section = parts.Length >= 4 ? parts[3].Trim().ToLowerInvariant() : "";
                var category = section switch
                {
                    "libs" or "libdevel" => "library",
                    "devel" or "interpreters" => "tool",
                    "admin" or "utils" or "text" or "net" => "tool",
                    "kernel" or "system" => "system",
                    _ => "tool"
                };

                AddPackage(new InstalledPackage
                {
                    PackageName = name,
                    Version = parts.Length >= 2 ? parts[1].Trim() : "",
                    Publisher = parts.Length >= 3 ? parts[2].Trim() : "",
                    Category = category,
                    SourceManager = "apt",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            _missingPerms.Add("dpkg_query");
        }
        catch (InvalidOperationException) { }
        catch { }
    }

    // ── snap packages ──

    private void ScanSnap()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "snap",
                Arguments = "list",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var output = ProcessFilter.RunProbe(psi, 10000);
            if (output == null) return;

            // Snap .desktop files live here (e.g. firefox_firefox.desktop).
            // Any snap that has a desktop entry is a GUI application discovered by
            // InstalledAppDetector via $XDG_DATA_DIRS — skip it here to avoid the
            // Firefox-as-package misclassification (one software = one identity).
            var snapDesktopDir = "/var/lib/snapd/desktop/applications";

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1 || parts[0].Equals("Name", StringComparison.OrdinalIgnoreCase))
                    continue;

                var snapName = parts[0];
                if (HasSnapDesktopEntry(snapName, snapDesktopDir))
                    continue; // GUI app — handled by InstalledAppDetector

                AddPackage(new InstalledPackage
                {
                    PackageName = snapName,
                    Version = parts.Length >= 2 ? parts[1] : "",
                    Category = "tool",
                    SourceManager = "snap",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    /// <summary>True if a .desktop file exists for the given snap name in the snap desktop dir.</summary>
    private static bool HasSnapDesktopEntry(string snapName, string snapDesktopDir)
    {
        try
        {
            if (!Directory.Exists(snapDesktopDir)) return false;
            // snap desktop files are named {snap}_{snap}.desktop (e.g. firefox_firefox.desktop)
            return Directory.GetFiles(snapDesktopDir, $"{snapName}_*.desktop").Length > 0;
        }
        catch { return false; }
    }

    // ── flatpak packages ──

    private void ScanFlatpak()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "flatpak",
                Arguments = "list --app --columns=application,version,runtime",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var output = ProcessFilter.RunProbe(psi, 10000);
            if (output == null) return;

            // Flatpak app .desktop files are exported to the flatpak share dir and surfaced
            // via $XDG_DATA_DIRS (e.g. /var/lib/flatpak/exports/share/applications).
            // Any flatpak app with an exported desktop entry is a GUI application discovered
            // by InstalledAppDetector — skip it here (one software = one identity).
            var flatpakDesktopDirs = GetFlatpakDesktopDirs();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1 || parts[0].Equals("Application", StringComparison.OrdinalIgnoreCase))
                    continue;

                var appId = parts[0];
                if (HasFlatpakDesktopEntry(appId, flatpakDesktopDirs))
                    continue; // GUI app — handled by InstalledAppDetector

                AddPackage(new InstalledPackage
                {
                    PackageName = appId,
                    Version = parts.Length >= 2 ? parts[1] : "",
                    Category = "tool",
                    SourceManager = "flatpak",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    /// <summary>Flatpak export dirs that may contain app .desktop files.</summary>
    private static List<string> GetFlatpakDesktopDirs()
    {
        var dirs = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".local", "share", "flatpak", "exports", "share", "applications"));
        dirs.Add("/var/lib/flatpak/exports/share/applications");
        return dirs;
    }

    /// <summary>True if a .desktop file exists for the given flatpak app id in any flatpak export dir.</summary>
    private static bool HasFlatpakDesktopEntry(string appId, List<string> dirs)
    {
        try
        {
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                // flatpak desktop files are named {appId}.desktop
                if (File.Exists(Path.Combine(dir, $"{appId}.desktop")))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    // ── Homebrew (macOS) ──

    private void ScanBrew()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "brew",
                Arguments = "list --formula --quiet",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var output = ProcessFilter.RunProbe(psi, 15000);
            if (output == null) return;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = line.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                AddPackage(new InstalledPackage
                {
                    PackageName = name,
                    Category = "tool",
                    SourceManager = "brew",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    // ── MacPorts (macOS alternative) ──

    private void ScanMacPorts()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "port",
                Arguments = "installed",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var output = ProcessFilter.RunProbe(psi, 15000);
            if (output == null) return;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) continue;
                var name = parts[0].Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Equals("The", StringComparison.OrdinalIgnoreCase))
                    continue;

                AddPackage(new InstalledPackage
                {
                    PackageName = name,
                    Version = parts.Length >= 2 ? parts[1].Trim('@') : "",
                    Category = "tool",
                    SourceManager = "macports",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    // ── Chocolatey (Windows) ──

    private void ScanChocolatey()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "choco",
                Arguments = "list --local-only --limit-output",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var output = ProcessFilter.RunProbe(psi, 15000);
            if (output == null) return;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|');
                if (parts.Length < 1) continue;

                AddPackage(new InstalledPackage
                {
                    PackageName = parts[0].Trim(),
                    Version = parts.Length >= 2 ? parts[1].Trim() : "",
                    Category = "tool",
                    SourceManager = "choco",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    // ── Winget (Windows) ──
    //
    // `winget list` reports EVERY installed program (GUI apps, MSIX packages, ARP entries,
    // frameworks) in a space-padded console table. The old parser split on whitespace, which
    // (a) shredded every multi-word name ("Google Chrome" → package "Google" version
    // "ARP\Machine\X86\Google") and (b) dumped every GUI app on the machine into
    // installed_packages. Now:
    //   - Column start offsets are read from the header line, so multi-word names survive.
    //   - ARP\… / MSIX\… rows are dropped — those ARE the GUI applications, discovered by the
    //     registry scan in InstalledAppDetector (one software = one identity).
    //   - MS Store apps and known framework/runtime rows are dropped too.
    //   - What remains (dotted package ids like Git.Git, GoLang.Go) is further deduped against
    //     the discovered GUI apps by SoftwareClassifier's name suppression.
    private void ScanWinget()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "list --accept-source-agreements --disable-interactivity",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var output = ProcessFilter.RunProbe(psi, 20000);
            if (output == null) return;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            var (nameStart, idStart, versionStart, availableStart, sourceStart) = ParseWingetColumnStarts(lines);
            if (idStart < 0) return;

            foreach (var line in lines)
            {
                if (line.StartsWith("Name", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("---", StringComparison.Ordinal))
                    continue;

                var name = SliceWingetColumn(line, nameStart, idStart);
                var id = SliceWingetColumn(line, idStart, versionStart);
                var version = SliceWingetColumn(line, versionStart, availableStart);
                var source = SliceWingetColumn(line, sourceStart, line.Length);

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;

                // ARP\… and MSIX\… rows are the GUI applications (also in the registry scan).
                if (id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase) ||
                    id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase))
                    continue;
                // MS Store apps are GUI applications too.
                if (source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Everything else is genuinely installed software that no GUI app claims
                // (the classifier suppresses names that match a discovered app). This is the
                // Windows analog of dpkg listing every package — .NET SDK, VC++ redists,
                // runtimes and tools all belong here, with no product-name filtering.
                AddPackage(new InstalledPackage
                {
                    PackageName = name,
                    Version = version,
                    Category = "tool",
                    SourceManager = "winget",
                    Description = id,
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    /// <summary>Locate the winget table column starts from the header line ("Name Id Version Available Source").</summary>
    private static (int name, int id, int version, int available, int source) ParseWingetColumnStarts(string[] lines)
    {
        var header = lines.FirstOrDefault(l => l.StartsWith("Name", StringComparison.OrdinalIgnoreCase)) ?? "";
        var tokens = Regex.Matches(header, @"\S+");
        int name = 0, id = -1, version = -1, available = -1, source = -1;
        foreach (Match token in tokens)
        {
            switch (token.Value)
            {
                case "Name": name = token.Index; break;
                case "Id": id = token.Index; break;
                case "Version": version = token.Index; break;
                case "Available": available = token.Index; break;
                case "Source": source = token.Index; break;
            }
        }
        // Older winget versions have no Available column — treat it as the Source start.
        if (available < 0) available = source;
        return (name, id, version, available, source);
    }

    /// <summary>Slice a fixed-width table column safely (out-of-range → empty string).</summary>
    private static string SliceWingetColumn(string line, int start, int end)
    {
        if (start < 0 || start >= line.Length) return "";
        var length = end < 0 ? line.Length - start : Math.Min(end, line.Length) - start;
        return length <= 0 ? "" : line.Substring(start, length).Trim();
    }

    // ── Scoop (Windows) ──

    private void ScanScoop()
    {
        try
        {
            var psi = BuildCliStartInfo("scoop", "list");
            var output = ProcessFilter.RunProbe(psi, 15000);
            if (output == null) return;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) continue;
                var name = parts[0].Trim();
                if (name.Equals("Installed", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("----", StringComparison.OrdinalIgnoreCase))
                    continue;

                AddPackage(new InstalledPackage
                {
                    PackageName = name,
                    Version = parts.Length >= 2 ? parts[1].Trim('(', ')') : "",
                    Category = "tool",
                    SourceManager = "scoop",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    // ── Registry-installed software (MSI installers + driver components) ──
    //
    // winget/npm/pip/choco/scoop cover only package-manager installs. Software installed via
    // classic MSI installers (Node.js, Eclipse Temurin JDK, OpenSSL, Npcap) and driver/system
    // components (Realtek audio) exist ONLY in the registry Uninstall keys. On Linux, dpkg lists
    // every installed package including libs/firmware — without this scan the Windows analog
    // (Node.js, Realtek Audio COM Components…) silently vanishes from installed_packages.
    // GUI apps in the registry are handled by InstalledAppDetector (one software = one identity),
    // so only non-app components are captured here, deduped against packages from the managers.
    [SupportedOSPlatform("windows")]
    private void ScanRegistrySoftware()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            ScanRegistrySoftwareNode(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            ScanRegistrySoftwareNode(Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
            ScanRegistrySoftwareNode(Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        }
        catch (UnauthorizedAccessException) { }
        catch { }
    }

    [SupportedOSPlatform("windows")]
    private void ScanRegistrySoftwareNode(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key == null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    if (!(subKey.GetValue("DisplayName") is string displayName) ||
                        string.IsNullOrWhiteSpace(displayName))
                        continue;

                    // Structural skips — OS flags + Windows conventions (shared with the app
                    // side): SystemComponent/ParentKeyName rows are internal installer churn,
                    // ReleaseType marks updates, KB rows are Windows Update, and
                    // LocalServiceComponents/"… Uninstaller" are installer helpers.
                    if (ExecutableMetadata.IsSystemOrUpdateRegistryRow(subKey, displayName)) continue;

                    // GUI apps (GUI-subsystem executable or URL associations) belong to
                    // InstalledAppDetector — one software = one identity. Everything else is a
                    // package: CUI-subsystem tools (node/git/openssl…), exe-less components and
                    // redists. Classification is the executable's PE Subsystem, not names.
                    var installPath = subKey.GetValue("InstallLocation") as string ?? "";
                    var displayIcon = subKey.GetValue("DisplayIcon") as string ?? "";
                    var exePath = ExecutableMetadata.ResolveExePath(installPath, displayIcon);
                    var subsystem = exePath != null ? ExecutableMetadata.GetSubsystem(exePath) : (ushort)0;
                    if (subsystem == ExecutableMetadata.SubsystemWindowsGui)
                        continue; // GUI application — app detector owns it
                    if (ExecutableMetadata.HasUrlAssociations(subKey))
                        continue; // browser application — app detector owns it
                    // Installer bootstrappers (Package Cache), uninstallers and *setup*
                    // executables are neither applications nor installable software — skip them
                    // entirely (structural conventions, no product names).
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        var binary = Path.GetFileNameWithoutExtension(exePath);
                        if (ExecutableMetadata.IsUninstallerFileName(binary) ||
                            ExecutableMetadata.IsInstallerFileName(binary) ||
                            ExecutableMetadata.IsInstallerCachePath(exePath))
                            continue;
                    }

                    var cleanName = CleanRegistryPackageName(displayName);
                    if (IsPackageAlreadyKnown(cleanName)) continue; // winget/npm already reported it

                    AddPackage(new InstalledPackage
                    {
                        PackageName = cleanName,
                        Version = subKey.GetValue("DisplayVersion") as string ?? "",
                        Category = "tool",
                        SourceManager = "installer",
                        Publisher = subKey.GetValue("Publisher") as string ?? "",
                        InstallPath = installPath,
                        Description = displayName,
                        DetectedAt = DateTime.UtcNow,
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    // ── Driver Store (Windows hardware drivers) ──
    //
    // Drivers never appear in the Uninstall registry or any package manager. The genuine OS
    // inventory is HKLM\SYSTEM\CurrentControlSet\Control\Class\{device-class-GUID}\<instance>
    // — the exact data Device Manager shows: DriverDesc ("Realtek Audio"), ProviderName
    // ("Realtek Semiconductor Corp."), DriverVersion (6.0.9175.1). This is the Windows analog
    // of dpkg listing firmware/alsa packages, works on Windows 7 through 11, zero names.
    [SupportedOSPlatform("windows")]
    private void ScanDriverStore()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var classes = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class");
            if (classes == null) return;

            // Dedup identical driver instances (the same driver is listed once per device).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var classGuidName in classes.GetSubKeyNames())
            {
                try
                {
                    using var classKey = classes.OpenSubKey(classGuidName);
                    if (classKey == null) continue;
                    if (classKey.GetValue("Class") is not string className) continue;
                    // Virtual device classes have no installable drivers.
                    if (className.Equals("PrintQueue", StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (var instanceName in classKey.GetSubKeyNames())
                    {
                        if (!int.TryParse(instanceName, out _)) continue; // numbered instances only
                        try
                        {
                            using var driverKey = classKey.OpenSubKey(instanceName);
                            if (driverKey == null) continue;
                            if (driverKey.GetValue("DriverDesc") is not string desc ||
                                string.IsNullOrWhiteSpace(desc)) continue;

                            var provider = driverKey.GetValue("ProviderName") as string ?? "";
                            var version = driverKey.GetValue("DriverVersion") as string ?? "";
                            var inf = driverKey.GetValue("InfPath") as string ?? "";

                            // OS-vendor inbox drivers ship with Windows itself — they are not
                            // software the employee installed (Microsoft = the OS vendor).
                            if (provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;

                            var dedupKey = $"{desc}|{provider}|{version}";
                            if (!seen.Add(dedupKey)) continue;

                            AddPackage(new InstalledPackage
                            {
                                PackageName = desc,
                                Version = version,
                                Category = "driver",
                                SourceManager = "driver-store",
                                Publisher = provider,
                                Description = className,
                                InstallPath = inf,
                                DetectedAt = DateTime.UtcNow,
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Clean a registry package name: strip (x64)/(64-bit) arch noise and "… with Hotspot &lt;ver&gt;"
    /// version clauses ("Eclipse Temurin JDK with Hotspot 17.0.19+10 (x64)" → "Eclipse Temurin JDK").
    /// </summary>
    private static string CleanRegistryPackageName(string displayName)
    {
        var n = displayName.Trim();
        n = Regex.Replace(n, @"\s*\((?:x64|x86|64-bit|32-bit|User|System)\)\s*$", "", RegexOptions.IgnoreCase);
        var withIdx = n.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
        if (withIdx > 0) n = n[..withIdx].Trim();
        return n.Trim();
    }

    /// <summary>
    /// True if a package with this name (or a name that contains it, both sides ≥4 chars) was
    /// already discovered by a package manager (winget/npm/pip/choco/scoop). Prevents duplicates
    /// like registry "Python 3.14.6 (64-bit)" vs winget "Python 3.14.6 (64-bit)" or "Git".
    /// </summary>
    private bool IsPackageAlreadyKnown(string name)
    {
        if (name.Length < 4)
            return _packages.Any(p => string.Equals(p.PackageName, name, StringComparison.OrdinalIgnoreCase));
        foreach (var p in _packages)
        {
            if (string.Equals(p.PackageName, name, StringComparison.OrdinalIgnoreCase)) return true;
            if (p.PackageName.Length >= 4 &&
                (p.PackageName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                 name.Contains(p.PackageName, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private void AddPackage(InstalledPackage pkg)
    {
        _packages.Add(pkg);
        _knownPackages.Add(pkg.PackageName);
    }
}
