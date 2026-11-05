namespace client.Core;

/// <summary>
/// Disambiguates multiple windows/instances of the same app by deriving a short
/// human label from the root process's command line at session creation time.
/// Once cgroup dedup correctly splits two VS Code windows (or two Chrome profiles)
/// into separate sessions, this label is what makes them distinguishable in reports.
///
/// VS Code → the workspace/project folder name.
/// Chrome/Chromium-family → the --profile-directory value.
/// Everything else → null (no label).
/// </summary>
public static class SessionLabelResolver
{
    public static string? Resolve(string processName, int rootPid, client.Core.BrowserAccessibility.IBrowserRegistry browserRegistry)
    {
        if (browserRegistry is null || !browserRegistry.IsBrowser(processName))
            return processName switch
            {
                "code" => ResolveVsCodeProject(rootPid),
                _ => null,
            };
        return ResolveBrowserProfile(rootPid);
    }

    /// <summary>
    /// Primary signal: scan argv for a real, existing directory/file path.
    /// VS Code's launch command almost always includes the workspace folder
    /// as one of the argv tokens for a genuinely new window.
    /// If the root PID is a subprocess (extension host, renderer — argv has no path),
    /// walk the PPID chain a few levels and try the parent's argv (the main 'code'
    /// process carries the workspace folder).
    /// </summary>
    private static string? ResolveVsCodeProject(int pid)
    {
        var current = pid;
        for (var depth = 0; depth < 5 && current > 0; depth++)
        {
            var argv = ReadCmdline(current);
            var pathToken = argv.LastOrDefault(a => Directory.Exists(a) || File.Exists(a));
            if (pathToken != null)
            {
                var folder = Directory.Exists(pathToken)
                    ? pathToken
                    : Path.GetDirectoryName(pathToken);
                if (!string.IsNullOrEmpty(folder))
                    return Path.GetFileName(folder.TrimEnd('/'));
            }

            current = GetParentPid(current);
        }
        return null;
    }

    private static int GetParentPid(int pid)
    {
        try
        {
            var status = File.ReadAllText($"/proc/{pid}/status");
            var marker = "PPid:\t";
            var idx = status.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return 0;
            var lineEnd = status.IndexOf('\n', idx);
            var value = status.Substring(idx + marker.Length, lineEnd - idx - marker.Length).Trim();
            return int.TryParse(value, out var ppid) ? ppid : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Chrome/Firefox profile: --profile-directory="Profile 1" / -P "profile-name"</summary>
    private static string? ResolveBrowserProfile(int pid)
    {
        var argv = ReadCmdline(pid);
        var match = argv.FirstOrDefault(a => a.StartsWith("--profile-directory=", StringComparison.Ordinal));
        if (match != null)
            return match.Split('=', 2)[1].Trim('"');
        match = argv.FirstOrDefault(a => a.StartsWith("-P", StringComparison.Ordinal));
        if (match != null)
            return match.Split(' ', 2).ElementAtOrDefault(1)?.Trim('"');
        match = argv.FirstOrDefault(a => a.StartsWith("--profile=", StringComparison.Ordinal));
        if (match != null)
            return match.Split('=', 2)[1].Trim('"');
        return null;
    }

    private static List<string> ReadCmdline(int pid)
    {
        try
        {
            var path = $"/proc/{pid}/cmdline";
            if (!File.Exists(path)) return new();
            return File.ReadAllText(path).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch
        {
            return new();
        }
    }
}
