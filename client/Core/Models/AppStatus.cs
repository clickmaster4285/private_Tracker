namespace client.Core.Models;

/// <summary>
/// One key/value status row (app_status table): heartbeat timestamps, login state,
/// permission-check bookmarks, etc. Upserted by key — a value change resets is_synced
/// so the server learns it on the next sync roundtrip. Never deleted client-side.
/// </summary>
public class AppStatus
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
}
