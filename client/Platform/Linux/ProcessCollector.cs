using System.Diagnostics;
using System.Text.RegularExpressions;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Platform.Linux;

public partial class ProcessCollector : IActivityCollector
{
    private readonly string _machineId;
    private static readonly HashSet<string> PermissionCache = new();

    public ProcessCollector(string machineId)
    {
        _machineId = machineId;
    }

    public async Task<IReadOnlyList<ActivityLog>> CollectAsync(CancellationToken ct)
    {
        var logs = new List<ActivityLog>();
        var now = DateTime.UtcNow;
        var currentUser = Environment.UserName;
        var foreground = GetActiveWindowInfo();

        var processes = Process.GetProcesses();
        var procTree = ParentProcessResolver.BuildProcessTree();
        var knownTitles = new Dictionary<int, string?>();

        // Enumerate ALL X11/XWayland windows via _NET_CLIENT_LIST
        // (Linux equivalent of EnumWindows on Windows)
        var x11Titles = EnumerateAllWindowTitles();
        foreach (var kvp in x11Titles)
            knownTitles[kvp.Key] = kvp.Value!;

        // Override with foreground title if detected (portal/atspi/etc.)
        if (foreground?.title != null && foreground.Value.pid > 0)
            knownTitles[foreground.Value.pid] = foreground.Value.title;

        var cpuBefore = SnapshotCpuTimes(processes);
        await Task.Delay(100, ct);
        processes = Process.GetProcesses();
        var cpuAfter = SnapshotCpuTimes(processes);

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;
            // Use checkSession:false on Linux — session IDs are unreliable on Wayland
            // and would filter out all user processes, resulting in 0 logs.
            if (!ProcessFilter.IsUserProcess(proc, false, false)) continue;

            try
            {
                var name = proc.ProcessName;
                var pid = proc.Id;

                // Filter out headless Chromium/Electron subprocesses (renderer, GPU, utility,
                // zygote, etc.) by reading /proc/{pid}/cmdline for --type= flags.
                // Process.ProcessName returns just "chrome" for ALL Chrome processes on Linux,
                // so cmdline inspection is the only reliable way to identify subprocesses.
                var cmdline = ReadProcessCmdline(pid);
                if (AppProcessClassifier.IsHeadlessSubprocess(cmdline))
                    continue;

                long mem = 0;
                try { mem = proc.WorkingSet64; } catch { }

                double cpu = 0;
                if (cpuBefore.TryGetValue(pid, out var prev) &&
                    cpuAfter.TryGetValue(pid, out var curr))
                {
                    cpu = (curr - prev).TotalSeconds / (Environment.ProcessorCount * 0.1) * 100;
                }

                var resolvedTitle = ParentProcessResolver.ResolveWindowTitle(
                    pid, name, procTree, knownTitles, foreground?.pid);

                if (resolvedTitle == null && foreground?.pid == pid)
                    resolvedTitle = foreground?.title;

                // Fallback to X11 window title if available
                resolvedTitle ??= x11Titles.GetValueOrDefault(pid);

                var profile = ParentProcessResolver.GetBrowserProfile(name, pid);

                var title = profile != null && resolvedTitle != null
                    ? $"{resolvedTitle} [{profile}]"
                    : resolvedTitle ?? profile;

                logs.Add(new ActivityLog
                {
                    MachineId = _machineId,
                    Timestamp = now,
                    ProcessName = name,
                    WindowTitle = title,
                    ProcessId = pid,
                    CpuPercent = Math.Round(cpu, 1),
                    MemoryBytes = mem,
                    IsForeground = foreground?.pid == pid,
                    UserName = currentUser,
                    Platform = "Linux",
                    SessionId = SessionInfo.SessionId
                });
            }
            catch
            {
                // process exited
            }
        }

