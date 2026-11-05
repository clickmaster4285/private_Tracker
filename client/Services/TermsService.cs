using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Terms &amp; Conditions acceptance gate (2026-09-16) — fetch / diff / accept service.
///
/// Follows the ScheduleCacheService pattern (A.6): pull on login / session-restore /
/// resume via a coalesced wake signal, then on a periodic timer (ALPHA_TERMS_CHECK_HOURS).
///
/// <para><b>Fetch</b> — GET /api/v1/terms-content/active with the employee Device token
/// (Bearer is the legacy fallback). The endpoint lives under DeviceAuth — see the
/// Client-vs-Web API Auth Separation Rule in AGENTS.md §6.</para>
///
/// <para><b>Diff</b> — a server term is "seen" when (id, terms_version, content_hash)
/// matches a local client_terms row. New or changed terms insert a PENDING row;
/// accepted rows are never duplicated. Deactivated/deleted terms are purged from the
/// pending queue (an admin "un-require" signal); accepted rows are retained.</para>
///
/// <para><b>Accept</b> — POST /terms-consent/sync immediately; the local row is marked
/// accepted ONLY after a 2xx. An unacknowledged acceptance stays pending and retries on
/// the next cycle (same principle as the 2026-09-09 sync-fix rule: a row the server
/// refused must never masquerade as accepted/synced).</para>
///
/// <para><b>Offline</b> — if the fetch fails, the locally cached pending queue still
/// gates the UI (never block on the network). With no cached state and no network the
/// client proceeds and retries next cycle (UI-gate-only v1; headless boots are never
/// gated — review decision 2026-09-16).</para>
///
/// <para><b>Events</b> — PendingTermsChanged fires after every refresh so the
/// MainViewModel router can raise/lower the fullscreen gate mid-session.</para>
/// </summary>
public sealed class TermsService : BackgroundService
{
    private static readonly TimeSpan FirstPullDelay = TimeSpan.FromSeconds(20);

    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TermsService> _logger;
    private readonly SemaphoreSlim _pullSignal = new(0, 1);
    private readonly object _signalGate = new();
    private DateTime _notBeforeUtc = DateTime.MinValue;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Raised whenever the pending queue may have changed (fetch completed,
    /// term accepted, purge ran). The ViewModel re-reads HasPendingTerms after this.</summary>
    public event Action? PendingTermsChanged;

    /// <summary>True when the last completed fetch served from the local cache because
    /// the server was unreachable — the UI shows an honest offline notice.</summary>
    public bool LastRefreshWasOffline { get; private set; }

    /// <summary>True inside the standalone terms-agent process (client.exe --terms).
    /// Suppresses the tracker-side spawn logic and the background retry loop — the
    /// agent does its consent retrying inline as the user accepts, then exits.</summary>
    public static bool IsAgentMode { get; set; }

    public TermsService(
        ILogStore store,
        AppConfig config,
        HttpClient httpClient,
        ILogger<TermsService> logger)
    {
        _store = store;
        _config = config;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>Wake the terms loop after login / session restore / resume / mid-session
    /// re-check requests. Repeated requests coalesce into one pull (ScheduleCacheService
    /// pattern).</summary>
    public void RequestImmediatePull()
    {
        lock (_signalGate)
        {
            if (DateTime.UtcNow > _notBeforeUtc) _notBeforeUtc = DateTime.UtcNow;
        }
        try
        {
            if (_pullSignal.CurrentCount == 0) _pullSignal.Release();
        }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.TermsEnabled)
        {
            _logger.LogInformation("TermsService: ALPHA_TERMS_ENABLED=false - parked");
            return;
        }

        try { await _pullSignal.WaitAsync(FirstPullDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTime notBefore;
                lock (_signalGate)
                {
                    notBefore = _notBeforeUtc;
                    _notBeforeUtc = DateTime.MinValue;
                }
                var delay = notBefore - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stoppingToken);

                await RefreshAsync(stoppingToken);
                if (!IsAgentMode)
                {
                    await RetryUnacknowledgedConsentsAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TermsService: refresh failed (server unreachable?)");
            }

            try
            {
                await _pullSignal.WaitAsync(TimeSpan.FromHours(_config.TermsCheckHours), stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>True when the logged-in employee still has unaccepted terms — the
    /// router raises the fullscreen gate while this holds.</summary>
    public async Task<bool> HasPendingTermsAsync(CancellationToken ct = default)
    {
        var employee = await _store.GetEmployeeInfoAsync(ct);
        if (employee == null || string.IsNullOrWhiteSpace(employee.EmployeeId)) return false;
        return await _store.CountPendingClientTermsAsync(employee.EmployeeId, ct) > 0;
    }

    /// <summary>Ordered pending queue for the acceptance UI (sort_order, created_at).</summary>
    public async Task<IReadOnlyList<ClientTerm>> GetPendingTermsAsync(CancellationToken ct = default)
    {
        var employee = await _store.GetEmployeeInfoAsync(ct);
        if (employee == null || string.IsNullOrWhiteSpace(employee.EmployeeId))
            return Array.Empty<ClientTerm>();
        return await _store.GetPendingClientTermsAsync(employee.EmployeeId, ct);
    }

    /// <summary>Record the user's agreement locally WITHOUT claiming the server
    /// acknowledged it. The row stays pending (gates the shell) until the consent POST
    /// succeeds; the retry loop only re-sends user-accepted rows.</summary>
    public async Task MarkUserAcceptedAsync(ClientTerm term, CancellationToken ct = default)
    {
        var employee = await _store.GetEmployeeInfoAsync(ct)
            ?? throw new InvalidOperationException("No logged-in employee");
        await _store.MarkClientTermUserAcceptedAsync(term.Id, employee.EmployeeId, ct);
    }

    /// <summary>
    /// Pull the active terms catalog, diff it into client_terms and purge stale pending
    /// rows. Best-effort by design: failures leave the cached queue in place.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.ServerUrl)) return;

        var employee = await _store.GetEmployeeInfoAsync(ct);
        if (employee == null || string.IsNullOrWhiteSpace(employee.EmployeeId))
        {
            _logger.LogDebug("TermsService: no logged-in employee - skip");
            return;
        }

        List<ServerTerm>? serverTerms;
        try
        {
            serverTerms = await FetchActiveTermsAsync(employee, ct);
            LastRefreshWasOffline = false;
        }
        catch (HttpRequestException ex)
        {
            // LOUD, not silent: a 401 here means the persisted employee credentials no
            // longer match the server (DB restore / secret rotation); a 5xx means the
            // server is unhealthy. The cached pending queue still gates either way.
            LastRefreshWasOffline = true;
            serverTerms = null;
            _logger.LogWarning(
                "TermsService: fetch failed ({Detail}) - serving {Pending} pending term(s) from cache",
                ex.StatusCode.HasValue ? $"HTTP {(int)ex.StatusCode.Value}" : ex.Message,
                await _store.CountPendingClientTermsAsync(employee.EmployeeId, ct));
        }
        catch (Exception ex)
        {
            // Offline: keep the cached pending queue (it still gates the UI). Nothing
            // to diff until the network returns; the next cycle retries.
            LastRefreshWasOffline = true;
            serverTerms = null;
            _logger.LogWarning(
                "TermsService: fetch failed ({Reason}) - serving {Pending} pending term(s) from cache",
                ex.Message,
                await _store.CountPendingClientTermsAsync(employee.EmployeeId, ct));
        }

        if (serverTerms != null)
        {
            var changed = 0;
            foreach (var t in serverTerms)
            {
                var local = new ClientTerm
                {
                    Id = t.Id,
                    FeatureId = t.FeatureId ?? string.Empty,
                    Heading = t.Heading ?? string.Empty,
                    Body = t.Body ?? string.Empty,
                    TermsVersion = string.IsNullOrWhiteSpace(t.TermsVersion) ? "1.0" : t.TermsVersion,
                    ContentHash = ComputeContentHash(t.Heading, t.Body),
                    SortOrder = t.SortOrder,
                    EmployeeId = employee.EmployeeId,
                };
                await _store.UpsertClientTermAsync(local, ct);
                changed++;
            }

            // Admin un-required terms (deactivated or deleted) drop out of the pending
            // queue; accepted rows stay as the local audit mirror.
            var activeIds = new HashSet<string>(serverTerms.Select(t => t.Id), StringComparer.Ordinal);
            var purged = await _store.PurgeStalePendingClientTermsAsync(employee.EmployeeId, activeIds, ct);

            _logger.LogInformation(
                "TermsService: refreshed {Count} active terms (purged {Purged} stale pending) for {Employee}",
                changed, purged, employee.EmployeeId);
        }

        // Instance-3 spawn + notify — ALWAYS, even on a failed fetch: the local cache
        // still holds pending terms and they must still be shown. (The previous
        // early-return on fetch failure silently skipped this — pending terms sat in
        // SQLite with no window and no explanation.)
        if (!IsAgentMode)
        {
            var pendingNow = await _store.GetPendingClientTermsAsync(employee.EmployeeId, ct);
            if (pendingNow.Count > 0)
            {
                SpawnTermsAgent();
            }
        }

        PendingTermsChanged?.Invoke();
    }

    /// <summary>
    /// Launch the standalone terms-agent process (client.exe --terms). Fire-and-forget:
    /// the agent is its own GUI app with its own single-instance mutex; the tracker
    /// neither waits for it nor blocks on its exit. Runs from the exe's own directory
    /// so installed builds and `dotnet run` both resolve the same binary.
    /// </summary>
    private void SpawnTermsAgent()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var exeName = OperatingSystem.IsWindows() ? "client.exe" : "client";
            var exePath = Path.Combine(exeDir, exeName);
            if (!File.Exists(exePath))
            {
                _logger.LogDebug("TermsService: terms-agent exe not found at {Path} - skipping spawn", exePath);
                return;
            }

            // Coalesce: at most one spawn attempt per 60s (the fetch cycle re-fires
            // this on every refresh while terms remain pending).
            if ((DateTime.UtcNow - _lastAgentSpawnUtc) < TimeSpan.FromSeconds(60)) return;
            _lastAgentSpawnUtc = DateTime.UtcNow;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--terms",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);
            _logger.LogInformation("TermsService: spawned standalone terms-agent (--terms)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TermsService: failed to spawn terms-agent");
        }
    }

    private DateTime _lastAgentSpawnUtc = DateTime.MinValue;

    /// <summary>
    /// Full accept path for one pending term: record the user's agreement locally, POST
    /// the consent entry to /terms-consent/sync (DeviceAuth), and only mark the local row
    /// accepted after a 2xx. Throws on failure so the UI can keep the term pending and
    /// retry.
    /// </summary>
    public async Task AcceptAsync(ClientTerm term, CancellationToken ct = default)
    {
        await MarkUserAcceptedAsync(term, ct);
        await SendConsentAsync(term, ct);
    }

    /// <summary>POST the consent entry; on 2xx mark the local row accepted. Called by the
    /// UI accept path and by the retry loop (user-accepted rows only).</summary>
    private async Task SendConsentAsync(ClientTerm term, CancellationToken ct)
    {
        var employee = await _store.GetEmployeeInfoAsync(ct)
            ?? throw new InvalidOperationException("No logged-in employee - cannot record consent");

        var payload = new
        {
            entries = new[]
            {
                new
                {
                    featureId = term.FeatureId,
                    termsVersion = term.TermsVersion,
                    action = "accepted",
                    acceptedAt = DateTime.UtcNow.ToString("O"),
                    revokedAt = (string?)null,
                }
            }
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_config.ServerUrl?.TrimEnd('/')}/api/v1/terms-consent/sync")
        {
            Content = content,
        };
        ApplyAuthHeader(request, employee);

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // NOT marked accepted locally — the user-acknowledged-but-unsynced row stays
            // pending and retries on the next cycle (2026-09-09 sync-fix principle).
            throw new HttpRequestException(
                $"Consent sync failed with {(int)response.StatusCode} {response.StatusCode}");
        }

        await _store.MarkClientTermAcceptedAsync(
            term.Id, employee.EmployeeId, DateTime.UtcNow, ct);
        _logger.LogInformation(
            "TermsService: consent acknowledged for term {TermId} ({Feature}) by {Employee}",
            term.Id, term.FeatureId, employee.EmployeeId);
    }

    /// <summary>
    /// Re-send consents the USER agreed to but the server never acknowledged
    /// (accept-time outage). Rows stay pending until a 2xx lands.
    /// ⚠️ ONLY is_user_accepted=1 rows are eligible — treating every pending row as
    /// "agreed" auto-accepted terms the user had never seen (2026-09-16 fix; the gate
    /// then never opened because the queue silently emptied behind the GUI).
    /// </summary>
    private async Task RetryUnacknowledgedConsentsAsync(CancellationToken ct)
    {
        var employee = await _store.GetEmployeeInfoAsync(ct);
        if (employee == null || string.IsNullOrWhiteSpace(employee.EmployeeId)) return;

        var pending = await _store.GetPendingClientTermsAsync(employee.EmployeeId, ct);
        foreach (var term in pending.Where(t => t.IsUserAccepted == 1))
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await SendConsentAsync(term, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("TermsService: consent retry for {TermId} failed - stays pending ({Reason})",
                    term.Id, ex.Message);
            }
        }
    }

