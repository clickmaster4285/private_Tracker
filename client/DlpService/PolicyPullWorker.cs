using System.Net.Http.Headers;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Pulls versioned DLP policy bundles (GET /api/v1/dlp/policy) on a poll interval.
/// Parks when ALPHA_DLP_ENABLED=false. Waits (does not exit) until ServerUrl/DeviceToken exist.
/// When credentials appear mid-run (tracker login) and <c>ALPHA_DLP_LOGIN_REBOOT</c> is on:
/// pre-kill → pull → force-apply blockers → one-shot reboot (no loop after restart).
/// </summary>
public sealed class PolicyPullWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly EnforceWorker _enforce;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PolicyPullWorker> _logger;
    private bool _loggedWaiting;
    /// <summary>Set when credentials appear mid-run; cleared after reboot is queued.</summary>
    private bool _pendingLoginReboot;

    public PolicyPullWorker(
        DlpConfig cfg,
        PolicyStore store,
        EnforceWorker enforce,
        IHttpClientFactory httpFactory,
        ILogger<PolicyPullWorker> logger)
    {
        _cfg = cfg;
        _store = store;
        _enforce = enforce;
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
        // Lab may set ALPHA_DLP_POLICY_POLL_SEC=1; ship default is 300. Floor is 1s.
        // The interval is re-read every pass (RefreshLocalSwitches updates it from
        // service.env), and a server push (pipe op RequestPolicyPull) ends the wait early.
        var waitInterval = TimeSpan.FromSeconds(15);
        // While a login reboot is pending, re-try hardening quickly instead of the long poll.
        var pendingInterval = TimeSpan.FromSeconds(10);

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
                _logger.LogInformation(
                    "PolicyPullWorker credentials ready — flush DNS/CDN pins then pull allowlists");
                _loggedWaiting = false;
                // Post-login fail-closed prep: drop cached DNS and CDN pin rules so a stale
                // allow path cannot linger while the first lockdown policy is applied.
                try
                {
                    await Task.Run(() =>
                    {
                        DomainBlockEnforcer.ClearPinnedIps();
                        DomainBlockEnforcer.FlushDns();
                    }, stoppingToken);
                }
                catch (Exception flushEx)
                {
                    _logger.LogWarning(flushEx, "Post-login DNS/pin flush failed (continuing pull)");
                }

                // Only when creds appeared mid-run (not already present at service boot).
                if (_cfg.LoginReboot && OperatingSystem.IsWindows())
                {
                    _pendingLoginReboot = true;
                    _logger.LogWarning(
                        "Post-login hardening armed (ALPHA_DLP_LOGIN_REBOOT=true) — " +
                        "kill → sync allowset → apply blockers → reboot");
                }
            }

            try
            {
                if (_pendingLoginReboot)
                    await PreKillOutsideBootstrapAsync(stoppingToken);

                await PullOnceAsync(stoppingToken);

                if (_pendingLoginReboot)
                    await TryFinishLoginRebootAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DLP policy pull failed");
            }

            var delay = _pendingLoginReboot
                ? pendingInterval
                : TimeSpan.FromSeconds(Math.Max(1, _cfg.PolicyPollSec));
            try
            {
                if (await DlpSignals.WaitPullAsync(delay, stoppingToken))
                    _logger.LogInformation("Policy pull requested (server push) — pulling now");
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Kill running EXEs outside Bootstrap A before the allowset arrives (fail-closed).
    /// Explorer is killable under lockdown; critical OS names are not (see <see cref="AppProcessGuard"/>).
    /// </summary>
    private async Task PreKillOutsideBootstrapAsync(CancellationToken ct)
    {
        try
        {
            var sweep = await Task.Run(
                () => AppProcessGuard.Sweep(Array.Empty<AllowAppEntry>()),
                ct);
            await _store.AuditAsync("login_pre_kill", sweep.Detail, ct);
            if (sweep.Killed > 0)
            {
                _logger.LogWarning(
                    "Post-login pre-kill: {Count} process(es) — {Detail}",
                    sweep.Killed, sweep.Detail);
            }
            else
            {
                _logger.LogInformation("Post-login pre-kill: {Detail}", sweep.Detail);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-login pre-kill failed (continuing)");
        }
    }

    private async Task TryFinishLoginRebootAsync(CancellationToken ct)
    {
        if (UpdatePaths.IsUpdateInProgress() || UpdatePaths.IsUpdatePriorityActive())
        {
            _logger.LogWarning("Post-login reboot deferred — silent update in progress");
            return;
        }

        try
        {
            await _enforce.ForceApplyOnceAsync(ct);
            await _store.AuditAsync("login_force_apply", "ok", ct);
            _logger.LogInformation("Post-login force-apply of DLP blockers completed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-login force-apply failed — will retry before reboot");
            await _store.AuditAsync("login_force_apply_failed", ex.GetType().Name, ct);
            return;
        }

        var reboot = LoginReboot.TrySchedule(delaySeconds: _cfg.LoginRebootDelaySec);
        await _store.AuditAsync(
            reboot.Ok ? "login_reboot_queued" : "login_reboot_failed",
            reboot.Detail,
            ct);
        if (reboot.Ok)
        {
            _pendingLoginReboot = false;
            _logger.LogWarning(
                "Post-login reboot queued ({Detail}). PC will restart; " +
                "after boot blockers stay from policy (no second reboot).",
                reboot.Detail);
        }
        else
        {
            _logger.LogWarning("Post-login reboot failed ({Detail}) — will retry", reboot.Detail);
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

        // lockdownPausedUntil rides every successful response (also `unchanged`). Absent / null /
        // unparseable = no pause. Stored separately from the bundle so EnforceWorker re-reads it each tick.
        DateTimeOffset? pausedUntil = null;
        if (root.TryGetProperty("lockdownPausedUntil", out var lp) &&
            lp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                lp.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsedPause))
        {
            pausedUntil = parsedPause;
        }
        await _store.SetLockdownPausedUntilAsync(pausedUntil, ct);

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
        // Apply right away instead of waiting out EnforceWorker's 15 s tick.
        DlpSignals.RequestEnforce();

        // Heal / sync service.env (incl. ALPHA_DLP_PORT_BLOCK) and refresh live switches
        // so EnforceWorker picks up PortBlock without a service restart.
        try
        {
            var synced = EnvBootstrap.SyncServiceEnvAfterPolicyApply();
            _cfg.RefreshLocalSwitches();
            if (synced.Count > 0)
            {
                _logger.LogInformation(
                    "Synced service.env after policy v{Version}: {Keys}; portBlock={Port}",
                    newVersion, string.Join(", ", synced), _cfg.PortBlock);
            }
            else
            {
                _logger.LogInformation(
                    "service.env reload after policy v{Version}; portBlock={Port}",
                    newVersion, _cfg.PortBlock);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "service.env sync after policy v{Version} failed", newVersion);
        }
    }
}