        return logs;
    }

    private static Dictionary<int, TimeSpan> SnapshotCpuTimes(Process[] processes)
    {
        var snap = new Dictionary<int, TimeSpan>();
        foreach (var proc in processes)
        {
            try { snap[proc.Id] = proc.TotalProcessorTime; } catch { }
        }
        return snap;
    }

    private static Dictionary<int, string?> EnumerateAllWindowTitles()
    {
        var map = new Dictionary<int, string?>();
        var seenPids = new HashSet<int>();

        // 🟡 Step 1: Try GNOME Shell introspection — works on Wayland for ALL windows (Firefox, Chrome, etc.)
        // This is the primary method on Wayland where xprop _NET_CLIENT_LIST only returns XWayland windows.
        // The GNOME Shell org.gnome.Shell.Introspect.GetWindows method returns ALL windows on Wayland.
        // We parse both the 'wm-pid' and 'title' fields using ShellPid() / ShellTitle() regexes.
        try
        {
            var shellRaw = Run("gdbus", "call --session --dest org.gnome.Shell --object-path /org/gnome/Shell/Introspect --method org.gnome.Shell.Introspect.GetWindows", 5000);
            if (shellRaw != null && !shellRaw.Contains("Access denied"))
            {
                // Parse all pid-title pairs from the GVariant array.
                // Output format example:
                // ({'title': <'Page Title - Mozilla Firefox'>, 'wm-pid': <int32 12345>, ...}, ...)
                // ShellPid regex: pid\s*:?\s*(\d+)
                // ShellTitle regex: title\s*:?\s*'([^']*)'
                var pidMatches = ShellPidRegexAll().Matches(shellRaw);
                var titleMatches = ShellTitleRegexAll().Matches(shellRaw);

                for (int i = 0; i < Math.Min(pidMatches.Count, titleMatches.Count); i++)
                {
                    if (int.TryParse(pidMatches[i].Groups[1].Value, out var pid) && pid > 0 && seenPids.Add(pid))
                    {
                        var title = titleMatches[i].Groups[1].Value;
                        map[pid] = string.IsNullOrWhiteSpace(title) ? null : title;
                    }
                }

                // If GNOME Shell introspection returned results, merge with X11 and return
                if (map.Count > 0)
                {
                    // Also get X11/XWayland windows for non-Wayland apps
                    MergeX11WindowTitles(map, seenPids);
                    return map;
                }
            }
        }
        catch { }

        // 🟡 Step 2: Fallback to X11 xprop for XWayland windows (limited on Wayland)
        MergeX11WindowTitles(map, seenPids);

        return map;
    }

    /// <summary>
    /// Enumerate X11/XWayland windows via xprop _NET_CLIENT_LIST.
    /// On Wayland, this only returns XWayland windows (not native Wayland like Firefox, Chrome).
    /// </summary>
    private static void MergeX11WindowTitles(Dictionary<int, string?> map, HashSet<int> seenPids)
    {
        try
        {
            var raw = Run("xprop", "-root -notype _NET_CLIENT_LIST", 2000);
            if (raw == null) return;

            var idMatches = WindowIdRegex().Matches(raw);
            foreach (Match idMatch in idMatches)
            {
                var wid = idMatch.Value;
                if (wid == "0x0") continue;

                var rawPid = Run("xprop", $"-id {wid} _NET_WM_PID", 2000);
                if (rawPid == null) continue;
                var pidMatch = PidRegex().Match(rawPid);
                if (!pidMatch.Success) continue;
                if (!int.TryParse(pidMatch.Groups[1].Value, out var pid)) continue;
                if (pid <= 0 || !seenPids.Add(pid)) continue;

                string? title = null;
                var rawTitle = Run("xprop", $"-id {wid} _NET_WM_NAME", 2000);
                if (rawTitle != null)
                {
                    var tMatch = TitleRegex().Match(rawTitle);
                    if (tMatch.Success) title = tMatch.Groups[1].Value;
                }

                map[pid] = title;
            }
        }
        catch { }
    }

    public static IReadOnlyDictionary<string, bool> GetPermissionStatus()
    {
        var status = new Dictionary<string, bool>();
        status["xprop"] = HasTool("xprop");
        status["xdotool"] = HasTool("xdotool");
        status["gdbus"] = HasTool("gdbus");
        status["python3"] = HasTool("python3");
        status["xdg_portal"] = GetActiveViaPortal() != null;
        status["shell_introspect"] = GetActiveViaShellIntrospect() != null;
        status["atspi"] = GetActiveViaAtSpi() != null;
        status["xprop_x11"] = CheckX11();
        return status;
    }

    private static bool HasTool(string name)
    {
        if (PermissionCache.Contains(name)) return true;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = name,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var ok = proc.WaitForExit(1000) && proc.ExitCode == 0;
            if (ok) PermissionCache.Add(name);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckX11()
    {
        try
        {
            var outRaw = Run("xprop", "-root -notype _NET_SUPPORTING_WM_CHECK", 1000);
            return outRaw != null;
        }
        catch
        {
            return false;
        }
    }

    private static (int pid, string? title)? GetActiveWindowInfo()
    {
        if (GetActiveViaXprop() is {} x11) return x11;
        if (GetActiveViaAtSpi() is {} atspi) return atspi;
        if (GetActiveViaPortal() is {} portal) return portal;
        if (GetActiveViaShellIntrospect() is {} shell) return shell;
        if (GetActiveViaXdotool() is {} xt) return xt;
        return GetActiveViaHeuristic();
    }

    private static (int pid, string? title)? GetActiveViaXprop()
    {
        try
        {
            var rawId = Run("xprop", "-root -notype _NET_ACTIVE_WINDOW", 2000);
            if (rawId == null) return null;

            var idMatch = WindowIdRegex().Match(rawId);
            if (!idMatch.Success) return null;

            var wid = idMatch.Value;
            if (wid == "0x0") return null;

            var rawPid = Run("xprop", $"-id {wid} _NET_WM_PID", 2000);
            if (rawPid == null) return null;
            var pidMatch = PidRegex().Match(rawPid);
            if (!pidMatch.Success) return null;
            var pid = int.Parse(pidMatch.Groups[1].Value);

            string? title = null;
            var rawTitle = Run("xprop", $"-id {wid} _NET_WM_NAME", 2000);
            if (rawTitle != null)
            {
                var tMatch = TitleRegex().Match(rawTitle);
                if (tMatch.Success)
                    title = tMatch.Groups[1].Value;
            }

            return (pid, title);
        }
        catch
        {
            return null;
        }
    }

    private static (int pid, string? title)? GetActiveViaAtSpi()
    {
        if (HasTool("python3"))
            return GetActiveViaAtSpiPython();
        if (HasTool("gdbus"))
            return GetActiveViaAtSpiGdbus();
        return null;
    }

    private static (int pid, string? title)? GetActiveViaAtSpiPython()
    {
        try
        {
            var script = """"
import dbus, dbus.bus, os, sys

def is_active_window(states):
    # AT-SPI state decoding. Older at-spi2-core returns a LIST of state ids
    # (e.g. [1, 8, 24, 25]); at-spi2-core >= 2.50 returns a PACKED 64-bit
    # bitmask (two uint32s, low word first) where bit N = state N. The focused
    # window carries STATE_ACTIVE (1) and/or STATE_FOCUSED (12) in BOTH forms.
    # (Checking for state 8 = ENABLED — the old code — matches every window and
    # matched nothing once the format became a bitmask.)
    try:
        vals = list(states)
    except Exception:
        return False
    if not vals:
        return False
    if max(vals) > 0xFFFF:
        mask = vals[0] | (vals[1] << 32 if len(vals) > 1 else 0)
        return bool(mask & ((1 << 1) | (1 << 12)))
    return 1 in vals or 12 in vals

try:
    raw = os.popen('gdbus call --session --dest org.a11y.Bus --object-path /org/a11y/bus --method org.a11y.Bus.GetAddress 2>/dev/null').read().strip()
    start = raw.find(chr(39)) + 1
    end = raw.rfind(chr(39))
    addr = raw[start:end] if start > 0 and end > start else ''
    if not addr:
        sys.exit(1)
    bus = dbus.bus.BusConnection(addr)
    registry = bus.get_object('org.a11y.atspi.Registry', '/org/a11y/atspi/accessible/root')
    children = registry.GetChildren(dbus_interface='org.a11y.atspi.Accessible')
    for app_bus_name, _ in children:
        name = str(app_bus_name)
        if name == ':1.0':
            continue
        try:
            app_obj = bus.get_object(name, '/org/a11y/atspi/accessible/root')
            win_children = app_obj.GetChildren(dbus_interface='org.a11y.atspi.Accessible')
        except:
            continue                for win_bus, win_path in win_children:
                    try:
                        win_obj = bus.get_object(str(win_bus), str(win_path))
                        state = win_obj.GetState(dbus_interface='org.a11y.atspi.Accessible')
                        if not is_active_window(state):
                            continue
                title = None
                try:
                    title = str(win_obj.Get('org.a11y.atspi.Accessible', 'Name', dbus_interface='org.freedesktop.DBus.Properties'))
                except:
                    pass
                pid = 0
                try:
                    app_ref = win_obj.GetApplication(dbus_interface='org.a11y.atspi.Accessible')
                    if app_ref:
                        parent_bus = str(app_ref[0])
                        dbus_obj = bus.get_object('org.freedesktop.DBus', '/org/freedesktop/DBus')
                        pid = dbus_obj.GetConnectionUnixProcessID(parent_bus, dbus_interface='org.freedesktop.DBus')
                except:
                    pass
                print(f'{pid}|{title or ""}')
                sys.exit(0)
            except:
                continue
except:
    pass
"""";

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = "-",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.StandardInput.Write(script);
            proc.StandardInput.Close();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(8000);

            if (string.IsNullOrEmpty(output)) return null;

            var parts = output.Split('|', 2);
            if (parts.Length < 2) return null;

            if (!int.TryParse(parts[0].Trim(), out var pid) || pid <= 0)
                return null;

            var title = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1].Trim();
            return (pid, title);
        }
        catch
        {
            return null;
        }
    }

    private static (int pid, string? title)? GetActiveViaAtSpiGdbus()
    {
        try
        {
            var addrRaw = Run("gdbus", "call --session --dest org.a11y.Bus --object-path /org/a11y/bus --method org.a11y.Bus.GetAddress", 3000);
            if (addrRaw == null) return null;

            var addrMatch = GvariantString().Match(addrRaw);
            if (!addrMatch.Success) return null;
            var busAddr = addrMatch.Groups[1].Value;

            var childrenRaw = Run("gdbus", $"call --address \"{busAddr}\" --dest org.a11y.atspi.Registry --object-path /org/a11y/atspi/accessible/root --method org.a11y.atspi.Accessible.GetChildren", 5000);
            if (childrenRaw == null) return null;

            var appMatches = DbusName().Matches(childrenRaw);
            foreach (Match appMatch in appMatches)
            {
                var appName = appMatch.Groups[1].Value;
                if (appName == ":1.0") continue;

                var winsRaw = Run("gdbus", $"call --address \"{busAddr}\" --dest {appName} --object-path /org/a11y/atspi/accessible/root --method org.a11y.atspi.Accessible.GetChildren", 3000);
                if (winsRaw == null) continue;

                var winMatches = DbusName().Matches(winsRaw);
                foreach (Match winMatch in winMatches)
                {
                    var winName = winMatch.Groups[1].Value;

                    var stateRaw = Run("gdbus", $"call --address \"{busAddr}\" --dest {winName} --object-path /org/a11y/atspi/accessible/root --method org.a11y.atspi.Accessible.GetState", 2000);
                    if (stateRaw == null) continue;

                    if (IsAtSpiActiveState(stateRaw))
                    {
                        var titleRaw = Run("gdbus", $"call --address \"{busAddr}\" --dest {winName} --object-path /org/a11y/atspi/accessible/root --method org.freedesktop.DBus.Properties.Get \"org.a11y.atspi.Accessible\" \"Name\"", 2000);
                        var title = titleRaw != null ? ExtractGvariantString(titleRaw) : null;

                        var pidRaw = Run("gdbus", $"call --address \"{busAddr}\" --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus --method org.freedesktop.DBus.GetConnectionUnixProcessID \"{appName}\"", 2000);
                        _ = int.TryParse(ExtractGvariantString(pidRaw), out var pid);
                        if (pid > 0) return (pid, title);
                    }
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Decode an AT-SPI GetState reply (GVariant text) into the focused-window test.
    /// at-spi2-core &gt;= 2.50 returns a PACKED 64-bit bitmask of state flags
    /// ("[uint32 1124073730, 0]" — low word first); older versions return a list of
    /// state ids ("[1, 8, 24, 25]"). In BOTH forms the focused window carries
    /// STATE_ACTIVE (1) and/or STATE_FOCUSED (12). (Checking for state 8 = ENABLED
    /// — the old Contains("8") — matched every window and matched nothing once the
    /// format became a bitmask.)
    /// </summary>
    private static bool IsAtSpiActiveState(string stateRaw)
    {
        var nums = new List<long>();
        foreach (Match m in GvariantUInt32Regex().Matches(stateRaw))
        {
            if (long.TryParse(m.Groups[1].Value, out var v))
                nums.Add(v);
        }
        if (nums.Count == 0) return false;
        if (nums.Max() > 0xFFFF)
        {
            var mask = nums[0] | (nums.Count > 1 ? nums[1] << 32 : 0);
            return (mask & ((1L << 1) | (1L << 12))) != 0;
        }
        return nums.Contains(1) || nums.Contains(12);
    }

    private static (int pid, string? title)? GetActiveViaPortal()
    {
        if (!HasTool("gdbus")) return null;
        try
        {
            var raw = Run("gdbus", "call --session --dest org.freedesktop.portal.Desktop --object-path /org/freedesktop/portal/desktop --method org.freedesktop.portal.Window.GetActiveWindow", 3000);
            if (raw == null || !raw.StartsWith("(")) return null;

            var match = GvariantString().Match(raw);
            if (!match.Success) return null;
            var windowId = match.Groups[1].Value;
            if (string.IsNullOrEmpty(windowId)) return null;

            var appIdRaw = Run("gdbus", $"call --session --dest org.freedesktop.portal.Desktop --object-path /org/freedesktop/portal/desktop --method org.freedesktop.portal.Window.GetWindowAppId \"{windowId}\"", 3000);
            if (appIdRaw == null) return null;

            var appMatch = GvariantString().Match(appIdRaw);
            if (!appMatch.Success) return null;
            var appId = appMatch.Groups[1].Value;
            if (string.IsNullOrEmpty(appId)) return null;

            var pid = ResolveAppIdToPid(appId);
            if (pid <= 0) return null;

            return (pid, appId);
        }
        catch { return null; }
    }

    private static (int pid, string? title)? GetActiveViaShellIntrospect()
    {
        if (!HasTool("gdbus")) return null;
        try
        {
            var outRaw = Run("gdbus", "call --session --dest org.gnome.Shell --object-path /org/gnome/Shell/Introspect --method org.gnome.Shell.Introspect.GetWindows", 3000);
            if (outRaw == null || outRaw.Contains("Access denied")) return null;

            var pidMatch = ShellPid().Match(outRaw);
            if (!pidMatch.Success) return null;
            var pid = int.Parse(pidMatch.Groups[1].Value);

            var titleMatch = ShellTitle().Match(outRaw);
            var title = titleMatch.Success ? titleMatch.Groups[1].Value : null;
            return (pid, title);
        }
        catch { return null; }
    }

    private static (int pid, string? title)? GetActiveViaXdotool()
    {
        try
        {
            var pidOut = Run("xdotool", "getactivewindow getwindowpid", 2000);
            if (pidOut == null || !int.TryParse(pidOut, out var pid)) return null;
            var title = Run("xdotool", "getactivewindow getwindowname", 2000);
            return (pid, string.IsNullOrWhiteSpace(title) ? null : title);
        }
        catch { return null; }
    }

    private static (int pid, string? title)? GetActiveViaHeuristic()
    {
        try
        {
            var processes = Process.GetProcesses();
            (int pid, string? title, DateTime start) best = (0, null, DateTime.MinValue);
            foreach (var proc in processes)
            {
                try
                {
                    if (!ProcessFilter.IsUserProcess(proc, false, false)) continue;
                    var startTime = proc.StartTime;
                    if (startTime > best.start)
                    {
                        var name = proc.ProcessName;
                        string? t = null;
                        try { t = proc.MainWindowTitle; } catch { }
                        best = (proc.Id, t ?? name, startTime);
                    }
                }
                catch { }
            }
            return best.pid > 0 ? (best.pid, best.title) : null;
        }
        catch { return null; }
    }

    private static int ResolveAppIdToPid(string appId)
    {
        try
        {
            var procs = Process.GetProcessesByName(appId);
            if (procs.Length > 0) return procs[0].Id;
            if (appId.Contains('.'))
            {
                var shortName = appId.Split('.').Last().ToLowerInvariant();
                procs = Process.GetProcessesByName(shortName);
                if (procs.Length > 0) return procs[0].Id;
            }
        }
        catch { }
        return 0;
    }

    private static string? ExtractGvariantString(string? output)
    {
        if (output == null) return null;
        var match = GvariantString().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? Run(string file, string args, int timeoutMs = 3000)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(timeoutMs);
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read the full command line of a process from /proc/{pid}/cmdline.
    /// Returns null if the process has exited or the file is unreadable.
    /// </summary>
    private static string? ReadProcessCmdline(int pid)
    {
        try
        {
            var cmdlinePath = $"/proc/{pid}/cmdline";
            if (!File.Exists(cmdlinePath)) return null;
            var content = File.ReadAllText(cmdlinePath);
            // /proc/pid/cmdline uses null bytes as separators; replace with spaces
            return content.Replace('\0', ' ');
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"0x[0-9a-f]+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowIdRegex();
    [GeneratedRegex(@"uint32\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GvariantUInt32Regex();
    [GeneratedRegex(@"=\s+(\d+)")]
    private static partial Regex PidRegex();
    [GeneratedRegex(@"""(.+?)""")]
    private static partial Regex TitleRegex();
    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex GvariantString();
    [GeneratedRegex(@"(:?\d[\w.]*)")]
    private static partial Regex DbusName();
    [GeneratedRegex(@"pid\s*:?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ShellPid();
    [GeneratedRegex(@"title\s*:?\s*'([^']*)'", RegexOptions.IgnoreCase)]
    private static partial Regex ShellTitle();
    // GVariant patterns for GNOME Shell introspection array output
    // Matches wm-pid in: 'wm-pid': <int32 12345>
    [GeneratedRegex(@"wm-pid'?:\s*<int32\s+(\d+)>", RegexOptions.IgnoreCase)]
    private static partial Regex ShellPidRegexAll();
    // Matches title in: 'title': <'Page Title'>
    [GeneratedRegex(@"'title':\s*<\s*'([^']*)'", RegexOptions.IgnoreCase)]
    private static partial Regex ShellTitleRegexAll();
}
