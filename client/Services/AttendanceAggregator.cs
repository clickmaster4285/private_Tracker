using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Time and Attendance (Phase 1, finalplan A.8): background service that rolls up
/// "today's" session-idle activity for the logged-in employee into
/// daily_attendance_cache — the local ARRIVAL / DEPARTURE / ACTIVE / IDLE picture.
///
/// Runs every 5 minutes (finalplan S8): the source data is 30s-granular, so a
/// 5-minute tick means the dashboard lags at most 5 minutes — honest.
///
/// The cache is DERIVED (client-owned) and NEVER sent to the server (finalplan
/// section 2.2): the server has its own aggregator for the public T&A view in
/// Phase 2. The local cache is a presentation hint, not a source of truth.
///
/// Inputs (read via the read-only connection, finalplan R8):
///   - session_events: power_on / resume (presence), idle_start / idle_end (A.4),
///     screen_lock / unlock (A.3).
///
/// Status: present | late | absent | half_day | off_shift | unknown. When the
/// employee has a mirrored schedule (A.6) we compare first-active vs the shift
/// start + grace; otherwise status is "unknown" (honest — we can't assert
/// "on time" without an assigned shift).
/// </summary>
public sealed class AttendanceAggregator : BackgroundService
{
    private static readonly TimeSpan AggregateInterval = TimeSpan.FromMinutes(5);

    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly ILogger<AttendanceAggregator> _logger;
    private readonly SemaphoreSlim _aggregateSignal = new(0, 1);

    public AttendanceAggregator(
        ILogStore store,
        AppConfig config,
        ILogger<AttendanceAggregator> logger)
    {
        _store = store;
        _config = config;
        _logger = logger;
    }

