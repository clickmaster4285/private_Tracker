namespace client.Core.DesktopEventBus;

public class RawDesktopEvent
{
    public string Source { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? AppName { get; set; }
    public string? WindowTitle { get; set; }
    public int? WindowId { get; set; }
    public int? TabId { get; set; }
    public string? CurrentPath { get; set; }
    public string? PreviousPath { get; set; }
    public string? RawData { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string MetadataJson { get; set; } = "{}";
}
