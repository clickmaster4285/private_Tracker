namespace AlphaAITracker.Service;

/// <summary>
/// Product folders and binaries that must stay excluded from Microsoft Defender
/// scanning so the tracker / DLP service are never quarantined or blocked.
/// Derived from <see cref="ServiceIdentity"/> — never hardcodes the display name.
/// </summary>
internal static class ProductAvExclusions
{
    public sealed record Set(
        IReadOnlyList<string> Paths,
        IReadOnlyList<string> Processes,
        IReadOnlyList<string> CfaAllowedApps);

    /// <summary>
    /// Builds the product exclusion set for the current machine layout.
    /// Paths that do not exist yet are still included (ProgramData / LocalAppData)
    /// so Defender never races a first write; Program Files roots are only listed
    /// when the install folder is present.
    /// </summary>
    public static Set Build()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cfaApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var display = ServiceIdentity.DisplayName;
        var dataFolder = ServiceIdentity.DataFolderName;
        var exeName = ServiceIdentity.ExecutableName;
        if (string.IsNullOrWhiteSpace(exeName)) exeName = "client";
        var clientExeFile = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeName
            : exeName + ".exe";
        var serviceExeFile = ServiceIdentity.ServiceName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? ServiceIdentity.ServiceName
            : ServiceIdentity.ServiceName + ".exe";

        // Program Files (+ x86) install dirs when present, plus wherever this service
        // actually runs from (custom install dir / older x86 installs).
        foreach (var install in InstallRoots(display))
        {
            paths.Add(NormalizePath(install));

            var clientExe = Path.Combine(install, clientExeFile);
            if (File.Exists(clientExe))
            {
                processes.Add(NormalizePath(clientExe));
                cfaApps.Add(NormalizePath(clientExe));
            }

            var serviceExe = Path.Combine(install, "Service", serviceExeFile);
            if (File.Exists(serviceExe))
            {
                processes.Add(NormalizePath(serviceExe));
                cfaApps.Add(NormalizePath(serviceExe));
            }
        }

