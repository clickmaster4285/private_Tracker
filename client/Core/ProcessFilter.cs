using System.Diagnostics;
using System.Text.RegularExpressions;

namespace client.Core;

public static partial class ProcessFilter
{
    /// <summary>
    /// Run a CLI probe with a HARD timeout and concurrent stream draining — the pattern that
    /// cannot deadlock. The old inline pattern (StandardOutput.ReadToEnd() then WaitForExit(ms))
    /// blocked forever when a grandchild inherited the stdout pipe after the direct child exited:
    /// ReadToEnd waits for an EOF that never comes, and the WaitForExit timeout is unreachable.
    /// That hang froze the inventory watcher's rescan; because the rescan never returned, its
    /// in-flight flag stayed set and EVERY later install/uninstall event was coalesced away —
    /// the DB looked stale until the app was restarted. This helper:
    ///   • drains stdout + stderr CONCURRENTLY (a chatty stderr can never fill its pipe buffer),
    ///   • hard-time-boxes the whole probe (exit wait AND stream drain),
    ///   • kills the process tree on timeout so no orphaned probe lingers.
    /// Returns the stdout text, or null on failure/timeout/empty process.
    /// </summary>
    public static string? RunProbe(ProcessStartInfo psi, int timeoutMs = 10000)
    {
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            // Only touch StandardError when the psi actually redirects it (snap/flatpak/brew
            // probes do not — reading a non-redirected stream would throw).
            var stderrTask = psi.RedirectStandardError
                ? proc.StandardError.ReadToEndAsync()
                : Task.FromResult(string.Empty);
            var exitTask = proc.WaitForExitAsync();

            // Hard time-box the whole probe.
            if (Task.WhenAny(exitTask, Task.Delay(timeoutMs)).GetAwaiter().GetResult() != exitTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                try { Task.WaitAll(new[] { stdoutTask, stderrTask }, 2000); } catch { }
                return null;
            }

            // Direct child exited — but a grandchild may still hold the stdout pipe open, so
            // bound the stream drain too instead of blocking on ReadToEnd forever.
            if (!Task.WaitAll(new[] { stdoutTask, stderrTask }, timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return stdoutTask.Result;
        }
        catch (Exception)
        {
            // Any failure (stream read, kill, spawn) is a failed probe — the caller
            // simply skips that source. Never let a probe take the scan down.
            return null;
        }
    }

    private static readonly HashSet<string> KernelNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "kthreadd", "kworker", "ksoftirqd", "migration", "idle_inject",
            "cpuhp", "rcu", "pool_workqueue_release", "kprobe", "kdevtmpfs",
            "netns", "xenbus", "watchdogd", "systemd", "systemd-udevd",
            "systemd-journald", "systemd-logind", "systemd-resolved",
            "systemd-timesyncd", "systemd-networkd", "systemd-oomd",
            "systemd-userdbd", "systemd-homed", "systemd-hostnamed",
            "systemd-localed", "systemd-timedated", "systemd-machined",
            "systemd-importd", "systemd-bootcheck", "journald", "auditd",
            "accounts-daemon", "acpid", "anacron", "atd", "cron", "dbus",
            "dbus-daemon", "irqbalance", "networkd-dispat", "polkitd",
            "rsyslogd", "sshd", "syslogd", "syslog-ng", "thermald",
            "udevd", "upowerd", "wpa_supplicant", "kerneloops", "psimon",
            "kworker/R-", "gjs", "pipewire", "wireplumber", "anydesk",
            "postgres", "apache2", "xdg-permission-store", "xdg-document-portal",
            "xdg-desktop-portal-gtk", "xdg-desktop-portal-gnome",
            "xdg-desktop-portal", "gnome-session-binary", "gnome-keyring-d",
            "at-spi-bus-launcher", "gvfsd", "gvfs-", "goa-daemon",
            "goa-identity-se", "gsd-", "evolution-", "ibus-", "ibus-daemon",
            "pulseaudio", "rtkit-daemon", "colord", "cupsd", "cups-browsed",
            "fwupd", "geoclue", "ModemManager", "NetworkManager",
            "packagekitd", "power-profiles-daemon", "snapd", "switcheroo",
            "systemd-logind", "udisksd", "upowerd",
            "Xwayland", "at-spi2-registryd", "gnome-shell-calendar-server",
            "tracker-extract", "tracker-store", "evolution-source-registry",
        };

    private static readonly string[] KernelNamePrefixes =
    {
        "gvfsd-", "gvfs-", "gsd-", "goa-", "evolution-",
        "ibus-", "at-spi2-", "gnome-shell-", "tracker-",
        "gdm", "mutter-",
    };

    private static readonly int CurrentSessionId = GetCurrentSessionId();

    private static readonly Regex KernelPattern = KernelNamePattern();

    [GeneratedRegex(@"^(kworker|kthread|migration|idle_inject|cpuhp|ksoftirqd|rcu_|pool_workqueue)", RegexOptions.IgnoreCase)]
    private static partial Regex KernelNamePattern();

    private static int GetCurrentSessionId()
    {
        try { return Process.GetCurrentProcess().SessionId; }
        catch { return -1; }
    }

    public static bool IsUserProcess(Process proc, bool checkWindowHandle = true, bool checkSession = true)
    {
        try
        {
            var pid = proc.Id;
            var name = proc.ProcessName;

            if (pid == 0) return false;
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (pid < 100) return false;

            if (KernelNames.Contains(name)) return false;
            if (KernelNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (KernelPattern.IsMatch(name)) return false;

            if (checkSession)
            {
                if (proc.SessionId == 0) return false;
                if (CurrentSessionId > 0 && proc.SessionId != CurrentSessionId) return false;
            }

            var mem = 0L;
            try { mem = proc.WorkingSet64; } catch { }

            if (checkWindowHandle)
            {
                var hasWindow = false;
                try { hasWindow = proc.MainWindowHandle != IntPtr.Zero; } catch { }
                if (hasWindow) return true;
            }

            if (mem <= 0) return false;

            try
            {
                var startTime = proc.StartTime;
                if ((DateTime.UtcNow - startTime.ToUniversalTime()).TotalSeconds < 5)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
