using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace client.Core;

public static partial class ParentProcessResolver
{
    public static Dictionary<int, int> BuildProcessTree()
    {
        var tree = new Dictionary<int, int>();
        var processes = Process.GetProcesses();

        foreach (var proc in processes)
        {
            try
            {
                var ppid = GetParentPid(proc);
                tree[proc.Id] = ppid;
            }
            catch { }
        }

        return tree;
    }

    /// <summary>
    /// Snapshot of pid → base process name for the whole process table. Used together
    /// with <see cref="BuildProcessTree"/> to resolve the TOP-MOST process of a
    /// same-binary family (the app's main window process) — the Windows/macOS analog
    /// of the Linux systemd cgroup scope. Built once per collection cycle; do NOT
    /// call Process.GetProcessById per ancestor.
    /// </summary>
    public static Dictionary<int, string> BuildProcessNameMap()
    {
        var names = new Dictionary<int, string>();
        var processes = Process.GetProcesses();
        foreach (var proc in processes)
        {
            try { names[proc.Id] = proc.ProcessName; } catch { }
        }
        return names;
    }

    public static string? ResolveWindowTitle(
        int pid,
        string processName,
        Dictionary<int, int> processTree,
        Dictionary<int, string?> knownTitles,
        int? foregroundPid)
    {
        if (pid == foregroundPid) return knownTitles.GetValueOrDefault(pid);

        if (knownTitles.TryGetValue(pid, out var ownTitle) && !string.IsNullOrEmpty(ownTitle))
            return ownTitle;

        var visited = new HashSet<int>();
        var current = pid;

        while (current > 0 && visited.Add(current))
        {
            if (processTree.TryGetValue(current, out var ppid) && ppid > 0 && visited.Add(ppid))
            {
                if (knownTitles.TryGetValue(ppid, out var parentTitle) && !string.IsNullOrEmpty(parentTitle))
                {
                    if (ppid == foregroundPid)
                        return parentTitle;

                    try
                    {
                        var parentProc = Process.GetProcessById(ppid);
                        var parentName = parentProc.ProcessName;
                        if (parentName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                        {
                            current = ppid;
                            continue;
                        }
                        return $"{parentTitle} [{processName}]";
                    }
                    catch
                    {
                        return $"{parentTitle} [{processName}]";
                    }
                }
                current = ppid;
            }
            else
            {
                break;
            }
        }

        return null;
    }

    /// <summary>Get the command line for a process (public access for headless subprocess detection).</summary>
    public static string? GetProcessCommandLine(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return GetWindowsCommandLine(pid);
            else if (OperatingSystem.IsLinux())
                return GetLinuxCommandLine(pid);
            else if (OperatingSystem.IsMacOS())
                return GetMacOSCommandLine(pid);
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Extracts the browser profile name from a process command line.
    /// Supports Chromium-based browsers (--profile-directory=), Firefox (-P or -profile),
    /// and extraction from --user-data-dir paths.
    /// </summary>
    public static string? GetBrowserProfile(string processName, int pid)
    {
        try
        {
            string? cmdline = null;

            if (OperatingSystem.IsWindows())
                cmdline = GetWindowsCommandLine(pid);
            else if (OperatingSystem.IsLinux())
                cmdline = GetLinuxCommandLine(pid);
            else if (OperatingSystem.IsMacOS())
                cmdline = GetMacOSCommandLine(pid);

            if (cmdline == null) return null;

            // 1. Check for Chromium --profile-directory (Chrome, Chromium, Brave, Edge, Opera, Vivaldi)
            var match = ChromiumProfileRegex().Match(cmdline);
            if (match.Success)
            {
                var profile = match.Groups[1].Value;
                return profile.Replace("\"", "");
            }

            // 2. Check for Firefox -P <profile> or -profile <profile_path>
            var ffMatch = FirefoxProfileRegex().Match(cmdline);
            if (ffMatch.Success)
            {
                var profile = ffMatch.Groups[1].Value;
                return $"firefox:{profile.Replace("\"", "")}";
            }

            // 3. Check for --user-data-dir (fallback for other browsers)
            var uddMatch = UserDataDirRegex().Match(cmdline);
            if (uddMatch.Success)
            {
                var path = uddMatch.Groups[1].Value.Replace("\"", "");
                var dirName = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(dirName))
                    return dirName;
            }
        }
        catch { }

        return null;
    }

    private static int GetParentPid(Process proc)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return GetParentPidLinux(proc.Id);
            else if (OperatingSystem.IsWindows())
                return GetParentPidWindows(proc);
            else if (OperatingSystem.IsMacOS())
                return GetParentPidMacOS(proc.Id);
        }
        catch { }
        return 0;
    }

    private static int GetParentPidLinux(int pid)
    {
        try
        {
            var status = File.ReadAllText($"/proc/{pid}/status");
            var match = PpidRegex().Match(status);
            return match.Success ? int.Parse(match.Groups[1].Value) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int GetParentPidWindows(Process proc)
    {
        try
        {
            var hSnapshot = CreateToolhelp32Snapshot(2, 0);
            if (hSnapshot == -1) return 0;

            try
            {
                var entry = new PROCESSENTRY32();
                entry.dwSize = Marshal.SizeOf<PROCESSENTRY32>();

                if (Process32First(hSnapshot, ref entry))
                {
                    do
                    {
                        if (entry.th32ProcessID == proc.Id)
                            return entry.th32ParentProcessID;
                    } while (Process32Next(hSnapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(hSnapshot);
            }
        }
        catch { }
        return 0;
    }

    private static int GetParentPidMacOS(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ps",
                Arguments = $"-o ppid= -p {pid}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return 0;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
            return int.TryParse(output, out var ppid) ? ppid : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string? GetWindowsCommandLine(int pid)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = $"process where processid={pid} get commandline /format:csv",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Contains(',') && !trimmed.StartsWith("Node"))
                {
                    var cmd = trimmed[(trimmed.IndexOf(',') + 1)..].Trim();
                    if (!string.IsNullOrEmpty(cmd)) return cmd;
                }
            }
        }
        catch { }
        return null;
    }

    private static string? GetLinuxCommandLine(int pid)
    {
        try
        {
            var parts = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetMacOSCommandLine(int pid)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ps",
                    Arguments = $"-o args= -p {pid}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"PPid:\s+(\d+)")]
    private static partial Regex PpidRegex();

    [GeneratedRegex(@"--profile-directory=(?:""([^""]*)""|(\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex ChromiumProfileRegex();

    [GeneratedRegex(@"-P\s+(?:""([^""]*)""|(\S+))|--profile\s+(?:""([^""]*)""|(\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex FirefoxProfileRegex();

    [GeneratedRegex(@"--user-data-dir=(?:""([^""]*)""|(\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex UserDataDirRegex();

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreateToolhelp32Snapshot(int flags, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(int hSnapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(int hSnapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(int hObject);
}
