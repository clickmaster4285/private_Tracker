using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Atomic JSON store for tracking control (temp → rename, keep .bak).
/// Lives under ProgramData so wipe of user SQLite cannot erase mode/desired state.
/// </summary>
public sealed class TrackingControlStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private TrackingControlState _state = new();

    public static string RootDir => DlpDataPaths.ControlRoot;

    public static string FilePath => Path.Combine(RootDir, "control.json");
    public static string BakPath => Path.Combine(RootDir, "control.json.bak");

    public TrackingControlState Snapshot()
    {
        lock (_gate)
        {
            return Clone(_state);
        }
    }

    public void LoadOrDefault()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(RootDir);
            _state = TryRead(FilePath) ?? TryRead(BakPath) ?? FailSafeFixed();
            NormalizeFailSafe(ref _state);
        }
    }

    public void Save(TrackingControlState next)
    {
        lock (_gate)
        {
            NormalizeFailSafe(ref next);
            Directory.CreateDirectory(RootDir);
            var tmp = Path.Combine(RootDir, $"control.{Guid.NewGuid():N}.tmp");
            var json = JsonSerializer.Serialize(next, JsonOpts);
            File.WriteAllText(tmp, json);
            try
            {
                if (File.Exists(FilePath))
                    File.Copy(FilePath, BakPath, overwrite: true);
            }
            catch { /* bak is best-effort */ }

            File.Move(tmp, FilePath, overwrite: true);
            _state = Clone(next);
        }
    }

    public void ApplyServerControl(
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
        var snap = Snapshot();
        var prevMode = (snap.Mode ?? "fixed").Trim().ToLowerInvariant();
        snap.Mode = string.IsNullOrWhiteSpace(mode) ? "fixed" : mode.Trim().ToLowerInvariant();
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
            // Fixed → Roaming: do NOT auto-start logging. Drain unsynced first, then modal.
            AccumulateRunningSegmentUnlocked(snap, DateTimeOffset.UtcNow);
            snap.DesiredState = "stopped";
            snap.Phase = "draining";
            snap.Generation++;
            ResetDayClockUnlocked(snap);
            snap.OpenControlsPending = true;
        }
        // else: already roaming — keep local desired/phase (user Start/Stop is authoritative).

        snap.ControlEpoch = controlEpoch;
        snap.LastControlAppliedAt = DateTime.UtcNow.ToString("o");
        snap.LastError = null;
        Save(snap);
    }

    public void MarkCheck(string? error = null)
    {
        var snap = Snapshot();
        snap.LastControlCheckAt = DateTime.UtcNow.ToString("o");
        snap.LastError = error;
        Save(snap);
    }

    /// <summary>
    /// Roaming Stop: persist desired=stopped + phase=draining first (crash-safe), bump generation.
    /// Rejects stale client generation and Fixed mode.
    /// </summary>
    public (bool Ok, string? Error, TrackingControlState State) TryRequestStop(
        string? reason, long? clientGeneration)
    {
        lock (_gate)
        {
            var s = Clone(_state);
            var mode = (s.Mode ?? "fixed").Trim().ToLowerInvariant();
            if (mode != "roaming")
                return (false, "mode_fixed", s);

            if (clientGeneration is long g && g > 0 && g < s.Generation)
                return (false, "stale_generation", s);

            if (s.RequireStopReason && string.IsNullOrWhiteSpace(reason))
                return (false, "reason_required", s);

            // Idempotent: already stopping / stopped.
            if (s.DesiredState == "stopped" && s.Phase is "draining" or "idle")
                return (true, null, s);

            var now = DateTimeOffset.UtcNow;
            EnsureDayBucketUnlocked(s, now);
            AccumulateRunningSegmentUnlocked(s, now);

            s.DesiredState = "stopped";
            s.Phase = "draining";
            s.Generation++;
            s.LastError = null;
            NormalizeFailSafe(ref s);
            PersistUnlocked(s);
            return (true, null, Clone(_state));
        }
    }

    /// <summary>Roaming Start (or Fixed force-running): desired=running, phase=starting.</summary>
    public (bool Ok, string? Error, TrackingControlState State) TryRequestStart(
        string? workLocation, long? clientGeneration)
    {
        lock (_gate)
        {
            var s = Clone(_state);
            var mode = (s.Mode ?? "fixed").Trim().ToLowerInvariant();

            if (clientGeneration is long g && g > 0 && g < s.Generation)
                return (false, "stale_generation", s);

            if (mode != "roaming" && mode != "fixed")
                mode = "fixed";

            var now = DateTimeOffset.UtcNow;

            // Fixed is always running — treat Start as ack.
            if (mode == "fixed")
            {
                s.DesiredState = "running";
                s.Phase = "collecting";
                if (string.IsNullOrWhiteSpace(s.SessionStartedAt))
                    s.SessionStartedAt = now.ToString("o");
                PersistUnlocked(s);
                return (true, null, Clone(_state));
            }

            if (s.DesiredState == "running" && s.Phase is "collecting" or "starting")
                return (true, null, s);

            EnsureDayBucketUnlocked(s, now);
            // Same-day restart: keep DayFirstStartedAt + accumulated; only mint a new segment.
            if (string.IsNullOrWhiteSpace(s.DayFirstStartedAt))
                s.DayFirstStartedAt = now.ToString("o");
            s.SessionStartedAt = now.ToString("o");

            s.DesiredState = "running";
            s.Phase = "starting";
            s.Generation++;
            s.OpenControlsPending = false;
            s.LastError = null;
            _ = workLocation;
            NormalizeFailSafe(ref s);
            PersistUnlocked(s);
            return (true, null, Clone(_state));
        }
    }

    /// <summary>Tracker reports phase after Pause/Start/drain (same generation).</summary>
    public (bool Ok, string? Error, TrackingControlState State) TryReportPhase(
        string phase, long? generation)
    {
        lock (_gate)
        {
            var s = Clone(_state);
            if (generation is long g && g > 0 && g != s.Generation)
                return (false, "stale_generation", s);

            var p = (phase ?? "").Trim().ToLowerInvariant();
            if (p is not ("starting" or "collecting" or "draining" or "idle" or "error"))
                return (false, "invalid_phase", s);

            s.Phase = p;
            var now = DateTimeOffset.UtcNow;
            if (p == "collecting" && s.DesiredState == "running")
            {
                EnsureDayBucketUnlocked(s, now);
                if (string.IsNullOrWhiteSpace(s.DayFirstStartedAt))
                    s.DayFirstStartedAt = now.ToString("o");
                // Same-day restart while desired=running: keep existing SessionStartedAt
                // so the day timer does not reset.
                if (string.IsNullOrWhiteSpace(s.SessionStartedAt))
                    s.SessionStartedAt = now.ToString("o");
            }
            if (p == "idle" && s.DesiredState == "stopped")
            {
                s.LastError = null;
                s.OpenControlsPending = true;
            }
            NormalizeFailSafe(ref s);
            PersistUnlocked(s);
            return (true, null, Clone(_state));
        }
    }

    /// <summary>Active seconds for the current local calendar day only.</summary>
    public static long ComputeElapsedActiveSec(TrackingControlState s, DateTimeOffset nowUtc)
    {
        var dayStart = StartOfLocalDay(nowUtc);
        var total = Math.Max(0, s.SessionAccumulatedActiveSec);
        if (string.Equals(s.DesiredState, "running", StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.TryParse(s.SessionStartedAt, out var started))
        {
            if (started < dayStart)
                started = dayStart;
            total += (long)Math.Max(0, (nowUtc - started).TotalSeconds);
        }
        return total;
    }

    private static void EnsureDayBucketUnlocked(TrackingControlState s, DateTimeOffset nowUtc)
    {
        var dayKey = nowUtc.ToLocalTime().ToString("yyyy-MM-dd");
        if (string.Equals(s.DayKey, dayKey, StringComparison.Ordinal))
            return;
        // Match offline modal: new day clears working timer and stops (no midnight→clock lookalike).
        s.DayKey = dayKey;
        s.SessionAccumulatedActiveSec = 0;
        s.DayFirstStartedAt = null;
        s.SessionStartedAt = null;
        s.DesiredState = "stopped";
        s.Phase = "idle";
        s.Generation++;
    }

    private static DateTimeOffset StartOfLocalDay(DateTimeOffset nowUtc)
    {
        var local = nowUtc.ToLocalTime();
        var midnightLocal = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
        return midnightLocal.ToUniversalTime();
    }

    private static void ResetDayClockUnlocked(TrackingControlState s)
    {
        s.DayKey = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        s.DayFirstStartedAt = null;
        s.SessionStartedAt = null;
        s.SessionAccumulatedActiveSec = 0;
    }

    private static void AccumulateRunningSegmentUnlocked(TrackingControlState s, DateTimeOffset nowUtc)
    {
        if (!DateTimeOffset.TryParse(s.SessionStartedAt, out var started))
        {
            s.SessionStartedAt = null;
            return;
        }
        var dayStart = StartOfLocalDay(nowUtc);
        if (started < dayStart)
            started = dayStart;
        s.SessionAccumulatedActiveSec += (long)Math.Max(0, (nowUtc - started).TotalSeconds);
        s.SessionStartedAt = null;
    }

    private void PersistUnlocked(TrackingControlState next)
    {
        NormalizeFailSafe(ref next);
        Directory.CreateDirectory(RootDir);
        var tmp = Path.Combine(RootDir, $"control.{Guid.NewGuid():N}.tmp");
        var json = JsonSerializer.Serialize(next, JsonOpts);
        File.WriteAllText(tmp, json);
        try
        {
            if (File.Exists(FilePath))
                File.Copy(FilePath, BakPath, overwrite: true);
        }
        catch { /* bak best-effort */ }

        File.Move(tmp, FilePath, overwrite: true);
        _state = Clone(next);
    }

    private static TrackingControlState FailSafeFixed() => new();

    private static void NormalizeFailSafe(ref TrackingControlState s)
    {
        var mode = (s.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("fixed" or "roaming"))
            mode = "fixed";
        s.Mode = mode;

        var desired = (s.DesiredState ?? "").Trim().ToLowerInvariant();
        // Roaming defaults to stopped (user must Start); Fixed always running.
        if (desired is not ("running" or "stopped"))
            desired = mode == "fixed" ? "running" : "stopped";
        s.DesiredState = desired;

        var phase = (s.Phase ?? "").Trim().ToLowerInvariant();
        if (phase is not ("starting" or "collecting" or "draining" or "idle" or "error"))
            phase = desired == "running" ? "collecting" : "idle";
        s.Phase = phase;

        if (string.IsNullOrWhiteSpace(s.StopWindow))
            s.StopWindow = "anytime";
    }

    private static TrackingControlState? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TrackingControlState>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static TrackingControlState Clone(TrackingControlState s) => new()
    {
        Mode = s.Mode,
        DesiredState = s.DesiredState,
        Phase = s.Phase,
        ControlEpoch = s.ControlEpoch,
        Generation = s.Generation,
        SessionStartedAt = s.SessionStartedAt,
        SessionAccumulatedActiveSec = s.SessionAccumulatedActiveSec,
        DayKey = s.DayKey,
        DayFirstStartedAt = s.DayFirstStartedAt,
        OpenControlsPending = s.OpenControlsPending,
        StopWindow = s.StopWindow,
        RequireStopReason = s.RequireStopReason,
        KeepDlpWhenStopped = s.KeepDlpWhenStopped,
        MaxStoppedHours = s.MaxStoppedHours,
        EffectiveModeSource = s.EffectiveModeSource,
        LastControlCheckAt = s.LastControlCheckAt,
        LastControlAppliedAt = s.LastControlAppliedAt,
        LastError = s.LastError,
    };
}
