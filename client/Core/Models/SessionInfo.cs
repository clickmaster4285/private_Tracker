namespace client.Core.Models;

public static class SessionInfo
{
    public static string SessionId { get; } = Guid.NewGuid().ToString("N");
    public static DateTime StartedAt { get; } = DateTime.UtcNow;
}
