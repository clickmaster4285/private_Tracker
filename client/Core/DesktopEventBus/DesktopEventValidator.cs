namespace client.Core.DesktopEventBus;

public static class DesktopEventValidator
{
    private static readonly HashSet<string> FileManagers = new(StringComparer.OrdinalIgnoreCase)
    {
        "nautilus", "org.gnome.Nautilus", "nautilus-autorun-software",
        "dolphin", "org.kde.dolphin",
        "thunar", "thunar-volman",
        "nemo", "caja", "pcmanfm", "pcmanfm-qt",
        "konqueror", "krusader", "doublecmd", "doublecmd-gtk",
        "ranger", "nnn", "lf", "vifm",
        "io.elementary.files", "pantheon-files",
    };

    private static readonly HashSet<string> IgnoredProcessPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "gvfs-", "goa-", "xdg-", "at-spi-", "gnome-",
        "dbus-", "systemd-", "tracker-",
    };

    public static bool IsFileManager(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return false;
        var lower = appName.Trim().ToLowerInvariant();
        if (FileManagers.Contains(lower)) return true;
        return lower.Contains("file") || lower.Contains("manager") || lower.Contains("explorer");
    }

    public static bool IsIgnoredProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        foreach (var prefix in IgnoredProcessPrefixes)
            if (processName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static bool IsValidPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        path = path.Trim();
        if (path.StartsWith('/') || path.StartsWith('~') || path.StartsWith("file://"))
            return true;
        if (path.Length >= 2 && path[1] == ':' && OperatingSystem.IsWindows())
            return path[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        return false;
    }
}
