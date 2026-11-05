namespace client.Core.Models;

public class ActivityLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MachineId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string? WindowTitle { get; set; }
    public int ProcessId { get; set; }
    public double CpuPercent { get; set; }
    public long MemoryBytes { get; set; }
    public bool IsForeground { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
}
