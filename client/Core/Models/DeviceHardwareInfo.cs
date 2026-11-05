using System.Text.Json;

namespace client.Core.Models;

public class DeviceHardwareInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MacAddress { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string OsName { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string CpuModel { get; set; } = string.Empty;
    public int CpuCores { get; set; }
    public long RamTotalMb { get; set; }

    public string GpuModel { get; set; } = string.Empty;
    public long GpuVramMb { get; set; }
    public string StorageDevices { get; set; } = "[]";
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

public class InstalledApplication
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AppName { get; set; } = string.Empty;
    /// <summary>Executable binary name (e.g., \"code\" for Visual Studio Code)</summary>
    public string BinaryName { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string InstallPath { get; set; } = string.Empty;
    public DateTime? InstallDate { get; set; }
    public string UninstallString { get; set; } = string.Empty;
    /// <summary>"installed" | "uninstalled" | "seen"</summary>
    public string ChangeType { get; set; } = "seen";
    /// <summary>
    /// Lifecycle state — ONE ROW PER INSTALL CYCLE. A row is opened when the app is first
    /// seen (install_date = NULL when the OS reports none, else the reported date; newly
    /// detected installs get the detection time) and closed with uninstall_date when the
    /// app disappears from a scan. A reinstall opens a NEW row. Closed rows are history.
    /// </summary>
    public bool IsInstalled { get; set; } = true;
    /// <summary>When this install cycle ended (NULL while currently installed).</summary>
    public DateTime? UninstallDate { get; set; }
    /// <summary>True if this app is a web browser (detected from .desktop Categories/MimeType)</summary>
    public bool IsBrowser { get; set; }
    /// <summary>
    /// Stable desktop entry identity on Linux: the .desktop filename without extension
    /// (e.g. "firefox_firefox", "code"). Used by SoftwareIdentityResolver to dedup across
    /// package managers and detect the same app discovered via multiple sources.
    /// Empty on Windows/macOS.
    /// </summary>
    public string DesktopId { get; set; } = string.Empty;
    /// <summary>
    /// Desktop entry Categories on Linux (e.g. "WebBrowser;Network;GTK;") or macOS bundle
    /// identifier (e.g. "org.mozilla.firefox"). Used for metadata-driven classification
    /// instead of hardcoded name lists. Empty on Windows.
    /// </summary>
    public string Categories { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

public class NetworkInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PublicIp { get; set; } = string.Empty;
    public string PrivateIp { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string NetworkInterfaceName { get; set; } = string.Empty;
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    /// <summary>When this IP combination was first observed (row created).</summary>
    public DateTime? FirstSeenAt { get; set; }
    /// <summary>Last time the same IP combination was still seen as the active one.</summary>
    public DateTime? LastSeenAt { get; set; }
    /// <summary>True while this IP combination is the device's active one (flipped to 0 on IP change).</summary>
    public bool IsCurrent { get; set; } = true;
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

public class StorageDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>FK → device_hardware_info.id</summary>
    public string DeviceHardwareId { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public long CapacityMb { get; set; }
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

public class InstalledPackage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PackageName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    /// <summary>"runtime" | "tool" | "library" | "system"</summary>
    public string Category { get; set; } = "tool";
    /// <summary>"npm" | "pip" | "apt" | "brew" | "choco" | "winget" | "scoop" | "cargo" | "snap" | "flatpak"</summary>
    public string SourceManager { get; set; } = string.Empty;
    public string InstallPath { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// When this package cycle was installed. Linux package managers (dpkg) expose no true
    /// install date — NULL means unknown (pre-existing software); a package first detected
    /// while the tracker runs gets the detection time stamped here.
    /// </summary>
    public DateTime? InstallDate { get; set; }
    /// <summary>Lifecycle state — ONE ROW PER INSTALL CYCLE (see InstalledApplication).</summary>
    public bool IsInstalled { get; set; } = true;
    /// <summary>When this install cycle ended (NULL while currently installed).</summary>
    public DateTime? UninstallDate { get; set; }
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

