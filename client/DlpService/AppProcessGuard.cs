using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AlphaAITracker.Service;

/// <summary>
/// Fail-closed process sweeper (Windows). AppLocker only blocks <em>new</em> launches and
/// may fail to enforce on some SKUs; this kills already-running EXEs whose path is outside
/// bootstrap A + published <c>allow.apps</c> paths.
/// </summary>
internal static class AppProcessGuard
{
    public sealed record Result(int Examined, int Killed, string Detail);

    /// <summary>
    /// Kill interactive / user EXEs that are not allowed. Never touches kernel/critical
    /// system process names (even when path is unreadable).
    /// </summary>
    public static Result Sweep(IReadOnlyList<AllowAppEntry> allowApps)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(0, 0, "unsupported_os");

        var allow = BuildAllowMatchers(allowApps);
        var killed = 0;
        var examined = 0;
        var examples = new List<string>();

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                using (proc)
                {
                    if (proc.Id <= 4)
                        continue;
                    var name = proc.ProcessName;
                    if (IsNeverKillName(name))
                        continue;

                    string? path = null;
                    try
                    {
                        path = proc.MainModule?.FileName;
                    }
                    catch
                    {
                        // Access denied / protected — leave alone.
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    examined++;
                    if (IsPathAllowed(path, allow))
                        continue;

                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        killed++;
                        if (examples.Count < 8)
                            examples.Add($"{name}:{path}");
                    }
                    catch
                    {
                        // May already have exited or access denied.
                    }
                }
            }
            catch
            {
                // Process exited mid-loop.
            }
        }

        var detail = killed == 0
            ? $"examined={examined} killed=0"
            : $"examined={examined} killed={killed} eg={string.Join(" | ", examples)}";
        return new Result(examined, killed, detail);
    }

    internal static List<PathMatcher> BuildAllowMatchers(IReadOnlyList<AllowAppEntry> allowApps)
    {
        var raw = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in AppAllowEnforcer.BootstrapPaths())
            raw.Add(ExpandMacros(p));
        foreach (var a in allowApps)
        {
            if (!string.IsNullOrWhiteSpace(a.Path))
                raw.Add(ExpandMacros(a.Path.Trim()));
        }

        var list = new List<PathMatcher>();
        foreach (var p in raw)
            list.Add(PathMatcher.Parse(p));
        return list;
    }

    internal static bool IsPathAllowed(string path, List<PathMatcher> allow)
    {
        var full = path.Trim();
        foreach (var m in allow)
        {
            if (m.Matches(full))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Process names we never kill (desktop survival + SCM). Path-based allow still
    /// gates Calculator / Taskmgr because those names are NOT listed here.
    /// </summary>
    private static bool IsNeverKillName(string processName)
    {
        var n = processName.Trim();
        if (n.Equals(ServiceIdentity.ExecutableName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.Equals(ServiceIdentity.ServiceName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.Equals("AlphaAITracker.Service", StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.Equals("client", StringComparison.OrdinalIgnoreCase))
            return true;

        // Critical OS / shell — must stay up even if MainModule is unreadable.
        string[] forever =
        [
            "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
            "svchost", "dwm", "fontdrvhost", "conhost", "explorer", "userinit", "LogonUI",
            "sihost", "taskhostw", "RuntimeBroker", "ApplicationFrameHost", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellHost", "TextInputHost",
            "ctfmon", "dllhost", "backgroundTaskHost", "SystemSettingsBroker", "WidgetService",
            "SecurityHealthService", "MsMpEng", "NisSrv", "SgrmBroker", "Memory Compression",
        ];
        foreach (var f in forever)
        {
            if (n.Equals(f, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ExpandMacros(string path)
    {
        var p = path.Replace('/', '\\');
        p = Environment.ExpandEnvironmentVariables(p);
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        p = ReplacePrefix(p, "%SYSTEM32%", sys);
        p = ReplacePrefix(p, "%WINDIR%", windir);
        p = ReplacePrefix(p, "%PROGRAMFILES%", pf);
        p = ReplacePrefix(p, "%PROGRAMFILES(X86)%", pf86);
        return p;
    }

    private static string ReplacePrefix(string path, string macro, string? value)
    {
        if (string.IsNullOrEmpty(value)) return path;
        if (path.StartsWith(macro, StringComparison.OrdinalIgnoreCase))
            return value + path[macro.Length..];
        return path;
    }

    internal sealed class PathMatcher
    {
        private readonly string _literalOrPrefix;
        private readonly Regex? _rx;
        private readonly bool _prefix;

        private PathMatcher(string literalOrPrefix, Regex? rx, bool prefix)
        {
            _literalOrPrefix = literalOrPrefix;
            _rx = rx;
            _prefix = prefix;
        }

        public static PathMatcher Parse(string pattern)
        {
            var p = pattern.Trim();
            if (p.Contains('*') || p.Contains('?'))
            {
                // Directory form: C:\Foo\*
                if (p.EndsWith("\\*", StringComparison.Ordinal) || p.EndsWith("/*", StringComparison.Ordinal))
                    return new PathMatcher(p[..^1], null, prefix: true);

                var rx = "^" + Regex.Escape(p)
                    .Replace("\\*", ".*", StringComparison.Ordinal)
                    .Replace("\\?", ".", StringComparison.Ordinal) + "$";
                return new PathMatcher(p, new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), false);
            }

            return new PathMatcher(p, null, false);
        }

        public bool Matches(string path)
        {
            if (_rx != null)
                return _rx.IsMatch(path);
            if (_prefix)
                return path.StartsWith(_literalOrPrefix, StringComparison.OrdinalIgnoreCase);
            return path.Equals(_literalOrPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
