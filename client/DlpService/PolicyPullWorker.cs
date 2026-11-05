using System.Net.Http.Headers;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Pulls versioned DLP policy bundles (GET /api/v1/dlp/policy) on a poll interval.
/// Parks when ALPHA_DLP_ENABLED=false. Waits (does not exit) until ServerUrl/DeviceToken exist.
/// </summary>
public sealed class PolicyPullWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PolicyPullWorker> _logger;
    private bool _loggedWaiting;

    public PolicyPullWorker(
        DlpConfig cfg,
        PolicyStore store,
        IHttpClientFactory httpFactory,
        ILogger<PolicyPullWorker> logger)
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
            _logger.LogInformation("PolicyPullWorker parked (ALPHA_DLP_ENABLED=false)");
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        var interval = TimeSpan.FromSeconds(Math.Max(30, _cfg.PolicyPollSec));
        var waitInterval = TimeSpan.FromSeconds(15);

        while (!stoppingToken.IsCancellationRequested)
        {
            _cfg.RefreshCredentials();
            if (string.IsNullOrWhiteSpace(_cfg.ServerUrl) || string.IsNullOrWhiteSpace(_cfg.DeviceToken))
            {
                if (!_loggedWaiting)
                {
                    _logger.LogWarning(
                        "PolicyPullWorker waiting for credentials " +
                        "(login Alpha AI Tracker, or set ALPHA_SERVER_URL / ALPHA_DEVICE_TOKEN in service.env)");
                    _loggedWaiting = true;
                }
                try { await Task.Delay(waitInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (_loggedWaiting)
            {
                _logger.LogInformation("PolicyPullWorker credentials ready — starting policy pull");
                _loggedWaiting = false;
            }

            try
            {
                await PullOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DLP policy pull failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PullOnceAsync(CancellationToken ct)
    {
        var (version, _, _) = await _store.GetAppliedAsync(ct);
        var client = _httpFactory.CreateClient("dlp");
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_cfg.ServerUrl}/api/v1/dlp/policy?sinceVersion={version}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Device", _cfg.DeviceToken);
        req.Headers.TryAddWithoutValidation("X-Client-Platform", "windows");

        using var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 401)
            {
                _logger.LogWarning(
                    "DLP policy pull HTTP 401 — device token invalid/revoked. Re-login the tracker.");
            }
            else
            {
                _logger.LogWarning("DLP policy pull HTTP {Status}: {Body}", (int)resp.StatusCode, body);
            }
            return;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("unchanged", out var u) && u.ValueKind == JsonValueKind.True)
        {
            _logger.LogDebug("Policy unchanged (version {Version})", version);
            return;
        }

        var newVersion = root.TryGetProperty("policyVersion", out var pv) ? pv.GetInt64() : 0;
        if (newVersion <= version)
        {
            _logger.LogDebug("Policy unchanged (version {Version})", version);
            return;
        }

        var hash = root.TryGetProperty("hash", out var bh) ? bh.GetString() ?? "" : "";
        string bundle;
        if (root.TryGetProperty("bundle", out var b))
        {
            bundle = b.ValueKind == JsonValueKind.String
                ? (b.GetString() ?? "{}")
                : b.GetRawText();
        }
        else
        {
            bundle = "{}";
        }
        await _store.SavePolicyAsync(newVersion, hash, bundle, ct);
    }
}
