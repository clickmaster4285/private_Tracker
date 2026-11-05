using client.Core.Models;

namespace client.Core.Abstractions;

/// <summary>
/// The single funnel for ALL session_events writes in the client.
///
/// Why an interface (and not just <c>LogCollectorService.RecordSessionEventAsync</c>):
///   - Centralises debounce/idempotency policy in one place (the implementation).
///   - Lets the SystemEventWatcher, ShutdownSentinel, IdleDetector, and LogCollector
///     all write through the same path with the same dedup + ordered-flush guarantees.
///   - Mockable for unit tests (T1 in the finalplan).
///   - One seam to swap (e.g. for a channel-based pipeline) without touching every caller.
///
/// Contract:
///   - RecordAsync writes a single event. The implementation MUST dedup events of the
///     same type arriving within a short window (5 s default) so two near-simultaneous
///     sources (e.g. UPower + ScreenSaver) don't produce 2 rows.
///   - RecordBatchAsync flushes multiple events in ONE SQLite transaction (single fsync).
///   - PeekRecentAsync is a read-only inspection helper for diagnostics + tests; it
///     does NOT clear the dedup cache.
///   - Implementations MUST be safe to call from any thread; the recorder owns its
///     own gate internally so callers don't have to.
///   - Implementations MUST NOT block shutdown longer than a hard 2-second timeout
///     (R7 in the finalplan: telemetry must never delay process exit).
/// </summary>
public interface IEventRecorder
{
    /// <summary>Record a single event. Idempotent within the dedup window.</summary>
    /// <param name="eventType">One of the <see cref="SessionEventTypes"/> constants.</param>
    /// <param name="osUsername">OS user the event belongs to (defaults to Environment.UserName when null).</param>
    /// <param name="at">UTC timestamp (defaults to DateTime.UtcNow when null).</param>
    /// <param name="meta">Optional metadata key/value pairs (e.g. source = "logind").</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordAsync(
        string eventType,
        string? osUsername = null,
        DateTime? at = null,
        IReadOnlyDictionary<string, string>? meta = null,
        CancellationToken ct = default);

    /// <summary>Flush multiple events in a single SQLite transaction. Bypasses the
    /// dedup window — call this only when you genuinely want every row.</summary>
    Task RecordBatchAsync(
        IEnumerable<SessionEvent> events,
        CancellationToken ct = default);

    /// <summary>Return the most recent N events from the local SQLite, newest first.
    /// Used by diagnostics and unit tests; does NOT touch the dedup cache.</summary>
    Task<IReadOnlyList<SessionEvent>> PeekRecentAsync(
        int limit,
        CancellationToken ct = default);
}
