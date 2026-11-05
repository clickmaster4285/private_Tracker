using client.Core;

namespace client.Services;

/// <summary>
/// Platform path for Fixed/Roaming <c>control.json</c>.
/// Windows: ProgramData (LocalSystem DlpService owns writes).
/// Linux/macOS: LocalApplicationData (cooperative user-process poller owns writes).
/// </summary>
public static class TrackingControlPaths
{
    public static string ControlDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    AppInfo.DataFolderName, "control");
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInfo.DataFolderName, "control");
        }
    }

    public static string ControlJsonPath => Path.Combine(ControlDirectory, "control.json");
}