    /// <summary>Wake the rollup after login/session restore.</summary>
    public void RequestImmediateAggregation()
    {
        try
        {
            if (_aggregateSignal.CurrentCount == 0) _aggregateSignal.Release();
        }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("AttendanceAggregator parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!_config.TaEnabled)
        {
            _logger.LogInformation("AttendanceAggregator: ALPHA_TA_ENABLED=false - parked");
            return;
        }

        _logger.LogInformation("AttendanceAggregator starting (every {Min} min)", AggregateInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AggregateTodayAsync(stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AttendanceAggregator: pass failed");
            }

            try
            {
                await _aggregateSignal.WaitAsync(AggregateInterval, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task AggregateTodayAsync(CancellationToken ct)
    {
        var employee = await _store.GetEmployeeInfoAsync(ct);
        if (employee == null || string.IsNullOrWhiteSpace(employee.EmployeeId))
        {
            _logger.LogDebug("AttendanceAggregator: no logged-in employee - skip");
            return;
        }

        // Local "today" in the employee's schedule timezone (default UTC).
        var schedules = await _store.ListEmployeeSchedulesAsync(ct);
        var hasSchedule = schedules.Any(s =>
            string.Equals(s.EmployeeId, employee.EmployeeId, StringComparison.OrdinalIgnoreCase));
        var schedule = hasSchedule
            ? schedules.First(s => string.Equals(s.EmployeeId, employee.EmployeeId, StringComparison.OrdinalIgnoreCase))
            : ((string EmployeeId, string Timezone, string WeeklyPattern, int GraceMinutes)?)null;
        var tz = string.IsNullOrEmpty(schedule?.Timezone)
            ? TimeZoneInfo.Utc : SafeTz(schedule.Value.Timezone);
        var localNow = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        var workDate = localNow.ToString("yyyy-MM-dd");

        // Day window in UTC (read-only connection).
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localNow.Date, tz);
        var dayEndUtc = dayStartUtc.AddDays(1);
        var from = dayStartUtc.AddHours(-1);   // small pre-window for events stamped just before midnight
        var events = await _store.GetSessionEventsInRangeAsync(from, dayEndUtc, ct);
        var holidays = await _store.ListCompanyHolidaysAsync(ct);
        var isHoliday = holidays.Any(h => string.Equals(h.Date, workDate, StringComparison.Ordinal));

        // ── Presence ──
        // The first event proving that the employee can use the machine is arrival.
        // While this hosted service is running, "now" is the honest last-seen time;
        // session_events are sparse and using the last marker would freeze worked
        // time whenever no lock/idle transition occurred.
        var firstActiveAt = events
            .Where(e => e.EventAt >= dayStartUtc &&
                        (e.EventType == SessionEventTypes.PowerOn ||
                         e.EventType == SessionEventTypes.Resume ||
                         e.EventType == SessionEventTypes.TrackerLogin ||
                         e.EventType == SessionEventTypes.ScreenUnlock ||
                         e.EventType == SessionEventTypes.IdleEnd))
            .Select(e => e.EventAt)
            .Cast<DateTime?>()
            .Min();
        var lastSeenAt = firstActiveAt.HasValue
            ? (DateTime.UtcNow < dayEndUtc ? DateTime.UtcNow : dayEndUtc)
            : (DateTime?)null;

        var presenceSeconds = firstActiveAt.HasValue && lastSeenAt.HasValue
            ? Math.Max(0, (lastSeenAt.Value - firstActiveAt.Value).TotalSeconds)
            : 0;
        var idleSeconds = firstActiveAt.HasValue && lastSeenAt.HasValue
            ? CalculateInactiveSeconds(events, firstActiveAt.Value, lastSeenAt.Value)
            : 0;
        idleSeconds = Math.Min(idleSeconds, presenceSeconds);
        var activeSeconds = Math.Max(0, presenceSeconds - idleSeconds);

        // ── Status ──
        var status = "unknown";
        var lateMinutes = 0;
        var offShiftSeconds = 0d;
        if (schedule.HasValue)
        {
            var weekDay = ToScheduleDayKey(localNow.DayOfWeek);
            var (shiftStart, shiftEnd) = ParseShift(schedule.Value.WeeklyPattern, weekDay);
            if (isHoliday || !shiftStart.HasValue || !shiftEnd.HasValue)
            {
                status = "off_shift";
                offShiftSeconds = presenceSeconds;
            }
            else if (!firstActiveAt.HasValue)
            {
                status = "absent";
            }
            else
            {
                var graceMinutes = schedule.Value.GraceMinutes;
                var shiftStartToday = localNow.Date.Add(shiftStart.Value);
                var shiftEndToday = localNow.Date.Add(shiftEnd.Value);
                if (shiftEndToday <= shiftStartToday) shiftEndToday = shiftEndToday.AddDays(1);
                var startWithGrace = shiftStartToday.AddMinutes(graceMinutes);
                var firstActiveLocal = TimeZoneInfo.ConvertTime(firstActiveAt.Value, tz);
                var lastSeenLocal = TimeZoneInfo.ConvertTime(lastSeenAt!.Value, tz);
                var overlapStart = firstActiveLocal > shiftStartToday ? firstActiveLocal : shiftStartToday;
                var overlapEnd = lastSeenLocal < shiftEndToday ? lastSeenLocal : shiftEndToday;
                var inShiftSeconds = Math.Max(0, (overlapEnd - overlapStart).TotalSeconds);
                offShiftSeconds = Math.Max(0, presenceSeconds - inShiftSeconds);

                var shiftSeconds = (shiftEndToday - shiftStartToday).TotalSeconds;
                if (localNow >= shiftEndToday && inShiftSeconds < shiftSeconds / 2)
                {
                    status = "half_day";
                }
                else if (firstActiveLocal > startWithGrace)
                {
                    status = "late";
                    lateMinutes = (int)(firstActiveLocal - startWithGrace).TotalMinutes;
                }
                else
                {
                    status = "present";
                }
            }
        }

        await _store.UpsertDailyAttendanceAsync(
            employee.EmployeeId,
            workDate,
            firstActiveAt,
            lastSeenAt,
            (int)Math.Round(activeSeconds),
            (int)Math.Round(idleSeconds),
            (int)Math.Round(offShiftSeconds),
            status,
            lateMinutes,
            ct);

        _logger.LogDebug(
            "AttendanceAggregator: {Date} {Emp} active={A}s idle={I}s status={Status} late={Late}m",
            workDate, employee.EmployeeId, (int)activeSeconds, (int)idleSeconds, status, lateMinutes);

        // High-water-mark key (finalplan section 5 S3).
        await _store.SetStatusAsync("ta_last_aggregator_run_at", DateTime.UtcNow.ToString("O"), ct);
    }

    private static TimeZoneInfo SafeTz(string iana)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static string ToScheduleDayKey(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "mon",
        DayOfWeek.Tuesday => "tue",
        DayOfWeek.Wednesday => "wed",
        DayOfWeek.Thursday => "thu",
        DayOfWeek.Friday => "fri",
        DayOfWeek.Saturday => "sat",
        DayOfWeek.Sunday => "sun",
        _ => throw new ArgumentOutOfRangeException(nameof(day)),
    };

    /// <summary>
    /// Returns the union of idle and screen-lock intervals. Tracking the two
    /// states separately avoids double-counting when an idle period overlaps a
    /// lock period.
    /// </summary>
    private static double CalculateInactiveSeconds(
        IReadOnlyList<SessionEvent> events,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        var idle = false;
        var locked = false;
        DateTime? inactiveStart = null;
        var total = 0d;

        foreach (var e in events.OrderBy(e => e.EventAt))
        {
            if (e.EventAt > rangeEnd) break;
            var wasInactive = idle || locked;
            switch (e.EventType)
            {
                case SessionEventTypes.IdleStart:
                    idle = true;
                    break;
                case SessionEventTypes.IdleEnd:
                    idle = false;
                    break;
                case SessionEventTypes.ScreenLock:
                    locked = true;
                    break;
                case SessionEventTypes.ScreenUnlock:
                    locked = false;
                    break;
                default:
                    continue;
            }

            var isInactive = idle || locked;
            if (!wasInactive && isInactive)
            {
                inactiveStart = e.EventAt < rangeStart ? rangeStart : e.EventAt;
            }
            else if (wasInactive && !isInactive && inactiveStart.HasValue)
            {
                var end = e.EventAt > rangeEnd ? rangeEnd : e.EventAt;
                if (end > inactiveStart.Value)
                    total += (end - inactiveStart.Value).TotalSeconds;
                inactiveStart = null;
            }
        }

        if ((idle || locked) && inactiveStart.HasValue && rangeEnd > inactiveStart.Value)
            total += (rangeEnd - inactiveStart.Value).TotalSeconds;

        return Math.Max(0, total);
    }

    /// <summary>
    /// Parses the mirrored schedule's weekly_pattern JSON for today's shift start.
    /// Returns (start, null) when the day has a shift, else (null, null).
    /// </summary>
    private static (TimeSpan? Start, TimeSpan? End) ParseShift(string weeklyPatternJson, string weekDay)
    {
        if (string.IsNullOrWhiteSpace(weeklyPatternJson)) return (null, null);
        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                weeklyPatternJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (map == null || !map.TryGetValue(weekDay, out var range) || string.IsNullOrWhiteSpace(range))
                return (null, null);

            // "09:00-18:00"
            var parts = range.Split('-');
            if (parts.Length != 2) return (null, null);
            if (!TimeSpan.TryParse(parts[0], out var start)) return (null, null);
            if (!TimeSpan.TryParse(parts[1], out var end)) return (null, null);
            return (start, end);
        }
        catch
        {
            return (null, null);
        }
    }
}
