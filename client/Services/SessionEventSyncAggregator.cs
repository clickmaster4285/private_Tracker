using client.Core.Models;

namespace client.Services;

/// <summary>
/// Builds 5-minute (configurable) aggregate sync payloads from raw session_events rows.
/// Local SQLite keeps one row per OS event (BUG-13); aggregation happens only when
/// SyncService constructs the server payload so abrupt shutdown never loses telemetry.
/// </summary>
public sealed record SessionEventAggregate(
    string SyncId,
    string EventType,
    string OsUsername,
    DateTime EventAt,
    int Count,
    DateTime FirstAt,
    DateTime LastAt,
    IReadOnlyList<string> SourceIds);

public static class SessionEventSyncAggregator
{
    /// <summary>
    /// Groups unsent rows into time buckets per event_type. Only buckets whose window
    /// has fully elapsed (<paramref name="utcNow"/> - window) are returned so a partial
    /// bucket never leaves the client before all events in that window are captured.
    /// </summary>
    public static IReadOnlyList<SessionEventAggregate> BuildAggregates(
        IReadOnlyList<SessionEvent> events,
        int windowSec,
        DateTime utcNow)
    {
        if (events.Count == 0)
            return Array.Empty<SessionEventAggregate>();

        if (windowSec <= 1)
        {
            return events
                .OrderBy(e => e.EventAt)
                .Select(e => new SessionEventAggregate(
                    e.Id,
                    e.EventType,
                    e.OsUsername,
                    e.EventAt,
                    1,
                    e.EventAt,
                    e.EventAt,
                    new[] { e.Id }))
                .ToList();
        }

        var window = TimeSpan.FromSeconds(windowSec);
        var groups = events
            .GroupBy(e => (e.EventType, BucketStart(e.EventAt, window)))
            .OrderBy(g => g.Key.Item2)
            .ThenBy(g => g.Key.EventType, StringComparer.Ordinal);

        var result = new List<SessionEventAggregate>();
        foreach (var group in groups)
        {
            var bucketStart = group.Key.Item2;
            var isSentinel = group.Key.EventType == SessionEventTypes.OldDataDropped;
            if (!isSentinel && bucketStart + window > utcNow)
                continue;

            var ordered = group.OrderBy(e => e.EventAt).ToList();
            var firstAt = ordered[0].EventAt;
            var lastAt = ordered[^1].EventAt;
            result.Add(new SessionEventAggregate(
                ordered[0].Id,
                ordered[0].EventType,
                ordered[0].OsUsername,
                firstAt,
                ordered.Count,
                firstAt,
                lastAt,
                ordered.Select(e => e.Id).ToList()));
        }

        return result;
    }

    private static DateTime BucketStart(DateTime utc, TimeSpan window)
    {
        var windowTicks = window.Ticks;
        var startTicks = utc.Ticks - (utc.Ticks % windowTicks);
        return new DateTime(startTicks, DateTimeKind.Utc);
    }
}
