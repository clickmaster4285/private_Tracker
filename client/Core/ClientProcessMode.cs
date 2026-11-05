namespace client.Core;

/// <summary>
/// Which role this <c>client.exe</c> process is playing. Tracker and GUI are
/// separate processes so Avalonia never initializes inside the headless host
/// (that one-shot init permanently broke lazy SHOW after celebration).
/// </summary>
public static class ClientProcessMode
{
    /// <summary>
    /// True in <c>--gui</c>: show the Avalonia shell; park collectors/sync so the
    /// <c>--background</c> tracker remains the sole writer.
    /// </summary>
    public static bool GuiAgent { get; set; }

    /// <summary>Park collectors / presence / stream / journey writers (GUI agent only).</summary>
    public static bool ParkTrackingWork => GuiAgent;

    /// <summary>Park SyncService (GUI agent only).</summary>
    public static bool ParkSyncWork => GuiAgent;
}
