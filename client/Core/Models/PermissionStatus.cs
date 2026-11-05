namespace client.Core.Models;

/// <summary>
/// One permission check result (permission_status table). Since 2026-08-11 the check_id is
/// the STABLE key "{platform}_{method}" and writes are upserts (ON CONFLICT DO UPDATE), so
/// the table holds ONE row per permission method — the previous design minted a new GUID
/// every ~5 min and inserted fresh rows each time (thousands of duplicates/day).
/// </summary>
public class PermissionStatus
{
    public string CheckId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string SessionType { get; set; } = "";
    public string Platform { get; set; } = "";
    public string CheckedAt { get; set; } = "";
    public string Method { get; set; } = "";
    public bool Works { get; set; }
    public string? Details { get; set; }
    public string? EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
}
