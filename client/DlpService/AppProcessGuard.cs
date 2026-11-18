using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

    private static readonly object MatcherGate = new();
    private static string _matcherFingerprint = "\0";
    private static List<PathMatcher> _cachedMatchers = new();

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// Kill interactive / user EXEs that are not allowed. Never touches kernel/critical
    /// system process names (even when path is unreadable).
    /// </summary>
    public static Result Sweep(IReadOnlyList<AllowAppEntry> allowApps, Action<string, string>? onKilled = null)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(0, 0, "unsupported_os");

        var allow = GetAllowMatchers(allowApps);
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

                    var sessionId = SafeSessionId(proc);
                    // Session 0 services: only force-check denied interactive names (should be 0).
                    // Skip the rest — MainModule on services is expensive and usually access-denied.
                    var interactiveDenied = IsDeniedInteractiveName(name) && sessionId > 0;
                    if (sessionId == 0 && !interactiveDenied)
                        continue;

                    string? path = null;
                    try
                    {
                        path = TryGetProcessImagePath(proc.Id) ?? proc.MainModule?.FileName;
                    }
                    catch
                    {
                        if (!interactiveDenied)
                            continue; // Access denied / protected — leave alone.
                    }

                    examined++;
                    if (!string.IsNullOrWhiteSpace(path) && IsPathAllowed(path, allow))
                        continue;
                    if (string.IsNullOrWhiteSpace(path) && !interactiveDenied)
                        continue;

                    // Anything the tracker or the service started (winget, its helper
                    // processes, the product's own PowerShell) stays up. A Start-menu
                    // or Explorer launch has no product parent and is still killed.
                    if (sessionId == 0 || IsOwnedByProduct(proc.Id))
                        continue;

                    // Spare Windows System32 / SystemApps and bootstrap shell Appx only.
                    // Store / Calculator / Edge packages under WindowsApps are killed
                    // unless already matched by allow.apps above.
                    if (!IsDeniedInteractiveName(name) && IsWindowsComponent(path, name))
                        continue;

                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        killed++;
                        // Product dialog only for clear user EXEs (shells / Settings /
                        // non-Windows paths). Never for Windows component leftovers.
                        if (ShouldRaiseBlockNotice(path, name))
                            onKilled?.Invoke(name, path ?? name);
                        if (examples.Count < 8)
                            examples.Add($"{name}:{path ?? "?"}");
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

    /// <summary>Stable fingerprint of allow.apps paths used for matcher cache + sweep cadence.</summary>
    public static string AllowAppsFingerprint(IReadOnlyList<AllowAppEntry> allowApps)
    {
        var sb = new StringBuilder();
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in allowApps)
        {
            if (!string.IsNullOrWhiteSpace(a.Path))
                paths.Add(a.Path.Trim());
        }
        foreach (var p in paths)
            sb.Append(p).Append('\n');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static List<PathMatcher> GetAllowMatchers(IReadOnlyList<AllowAppEntry> allowApps)
    {
        var fp = AllowAppsFingerprint(allowApps);
        lock (MatcherGate)
        {
            if (fp == _matcherFingerprint && _cachedMatchers.Count > 0)
                return _cachedMatchers;
            _cachedMatchers = BuildAllowMatchers(allowApps);
            _matcherFingerprint = fp;
            return _cachedMatchers;
        }
    }

    internal static List<PathMatcher> BuildAllowMatchers(IReadOnlyList<AllowAppEntry> allowApps)
    {
        var raw = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in AppAllowEnforcer.BootstrapPaths())
            AddExpandedPaths(raw, p);
        foreach (var a in allowApps)
        {
            if (!string.IsNullOrWhiteSpace(a.Path))
                AddExpandedPaths(raw, a.Path.Trim());
        }

        var list = new List<PathMatcher>();
        foreach (var p in raw)
            list.Add(PathMatcher.Parse(p));
        return list;
    }

    /// <summary>
    /// Expand AppLocker-style macros to concrete filesystem paths. AppLocker's
    /// %PROGRAMFILES% covers both Program Files roots — emit matchers for both so the
    /// sweeper agrees with AppLocker (Edge typically lives under Program Files (x86)).
    /// Also expands legacy %PROGRAMFILES(X86)% from older policy XML.
    /// </summary>
    private static void AddExpandedPaths(SortedSet<string> raw, string path)
    {
        var p = path.Replace('/', '\\').Trim();
        p = Environment.ExpandEnvironmentVariables(p);
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (StartsWithMacro(p, "%SYSTEM32%"))
        {
            raw.Add(ReplacePrefix(p, "%SYSTEM32%", sys));
            return;
        }
        if (StartsWithMacro(p, "%WINDIR%"))
        {
            raw.Add(ReplacePrefix(p, "%WINDIR%", windir));
            return;
        }
        if (StartsWithMacro(p, "%PROGRAMFILES(X86)%"))
        {
            raw.Add(ReplacePrefix(p, "%PROGRAMFILES(X86)%", pf86));
            return;
        }
        if (StartsWithMacro(p, "%PROGRAMFILES%"))
        {
            var rest = p["%PROGRAMFILES%".Length..];
            if (!string.IsNullOrEmpty(pf))
                raw.Add(pf + rest);
            if (!string.IsNullOrEmpty(pf86) &&
                !string.Equals(pf, pf86, StringComparison.OrdinalIgnoreCase))
                raw.Add(pf86 + rest);
            return;
        }

        raw.Add(p);
    }

    private static bool StartsWithMacro(string path, string macro) =>
        path.StartsWith(macro, StringComparison.OrdinalIgnoreCase);

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
    /// Process names we never kill (SCM + desktop shell). Settings / PowerShell are NOT
    /// listed — under lockdown the sweeper kills them unless published on allow.apps
    /// (interactive shells are force-killed even when path lookup fails).
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

        // Critical OS + Explorer shell — must stay up even if MainModule is unreadable.
        string[] forever =
        [
            "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
            "svchost", "dwm", "fontdrvhost", "conhost", "explorer", "userinit", "LogonUI",
            "sihost", "taskhostw", "RuntimeBroker", "ApplicationFrameHost", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellHost", "TextInputHost",
            "ctfmon", "dllhost", "backgroundTaskHost",
            "SecurityHealthService", "MsMpEng", "NisSrv", "SgrmBroker", "Memory Compression",
        ];
        foreach (var f in forever)
        {
            if (n.Equals(f, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Shells / Settings / Control Panel that must not stay open for interactive users
    /// until published on the server allow list. Explorer is never listed (desktop survival).
    /// </summary>
    private static bool IsDeniedInteractiveName(string processName)
    {
        string[] denied =
        [
            "powershell", "powershell_ise", "pwsh",
            "SystemSettings", "SystemSettingsAdminFlows", "SystemSettingsBroker",
            "control", "mmc", "cmd",
            "WindowsTerminal", "wt",
            // Windows Security app — the UI toggle for real-time protection / exclusions.
            "SecHealthUI",
        ];
        foreach (var d in denied)
        {
            if (processName.Equals(d, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the backup killer may show the product allowlist dialog. False for
    /// Windows System32 / bootstrap shell Appx. Store / Calculator under WindowsApps
    /// may raise a notice when killed (strict shell-only).
    /// </summary>
    internal static bool ShouldRaiseBlockNotice(string? path, string processName)
    {
        if (IsWindowsComponent(path, processName))
            return false;
        return true;
    }

    /// <summary>
    /// Processes under the Windows directory / SystemApps, plus bootstrap shell Appx
    /// under WindowsApps. Store / Calculator / Edge / Xbox packages are NOT spared —
    /// AppLocker denies new launches; this killer removes already-running ones unless
    /// the path is on published allow.apps (checked before this call).
    /// </summary>
    private static bool IsWindowsComponent(string? path, string processName)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windir))
            {
                var root = windir.TrimEnd('\\') + "\\";
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Spare only bootstrap shell Appx (Start/Search/Lock). Kill Store/Calculator/etc.
            if (IsUnderWindowsApps(path))
                return IsBootstrapWindowsAppsPackage(path);
        }

        return processName.Equals("LocationNotificationWindows", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("LockApp", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("SearchApp", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("SecurityHealthHost", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("SecurityHealthSystray", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a WindowsApps path belongs to a bootstrap shell Appx product family.
    /// </summary>
    internal static bool IsBootstrapWindowsAppsPackage(string path)
    {
        foreach (var product in AppAllowEnforcer.BootstrapAppxProductNames)
        {
            if (string.IsNullOrWhiteSpace(product))
                continue;
            // ...\WindowsApps\Microsoft.Windows.Search_cw5n1h2txyewy\...
            if (path.Contains("\\" + product + "_", StringComparison.OrdinalIgnoreCase)
                || path.Contains("\\" + product + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Store package root under either Program Files tree.</summary>
    private static bool IsUnderWindowsApps(string path)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf))
        {
            var root = Path.Combine(pf.TrimEnd('\\'), "WindowsApps") + "\\";
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (!string.IsNullOrEmpty(pf86)
            && !string.Equals(pf, pf86, StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.Combine(pf86.TrimEnd('\\'), "WindowsApps") + "\\";
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // Some builds report \\?\ or short paths; also match the folder segment.
        return path.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProductProcessName(string processName)
    {
        if (processName.Equals(ServiceIdentity.ExecutableName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (processName.Equals("client", StringComparison.OrdinalIgnoreCase))
            return true;
        if (processName.Equals(ServiceIdentity.ServiceName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (processName.Equals("AlphaAITracker.Service", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>Start menu, Explorer, and Search. A shell under one of these was opened by hand.</summary>
    private static bool IsManualShellParent(string processName)
    {
        return processName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("SearchApp", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("OpenWith", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("wt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when this process was started by the tracker or the service
    /// (parent, or a parent further up, is the product).
    /// </summary>
    private static bool IsOwnedByProduct(int pid)
    {
        var current = pid;
        var seen = new HashSet<int>();
        for (var depth = 0; depth < 8; depth++)
        {
            if (!seen.Add(current))
                return false;
            var parent = ParentPid(current);
            if (parent <= 4)
                return false;
            var parentName = ProcessNameFromPid(parent);
            if (string.IsNullOrEmpty(parentName))
                return false;
            if (IsProductProcessName(parentName))
                return true;
            if (IsManualShellParent(parentName))
                return false;
            current = parent;
        }
        return false;
    }

    private static string? ProcessNameFromPid(int pid)
    {
        var path = TryGetProcessImagePath(pid);
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFileNameWithoutExtension(path);
        }
        catch
        {
            return null;
        }
    }

    private static int ParentPid(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return 0;
        try
        {
            var info = new ProcessBasicInformation();
            var len = Marshal.SizeOf<ProcessBasicInformation>();
            var status = NtQueryInformationProcess(handle, 0, ref info, len, out _);
            if (status != 0)
                return 0;
            var parent = info.InheritedFromUniqueProcessId.ToInt64();
            if (parent <= 0 || parent > int.MaxValue)
                return 0;
            return (int)parent;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static int SafeSessionId(Process proc)
    {
        try { return proc.SessionId; }
        catch { return -1; }
    }

    private static string? TryGetProcessImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(handle, 0, sb, ref size))
                return null;
            return sb.ToString(0, (int)size);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string ReplacePrefix(string path, string macro, string? value)
    {
        if (string.IsNullOrEmpty(value)) return path;
        if (path.StartsWith(macro, StringComparison.OrdinalIgnoreCase))
            return value + path[macro.Length..];
        return path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, uint flags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

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
