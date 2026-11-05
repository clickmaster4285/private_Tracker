namespace client.Core.Models;

/// <summary>
/// A physical device attached to the machine — USB drives, phones, mice, keyboards,
/// headsets, monitors, chargers — with its plug-in / plug-out timestamps.
/// device_class: 'storage' | 'input' | 'audio' | 'display' | 'usb' | 'power' | 'other'
/// </summary>
public class HardwareDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceClass { get; set; } = "other";
    public string Vendor { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    /// <summary>udev DEVPATH (e.g. /devices/pci0000:00/.../usb1/1-8) — stable per physical plug.</summary>
    public string BusPath { get; set; } = string.Empty;
    /// <summary>Kernel device node when applicable (e.g. /dev/sdb1, event5, card0-connector).</summary>
    public string DeviceNode { get; set; } = string.Empty;
    public DateTime PluggedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Null while the device is still plugged in.</summary>
    public DateTime? UnpluggedAt { get; set; }
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}
