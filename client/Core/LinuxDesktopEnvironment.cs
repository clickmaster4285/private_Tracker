using System.Diagnostics;

namespace client.Core;

/// <summary>
/// Linux: refresh graphical session env (DISPLAY / WAYLAND_DISPLAY / XAUTHORITY …)
/// from the user systemd manager before spawning the separate <c>--gui</c> process.
/// GNOME Wayland rotates XAUTHORITY under /run/user, so a long-lived headless
/// tracker would otherwise hand Avalonia a stale cookie.
/// </summary>
public static class LinuxDesktopEnvironment
{
    public static void Refresh()
    {
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = "--user show-environment",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return;
            }

            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "DISPLAY",
                "WAYLAND_DISPLAY",
                "XAUTHORITY",
                "XDG_RUNTIME_DIR",
                "DBUS_SESSION_BUS_ADDRESS",
            };
            foreach (var line in outputTask.GetAwaiter().GetResult().Split('\n'))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var name = line[..separator];
                if (!allowed.Contains(name)) continue;
                Environment.SetEnvironmentVariable(name, line[(separator + 1)..].TrimEnd('\r'));
            }
        }
        catch
        {
            /* existing env may already be valid */
        }
    }
}