        // Always exclude ProgramData product root (dlp / updates / control).
        try
        {
            var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(pd))
                paths.Add(NormalizePath(Path.Combine(pd, dataFolder)));
        }
        catch { /* ignore */ }

        // Per-user LocalAppData product roots (SQLite, face models, screenshots…).
        foreach (var local in EnumerateUserLocalAppDataRoots(dataFolder))
            paths.Add(NormalizePath(local));

        // Never let a broad path ride in on a product root (defence in depth).
        paths.RemoveWhere(IsBroadOrUnsafeExclusion);

        // Image-name fallbacks (Defender accepts bare process names).
        processes.Add(clientExeFile);
        processes.Add(serviceExeFile);

        return new Set(
            paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            processes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            cfaApps.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Flat allowlist strings (paths + processes) for the remover side.</summary>
    public static IReadOnlyList<string> FlattenAllowlist(Set set)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in set.Paths) all.Add(p);
        foreach (var p in set.Processes) all.Add(p);
        return all.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True when <paramref name="path"/> sits inside any product folder.</summary>
    public static bool IsProductPath(string? path, Set set)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim();
        // Defender event / threat resources prefix files with "file:_".
        if (p.StartsWith("file:_", StringComparison.OrdinalIgnoreCase)) p = p[6..];
        p = NormalizePath(p);
        if (p.Length == 0) return false;
        foreach (var root in set.Paths)
        {
            if (root.Length == 0) continue;
            if (p.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
            if (p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static IEnumerable<string> InstallRoots(string display)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pf in ProgramFilesRoots())
        {
            var install = NormalizePath(Path.Combine(pf, display));
            if (Directory.Exists(install) && seen.Add(install))
                yield return install;
        }

        // Service binary lives in <install>\Service\ — its parent is the real install root.
        string? self = null;
        try
        {
            var baseDir = NormalizePath(AppContext.BaseDirectory);
            if (Path.GetFileName(baseDir).Equals("Service", StringComparison.OrdinalIgnoreCase))
                self = Path.GetDirectoryName(baseDir);
        }
        catch { /* ignore */ }
        if (!string.IsNullOrWhiteSpace(self))
        {
            var n = NormalizePath(self);
            if (Directory.Exists(n) && seen.Add(n))
                yield return n;
        }
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var t = path.Trim();
        try { t = Path.GetFullPath(t); } catch { /* keep trimmed */ }
        return t.TrimEnd('\\', '/');
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            string? root = null;
            try { root = Environment.GetEnvironmentVariable(env); } catch { /* ignore */ }
            if (string.IsNullOrWhiteSpace(root)) continue;
            var n = NormalizePath(root);
            if (n.Length == 0 || !seen.Add(n)) continue;
            yield return n;
        }
    }

    private static IEnumerable<string> EnumerateUserLocalAppDataRoots(string dataFolder)
    {
        var list = new List<string>();

        // Current process — only when it is a real interactive user. LocalSystem's
        // LocalAppData is C:\Windows\system32\config\systemprofile\…, never a tracker dir.
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local) && !IsUnderWindowsDir(local))
                list.Add(Path.Combine(local, dataFolder));
        }
        catch { /* ignore */ }

        // Every real user profile. The old code walked "..\" of LocalSystem's profile, i.e.
        // C:\Windows\system32\config, and excluded Journal / RegBack / systemprofile paths.
        foreach (var profile in EnumerateUserProfileDirs())
            list.Add(Path.Combine(profile, "AppData", "Local", dataFolder));

        return list;
    }

    /// <summary>
    /// Interactive user profile folders from the OS profile registry (ProfileList), limited to
    /// domain/local user SIDs (S-1-5-21-…). Service / system profiles under the Windows
    /// directory are never returned. Falls back to the ProfilesDirectory listing.
    /// </summary>
    internal static IReadOnlyList<string> EnumerateUserProfileDirs()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return result;

        string? profilesDir = null;
        try
        {
            using var list = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list is not null)
            {
                profilesDir = Environment.ExpandEnvironmentVariables(
                    list.GetValue("ProfilesDirectory") as string ?? "");
                foreach (var sid in list.GetSubKeyNames())
                {
                    if (!IsUserSid(sid)) continue;
                    using var k = list.OpenSubKey(sid);
                    var raw = k?.GetValue("ProfileImagePath") as string;
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var dir = NormalizePath(Environment.ExpandEnvironmentVariables(raw));
                    if (IsUsableProfileDir(dir) && seen.Add(dir))
                        result.Add(dir);
                }
            }
        }
        catch { /* fall back below */ }

        if (result.Count == 0)
        {
            try
            {
                var root = string.IsNullOrWhiteSpace(profilesDir)
                    ? Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users")
                    : profilesDir;
                if (Directory.Exists(root))
                {
                    foreach (var profile in Directory.EnumerateDirectories(root))
                    {
                        var leaf = Path.GetFileName(profile);
                        if (leaf is "Public" or "Default" or "Default User" or "All Users") continue;
                        var dir = NormalizePath(profile);
                        if (IsUsableProfileDir(dir) && seen.Add(dir))
                            result.Add(dir);
                    }
                }
            }
            catch { /* ignore */ }
        }
        return result;
    }

    /// <summary>S-1-5-21-* are local / domain user accounts; S-1-5-18/19/20 are system.</summary>
    internal static bool IsUserSid(string? sid) =>
        !string.IsNullOrWhiteSpace(sid) &&
        sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) &&
        !sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsableProfileDir(string dir) =>
        dir.Length > 0 && !IsUnderWindowsDir(dir) && Directory.Exists(dir);

    internal static bool IsUnderWindowsDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string win;
        try { win = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)); }
        catch { return false; }
        if (win.Length == 0) return false;
        var p = NormalizePath(path);
        return p.Equals(win, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // ── Broad / unsafe exclusion guard ──────────────────────────────────────

    /// <summary>
    /// True for exclusions that would blind Defender to whole classes of malware: drive roots,
    /// the Windows directory, the Users / profile roots and any Temp folder (the most common
    /// malware drop zone). Such paths are never added and never kept as "allowed".
    /// </summary>
    internal static bool IsBroadOrUnsafeExclusion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var p = NormalizePath(raw);
        if (p.Length == 0) return true;
        // "C:\" normalizes to "C:" — a whole drive, always broad.
        if (p.Length == 2 && p[1] == ':' && char.IsLetter(p[0])) return true;
        // Bare image names ("client.exe") are process exclusions, not folders.
        if (!p.Contains('\\') && !p.Contains('/')) return false;

        string? root = null;
        try { root = Path.GetPathRoot(p); } catch { /* ignore */ }
        if (!string.IsNullOrEmpty(root) && p.Equals(NormalizePath(root), StringComparison.OrdinalIgnoreCase))
            return true; // C:\

        try
        {
            var win = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (win.Length > 0 && (p.Equals(win, StringComparison.OrdinalIgnoreCase) ||
                                   p.Equals(win + "\\System32", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        catch { /* ignore */ }

        // Any Temp folder (or anything inside one): %TEMP%, %SystemRoot%\Temp, AppData\Local\Temp.
        var segments = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
                              s.Equals("Tmp", StringComparison.OrdinalIgnoreCase)))
            return true;

        // Users root and whole user profiles.
        var leaf = segments.Length > 0 ? segments[^1] : "";
        if (segments.Length == 2 && segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase))
            return true;
        if (segments.Length == 3 && segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase))
            return true;
        if (leaf.Equals("AppData", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("Local", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2 &&
            segments[^2].Equals("AppData", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("Roaming", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2 &&
            segments[^2].Equals("AppData", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    // ── Cached build ────────────────────────────────────────────────────────

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly object CacheGate = new();
    private static Set? _cached;
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// <see cref="Build"/> memoized for a few minutes. The guard tick and every Defender event
    /// need the set; rebuilding it walks the file system and the profile registry each time.
    /// </summary>
    public static Set BuildCached()
    {
        lock (CacheGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cached is null || now - _cachedAt >= CacheTtl)
            {
                _cached = Build();
                _cachedAt = now;
            }
            return _cached;
        }
    }
}
