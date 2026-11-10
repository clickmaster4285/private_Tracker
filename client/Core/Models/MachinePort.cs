namespace client.Core.Models;

/// <summary>
/// One scanned port / interface / device on this machine. Covers all three
/// categories: 1=external physical port, 2=internal interface, 3=wireless/virtual.
/// Keyed locally by <see cref="InstanceId"/> (the stable PnP DeviceInstanceId).
/// machine_id is stamped server-side from DeviceAuth, not sent in the body.
/// </summary>
public class MachinePort
{
    public string InstanceId { get; set; } = string.Empty;
    public int Category { get; set; } = 1;
    public string PnpClass { get; set; } = string.Empty;
    public string ClassGuid { get; set; } = string.Empty;
    public string BusType { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    /// <summary>True = attached/occupied now; false = removed (was seen before).</summary>
    public bool Present { get; set; } = true;
    public string Status { get; set; } = string.Empty;
    /// <summary>False for video-out connectors and the system disk (alert-only).</summary>
    public bool Blockable { get; set; } = true;
    public bool IsSynced { get; set; }
}
