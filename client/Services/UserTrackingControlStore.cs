using System.Text.Json;

namespace client.Services;

/// <summary>
/// Cooperative Fixed/Roaming snapshot under LocalAppData (Linux/macOS).
/// Mirrors the Windows service <c>TrackingControlStore.ApplyServerControl</c> mode
/// semantics without LocalSystem ownership — not tamper-proof.
/// </summary>
public static class UserTrackingControlStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly object Gate = new();

    public sealed class State
    {
        public string Mode { get; set; } = "roaming";
        public string DesiredState { get; set; } = "running";
        public string Phase { get; set; } = "collecting";
        public long ControlEpoch { get; set; }
        public long Generation { get; set; }
        public string StopWindow { get; set; } = "anytime";
        public bool RequireStopReason { get; set; }
        public bool KeepDlpWhenStopped { get; set; } = true;
        public int? MaxStoppedHours { get; set; }
        public string? EffectiveModeSource { get; set; }
        public string? LastControlCheckAt { get; set; }
        public string? LastControlAppliedAt { get; set; }
        public string? LastError { get; set; }
        /// <summary>After Fixed→Roaming: open Start/Stop modal once drain completes.</summary>
        public bool OpenControlsPending { get; set; }
    }

    public static State Snapshot()
    {
        lock (Gate)
            return Clone(TryReadUnlocked() ?? new State());
    }

    public static void ApplyServerControl(
        string mode,
        string? source,
        string stopWindow,
        bool requireStopReason,
        bool keepDlpWhenStopped,
        int? maxStoppedHours,
        string desiredState,
        string phase,
        long controlEpoch)
    {
        lock (Gate)
        {
            var snap = TryReadUnlocked() ?? new State();
            var prevMode = (snap.Mode ?? "roaming").Trim().ToLowerInvariant();
            snap.Mode = string.IsNullOrWhiteSpace(mode) ? "fixed" : mode.Trim().ToLowerInvariant();
            if (snap.Mode is not ("fixed" or "roaming"))
                snap.Mode = "fixed";
            snap.EffectiveModeSource = source;
            snap.StopWindow = string.IsNullOrWhiteSpace(stopWindow) ? "anytime" : stopWindow;
            snap.RequireStopReason = requireStopReason;
            snap.KeepDlpWhenStopped = keepDlpWhenStopped;
            snap.MaxStoppedHours = maxStoppedHours;

            if (snap.Mode != "roaming")
            {
                // Fixed: always collecting; no Start/Stop modal.
                snap.DesiredState = "running";
                snap.Phase = "collecting";
                snap.OpenControlsPending = false;
                _ = desiredState;
                _ = phase;
            }
            else if (prevMode != "roaming")
            {
                // Fixed → Roaming: do NOT auto-start logging.
                snap.DesiredState = "stopped";
                snap.Phase = "draining";
                snap.Generation++;
                snap.OpenControlsPending = true;
                // Offline Start/Stop follower reads local-roaming.json — seed stopped.
                try { LocalRoamingControl.WriteStop(); }
                catch { /* best-effort */ }
            }
            // else: already roaming — keep local desired/phase (LocalRoamingControl is authoritative for Start/Stop).

            snap.ControlEpoch = controlEpoch;
            snap.LastControlAppliedAt = DateTime.UtcNow.ToString("o");
            snap.LastControlCheckAt = snap.LastControlAppliedAt;
            snap.LastError = null;
            PersistUnlocked(snap);
        }
    }

    public static void MarkCheck(string? error = null)
    {
        lock (Gate)
        {
            var snap = TryReadUnlocked() ?? new State();
            snap.LastControlCheckAt = DateTime.UtcNow.ToString("o");
            snap.LastError = error;
            PersistUnlocked(snap);
        }
    }

    private static State? TryReadUnlocked()
    {
        try
        {
            var path = TrackingControlPaths.ControlJsonPath;
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<State>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static void PersistUnlocked(State s)
    {
        var path = TrackingControlPaths.ControlJsonPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $"control.{Guid.NewGuid():N}.tmp");
        // Emit both mode and trackingMode so resolvers/view-models share one shape with Windows.
        var payload = new
        {
            mode = s.Mode,
            trackingMode = s.Mode,
            desiredState = s.DesiredState,
            phase = s.Phase,
            controlEpoch = s.ControlEpoch,
            generation = s.Generation,
            stopWindow = s.StopWindow,
            requireStopReason = s.RequireStopReason,
            keepDlpWhenStopped = s.KeepDlpWhenStopped,
            maxStoppedHours = s.MaxStoppedHours,
            effectiveModeSource = s.EffectiveModeSource,
            lastControlCheckAt = s.LastControlCheckAt,
            lastControlAppliedAt = s.LastControlAppliedAt,
            lastError = s.LastError,
            openControlsPending = s.OpenControlsPending,
        };
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    private static State Clone(State s) => new()
    {
        Mode = s.Mode,
        DesiredState = s.DesiredState,
        Phase = s.Phase,
        ControlEpoch = s.ControlEpoch,
        Generation = s.Generation,
        StopWindow = s.StopWindow,
        RequireStopReason = s.RequireStopReason,
        KeepDlpWhenStopped = s.KeepDlpWhenStopped,
        MaxStoppedHours = s.MaxStoppedHours,
        EffectiveModeSource = s.EffectiveModeSource,
        LastControlCheckAt = s.LastControlCheckAt,
        LastControlAppliedAt = s.LastControlAppliedAt,
        LastError = s.LastError,
        OpenControlsPending = s.OpenControlsPending,
    };
}
