namespace client.Core.DesktopEventBus;

internal class JourneyRecord
{
    public string JourneyId { get; set; } = string.Empty;
    public string AppSessionId { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public string CurrentPath { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastEventAt { get; set; } = DateTime.UtcNow;
    public int Sequence { get; set; }
    public int? WindowId { get; set; }
    public int? TabId { get; set; }
}
