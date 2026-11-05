using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Drains unsynced local DLP alerts to POST /api/v1/dlp/alerts/sync.
/// Waits for credentials instead of parking forever (late tracker login).
/// </summary>
public sealed class AlertSyncWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AlertSyncWorker> _logger;
    private bool _loggedWaiting;

    public AlertSyncWorker(
        DlpConfig cfg,
        PolicyStore store,
        IHttpClientFactory httpFactory,
        ILogger<AlertSyncWorker> logger)
    {
        _cfg = cfg;
        _store = store;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.Enabled)
        {
            _logger.LogInformation("AlertSyncWorker parked (ALPHA_DLP_ENABLED=false)");
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        var interval = TimeSpan.FromSeconds(Math.Max(15, _cfg.AlertSyncSec));
        var waitInterval = TimeSpan.FromSeconds(15);

        while (!stoppingToken.IsCancellationRequested)
        {
            _cfg.RefreshCredentials();
            if (string.IsNullOrWhiteSpace(_cfg.ServerUrl) || string.IsNullOrWhiteSpace(_cfg.DeviceToken))
            {
                if (!_loggedWaiting)
                {
                    _logger.LogWarning(
                        "AlertSyncWorker waiting for credentials " +
                        "(login Alpha AI Tracker so device.auth is written)");
                    _loggedWaiting = true;
                }
                try { await Task.Delay(waitInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (_loggedWaiting)
            {
                _logger.LogInformation("AlertSyncWorker credentials ready — draining alerts");
                _loggedWaiting = false;
            }

            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DLP alert sync failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        // Oldest first so a long outage still drains, plus a few newest so a huge
        // backlog cannot starve alerts that just happened. Nothing is deleted to make room.
        var oldest = await _store.GetUnsyncedAlertsAsync(80, ct);
        var newest = await _store.GetUnsyncedAlertsAsync(20, ct, newestFirst: true);
        var batch = new List<LocalAlert>(oldest.Count + newest.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alert in oldest)
        {
            if (seen.Add(alert.Id)) batch.Add(alert);
        }
        foreach (var alert in newest)
        {
            if (seen.Add(alert.Id)) batch.Add(alert);
        }
        if (batch.Count == 0) return;

        // Evidence must stay map[string]string on the wire (no nested arrays/objects).
        // occurredAt must be RFC3339 with Z so Go time.Time binds (avoid 400).
        var payload = new
        {
            alerts = batch.Select(a => new Dictionary<string, object?>
            {
                ["id"] = a.Id,
                ["ruleId"] = string.IsNullOrWhiteSpace(a.RuleId) ? null : a.RuleId,
                ["policyVersion"] = a.PolicyVersion,
                ["type"] = a.Type,
                ["severity"] = a.Severity,
                ["actionTaken"] = a.ActionTaken,
                ["occurredAt"] = a.OccurredAt.UtcDateTime.ToString("o"),
                ["evidence"] = FlattenEvidenceForWire(a.Evidence),
            }).ToArray(),
        };

        var json = JsonSerializer.Serialize(payload);
        var client = _httpFactory.CreateClient("dlp");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.ServerUrl}/api/v1/dlp/alerts/sync")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Device", _cfg.DeviceToken);
        req.Headers.TryAddWithoutValidation("X-Client-Platform", "windows");

        using var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 401)
            {
                _logger.LogWarning(
                    "DLP alert sync HTTP 401 — device token invalid/revoked. Re-login the tracker or set ALPHA_DEVICE_TOKEN.");
            }
            else
            {
                _logger.LogWarning("DLP alert sync HTTP {Status}: {Body}", (int)resp.StatusCode, body);
            }
            return;
        }

        // Present acceptedIds is authoritative, including []. Missing/unreadable leaves every row.
        if (!AlertAcceptDecision.TrySelectIds(body, out var accepted))
        {
            _logger.LogWarning(
                "DLP alert sync response has no acceptedIds; leaving {Count} alerts unsynced",
                batch.Count);
            return;
        }

        await _store.MarkAlertsSyncedAsync(accepted, ct);
        var kept = batch.Count - accepted.Count;
        _logger.LogInformation(
            "Synced {Count} DLP alerts; {Kept} in this batch stay local",
            accepted.Count, Math.Max(0, kept));
    }

    /// <summary>
    /// Wire evidence is always string→string. If a value looks like a JSON array/object
    /// (legacy queue), keep it as a compact string so the server can store it.
    /// </summary>
    private static Dictionary<string, string> FlattenEvidenceForWire(
        Dictionary<string, string>? evidence)
    {
        if (evidence == null || evidence.Count == 0)
            return new Dictionary<string, string>();
        var outMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in evidence)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            outMap[k] = v ?? "";
        }
        return outMap;
    }
}
