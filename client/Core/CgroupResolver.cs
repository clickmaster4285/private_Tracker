using System.Text.RegularExpressions;

namespace client.Core;

/// <summary>
/// Resolves the systemd transient scope for a Linux process via /proc/&lt;pid&gt;/cgroup.
///
/// On any systemd-based desktop (GNOME/KDE with systemd --user), every app launched
/// via a .desktop file is placed into its own transient scope unit:
///
///     app-gnome-code-&lt;random&gt;.scope
///     app-flatpak-org.antigravity-&lt;random&gt;.scope
///
/// Every subprocess spawned by that launch (main, renderer, extension host, GPU
/// process, language server) lives inside that same cgroup for the life of the app,
/// regardless of PPID reparenting. Two separate launches of the same app — even within
/// the same second — get different scope IDs. This is the correct grouping key for
/// collapsing multi-process GUI apps (VS Code's ~11 PIDs, Chrome's renderers, etc.)
/// into a single app_session per logical window/instance.
///
/// Linux-only: returns null on other platforms.
/// </summary>
public static partial class CgroupResolver
{
    /// <summary>
    /// Returns the app-*.scope (or app-*.slice) segment for the given PID,
    /// or null if the process is not under a systemd app scope (or not on Linux).
    /// </summary>
    public static string? GetAppScope(int pid)
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            var path = $"/proc/{pid}/cgroup";
            if (!File.Exists(path)) return null;

            foreach (var line in File.ReadLines(path))
            {
                // format: hierarchy-id:controller-list:cgroup-path
                var parts = line.Split(':', 3);
                if (parts.Length < 3) continue;

                var cgroupPath = parts[2];
                var match = AppScopeRegex().Match(cgroupPath);
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch (IOException) { /* process exited mid-read; treat as null */ }
        catch (UnauthorizedAccessException) { /* permission edge case; treat as null */ }

        return null;
    }

    [GeneratedRegex(@"(app-[^/]+\.(?:scope|slice))")]
    private static partial Regex AppScopeRegex();
}
