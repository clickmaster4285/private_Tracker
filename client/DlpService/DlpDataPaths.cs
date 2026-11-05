namespace AlphaAITracker.Service;

/// <summary>
/// Machine-wide DLP state roots. Windows: ProgramData\&lt;mutex&gt;\dlp.
/// Linux: /var/lib/&lt;package&gt;/dlp (sticky 1777 from .deb — user bridge + root daemon).
/// </summary>
internal static class DlpDataPaths
{
    public static string PackageName => ServiceIdentity.PackageName;

    public static string DlpRoot
    {
        get
        {
            if (OperatingSystem.IsLinux())
                return Path.Combine("/var/lib", PackageName, "dlp");

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, ServiceIdentity.DataFolderName, "dlp");
        }
    }

    public static string ControlRoot
    {
        get
        {
            if (OperatingSystem.IsLinux())
                return Path.Combine("/var/lib", PackageName, "control");

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, ServiceIdentity.DataFolderName, "control");
        }
    }

    public static string UpdatesRoot
    {
        get
        {
            if (OperatingSystem.IsLinux())
                return Path.Combine("/var/lib", PackageName, "updates");

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, ServiceIdentity.DataFolderName, "updates");
        }
    }

    /// <summary>Unix domain socket for tracker ↔ DLP daemon IPC.</summary>
    public static string UnixSocketPath =>
        Path.Combine(DlpRoot, "dlp.sock");
}
