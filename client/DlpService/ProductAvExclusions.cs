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

        // Current process (interactive tracker) — cheap.
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
                list.Add(Path.Combine(local, dataFolder));
        }
        catch { /* ignore */ }

        // LocalSystem: every profile under C:\Users\*\AppData\Local\<folder>
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

        if (!Directory.Exists(usersRoot))
            return list;

        try
        {
            foreach (var profile in Directory.EnumerateDirectories(usersRoot))
            {
                var leaf = Path.GetFileName(profile);
                if (leaf is "Public" or "Default" or "Default User" or "All Users") continue;
                list.Add(Path.Combine(profile, "AppData", "Local", dataFolder));
            }
        }
        catch { /* ignore */ }

        return list;
    }
}
