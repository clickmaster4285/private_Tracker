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

    /// <summary>
    /// Tracking control role (Fixed/Roaming). Resolved by <c>TrackingModeGate</c>
    /// before the DI host starts producers.
    /// </summary>
    public static TrackingRunMode RunMode { get; set; } = TrackingRunMode.Full;

    /// <summary>
    /// Park collectors / presence / stream / journey writers.
    /// True for GuiAgent, ControlShell, and DrainOnly (producers off; sync may still run).
    /// </summary>
    public static bool ParkTrackingWork =>
        GuiAgent || RunMode != TrackingRunMode.Full;

    /// <summary>
    /// Park SyncService. DrainOnly must keep sync; ControlShell / GuiAgent park it.
    /// </summary>
    public static bool ParkSyncWork =>
        GuiAgent || RunMode is TrackingRunMode.ControlShell or TrackingRunMode.GuiAgent;

    /// <summary>Drain-only: SyncService runs; producers park.</summary>
    public static bool DrainOnly => RunMode == TrackingRunMode.DrainOnly;
}

/// <summary>Process role for Fixed/Roaming tracking control (newplan.md).</summary>
public enum TrackingRunMode
{
    /// <summary>Normal collectors + sync.</summary>
    Full = 0,
    /// <summary>Sync pending rows only (Roaming Stop drain).</summary>
    DrainOnly = 1,
    /// <summary>Tray + Start UI; zero collectors (Roaming stopped).</summary>
    ControlShell = 2,
    /// <summary>Separate Avalonia GUI agent (--gui).</summary>
    GuiAgent = 3,
}
