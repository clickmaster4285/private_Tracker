namespace AlphaAITracker.Service;

/// <summary>
/// Persisted Fixed/Roaming control snapshot under
/// <c>%ProgramData%\&lt;APP_MUTEX&gt;\control\control.json</c> (service-owned).
/// Fail-safe defaults: mode=fixed, desired=running, phase=collecting.
/// </summary>
public sealed class TrackingControlState
{
    public string Mode { get; set; } = "fixed";
    public string DesiredState { get; set; } = "running";
    public string Phase { get; set; } = "collecting";
    public long ControlEpoch { get; set; }
    public long Generation { get; set; }
    public string? SessionStartedAt { get; set; }
    public long SessionAccumulatedActiveSec { get; set; }
    /// <summary>Local calendar day (<c>yyyy-MM-dd</c>) for the day timer bucket.</summary>
    public string? DayKey { get; set; }
    /// <summary>UTC instant of the first Start on <see cref="DayKey"/> (persists across restarts).</summary>
    public string? DayFirstStartedAt { get; set; }
    /// <summary>After Fixed→Roaming: open Start/Stop modal once drain completes.</summary>
    public bool OpenControlsPending { get; set; }
    public string StopWindow { get; set; } = "anytime";
    public bool RequireStopReason { get; set; }
    public bool KeepDlpWhenStopped { get; set; } = true;
    public int? MaxStoppedHours { get; set; }
    public string? EffectiveModeSource { get; set; }
    public string? LastControlCheckAt { get; set; }
    public string? LastControlAppliedAt { get; set; }
    public string? LastError { get; set; }
}
