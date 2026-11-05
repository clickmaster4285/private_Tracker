using System.Diagnostics;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Platform.MacOS;

public class ProcessCollector : IActivityCollector
{
    private readonly string _machineId;

    public ProcessCollector(string machineId)
    {
        _machineId = machineId;
    }

    public Task<IReadOnlyList<ActivityLog>> CollectAsync(CancellationToken ct)
    {
        var logs = new List<ActivityLog>();
        var now = DateTime.UtcNow;
        var currentUser = Environment.UserName;
        var foreground = GetActiveWindowInfo();

        var processes = Process.GetProcesses();
        var procTree = ParentProcessResolver.BuildProcessTree();
        var knownTitles = new Dictionary<int, string?>();

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;
            if (!ProcessFilter.IsUserProcess(proc, false)) continue;

            try
            {
                var pid = proc.Id;
                if (foreground?.pid == pid && foreground?.title != null)
                    knownTitles[pid] = foreground.Value.title;
            }
            catch { }
        }

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;
            if (!ProcessFilter.IsUserProcess(proc, false)) continue;

            try
            {
                var name = proc.ProcessName;
                var pid = proc.Id;

                long mem = 0;
                try { mem = proc.WorkingSet64; } catch { }

                // Filter out headless Chromium/Electron subprocesses (renderer, GPU, utility,
                // zygote, etc.) by querying the full command line via `ps`.
                // ProcessName returns just "Google Chrome" without args on macOS, so we must
                // query `ps -o command=` to detect --type= flags.
                var cmdline = GetProcessCommandLine(pid);
                if (AppProcessClassifier.IsHeadlessSubprocess(cmdline))
                    continue;

                var resolvedTitle = ParentProcessResolver.ResolveWindowTitle(
                    pid, name, procTree, knownTitles, foreground?.pid);

                var profile = ParentProcessResolver.GetBrowserProfile(name, pid);

                var title = profile != null
                    ? $"{resolvedTitle} [{profile}]"
                    : resolvedTitle;

                logs.Add(new ActivityLog
                {
                    MachineId = _machineId,
                    Timestamp = now,
                    ProcessName = name,
                    WindowTitle = title,
                    ProcessId = pid,
                    CpuPercent = 0,
                    MemoryBytes = mem,
                    IsForeground = foreground?.pid == pid,
                    UserName = currentUser,
                    Platform = "macOS",
                    SessionId = SessionInfo.SessionId
                });
            }
            catch
            {
                // process exited
            }
        }

        return Task.FromResult<IReadOnlyList<ActivityLog>>(logs);
    }

    public static IReadOnlyDictionary<string, bool> GetPermissionStatus()
    {
        var status = new Dictionary<string, bool>();
        status["osascript"] = HasOsascript();
        status["accessibility_permission"] = CheckAccessibilityPermission();
        return status;
    }

    private static bool HasOsascript()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "osascript",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            return proc.WaitForExit(1000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the full command line of a process on macOS via `ps -o command=`.
    /// Returns null if the process has exited or the command fails.
    /// Used to detect --type= flags in Chromium/Electron subprocesses.
    /// </summary>
    private static string? GetProcessCommandLine(int pid)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ps",
                    Arguments = $"-o command= -p {pid}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    public static bool CheckAccessibilityPermission()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = "-e 'tell application \"System Events\" to get name of first application process whose frontmost is true'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0 && string.IsNullOrEmpty(err);
        }
        catch
        {
            return false;
        }
    }

    private static (int pid, string? title)? GetActiveWindowInfo()
    {
        return GetActiveViaOsascript();
    }

    private static (int pid, string? title)? GetActiveViaOsascript()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = "-e 'tell application \"System Events\" to set frontApp to first application process whose frontmost is true' -e 'set appName to name of frontApp' -e 'set appTitle to title of frontApp' -e 'set appPID to unix id of frontApp' -e 'return appName & \"|\" & appTitle & \"|\" & (appPID as string)'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0 || string.IsNullOrEmpty(output))
                return null;

            var parts = output.Split('|', 3);
            if (parts.Length < 3) return null;

            if (!int.TryParse(parts[2].Trim(), out var pid) || pid <= 0)
                return null;

            var title = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1].Trim();
            return (pid, title);
        }
        catch
        {
            return null;
        }
    }
}
