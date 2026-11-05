namespace client.Core.DesktopEventBus;

public class DesktopEvent
{
    public string ObjectType { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? AppName { get; set; }
    public string? AppDisplayName { get; set; }
    public string? WindowTitle { get; set; }
    public int? WindowId { get; set; }
    public int? TabId { get; set; }
    public string? PreviousPath { get; set; }
    public string? CurrentPath { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string MetadataJson { get; set; } = "{}";
    public string JourneyId { get; set; } = string.Empty;
    public int Sequence { get; set; }
}
