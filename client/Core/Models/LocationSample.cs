namespace client.Core.Models;

/// <summary>
/// A single device location fix — latitude/longitude from the OS geolocation API or
/// an IP-geolocation fallback. Synced to the server via POST /api/v1/location-samples/sync.
/// </summary>
public class LocationSample
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>Horizontal accuracy in metres from the OS (null for IP fallback).</summary>
    public double? AccuracyM { get; set; }
    public double? AltitudeM { get; set; }
    /// <summary>gps | wifi | ip | manual</summary>
    public string Source { get; set; } = "ip";
    public string? Address { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}
