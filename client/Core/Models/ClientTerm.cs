namespace client.Core.Models;

/// <summary>
/// One Terms &amp; Conditions entry as seen by the desktop client — a mirror of the
/// server's <c>terms_content</c> row plus the local acceptance state.
///
/// Equality rule (plan workstream 1): a server term is "seen" when
/// (Id, TermsVersion, ContentHash) matches a local row. Only NEW or CHANGED terms
/// insert a pending row (IsAccepted = 0); accepted rows are never duplicated.
/// ContentHash = SHA-256 of the normalized (heading + body) — computed by
/// TermsService before storing.
/// </summary>
public class ClientTerm
{
    /// <summary>Server terms_content.id (tc-001 … / UUID for admin-created terms).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Feature key (app_usage, browser_journey, file_journey, live_view …).
    /// Admin-created terms may carry an empty feature id.</summary>
    public string FeatureId { get; set; } = string.Empty;

    public string Heading { get; set; } = string.Empty;

    /// <summary>Raw server body (HTML). Kept verbatim in SQLite; the UI strips tags.</summary>
    public string Body { get; set; } = string.Empty;

    public string TermsVersion { get; set; } = "1.0";

    /// <summary>SHA-256 of normalized (heading + body) — detects silent content edits
    /// even when the admin forgot to bump TermsVersion.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Server presentation order (sort_order ASC, created_at ASC). Persisted so
    /// the offline pending queue keeps the server's sequence across reboots.</summary>
    public int SortOrder { get; set; }

    /// <summary>0 = pending acceptance (gates the shell), 1 = accepted (server acknowledged).</summary>
    public int IsAccepted { get; set; }

    /// <summary>1 = the user clicked "I agree" (may still be awaiting server ack).
    /// ONLY user-accepted rows are eligible for consent re-sending — treating a plain
    /// pending row as "agreed" auto-accepted terms the user never saw (2026-09-16 fix).</summary>
    public int IsUserAccepted { get; set; }

    /// <summary>UTC moment the server acknowledged the consent (NULL while pending).</summary>
    public DateTime? AcceptedAt { get; set; }

    /// <summary>Employee this row belongs to. Per-employee gating: switching users on the
    /// same machine recomputes the pending list from rows matching the current employee.</summary>
    public string EmployeeId { get; set; } = string.Empty;

    /// <summary>UTC of the last successful server sync of this consent (audit mirror).</summary>
    public DateTime? SyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Admin turned the term off server-side ("un-require") — drop from pending queue.</summary>
    public bool IsActive => true; // rows persisted while inactive are filtered at query time

    public bool IsPending => IsAccepted == 0;
}