    private async Task<List<ServerTerm>> FetchActiveTermsAsync(EmployeeInfo employee, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_config.ServerUrl?.TrimEnd('/')}/api/v1/terms-content/active");
        ApplyAuthHeader(request, employee);

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GET /terms-content/active returned {(int)response.StatusCode}");

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<TermsPayload>(stream, JsonOpts, ct);
        return payload?.Items?.ToList() ?? new List<ServerTerm>();
    }

    private static void ApplyAuthHeader(HttpRequestMessage request, EmployeeInfo employee)
    {
        // Client surface = DeviceAuth. Device token first; Bearer only as the legacy
        // fallback (mirrors ScheduleCacheService / SyncService).
        if (!string.IsNullOrWhiteSpace(employee.DeviceToken))
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Device", employee.DeviceToken);
        else
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", employee.Token);
        ClientIdentityHeaders.Apply(request);
    }

    /// <summary>SHA-256 of normalized (heading + body): trimmed, CRLF→LF, tag-whitespace
    /// collapsed — so invisible editor churn never re-prompts everyone.</summary>
    internal static string ComputeContentHash(string? heading, string? body)
    {
        static string Normalize(string? s) => (s ?? string.Empty)
            .Replace("\r\n", "\n")
            .Trim();

        var combined = Normalize(heading) + "\n" + Normalize(body);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── Server payload shapes (GET /terms-content/active) ──

    private sealed class TermsPayload
    {
        public List<ServerTerm>? Items { get; set; }
    }

    private sealed class ServerTerm
    {
        public string Id { get; set; } = string.Empty;
        public string Heading { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string TermsVersion { get; set; } = "1.0";
        public string? FeatureId { get; set; }
        public int SortOrder { get; set; }
    }
}
