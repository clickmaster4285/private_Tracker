namespace AlphaAITracker.Service;

/// <summary>
/// While web-block domains are active, poll the Windows DNS client cache for hostnames
/// under those apexes (e.g. rr*---sn-*.googlevideo.com), resolve their real IPs via DoH,
/// pin them into <c>AlphaAI-DLP-PIN-*</c> firewall rules, and TCP-kill matching sockets.
///
/// Why: hosts cannot wildcard CDN subdomains; apex firewall IPs miss rotating video nodes.
/// This path needs no PC/browser restart — employees keep working; new media lookups die.
/// Pins are NOT stored in dlp.db — only in memory + Windows Firewall; clear by PIN- prefix.
/// </summary>
public sealed class DnsBlockPinWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly WebBlockState _webBlock;
    private readonly PolicyStore _store;
    private readonly ILogger<DnsBlockPinWorker> _logger;
    private readonly HashSet<string> _seenHosts = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastLog = DateTimeOffset.MinValue;
    private bool _clearedWhileEmpty;

    public DnsBlockPinWorker(
        DlpConfig cfg,
        WebBlockState webBlock,
        PolicyStore store,
        ILogger<DnsBlockPinWorker> logger)
    {
        _cfg = cfg;
        _webBlock = webBlock;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.Enabled || !_cfg.WebBlock)
        {
            _logger.LogInformation(
                "DnsBlockPinWorker parked (enabled={Enabled}, webBlock={Web})",
                _cfg.Enabled, _cfg.WebBlock);
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        _logger.LogInformation(
            "DnsBlockPinWorker running ({Mode})",
            OperatingSystem.IsLinux() ? "resolve→nft pin" : "DNS-cache → firewall IP pin");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DnsBlockPinWorker tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var (_, domains, _, generation) = _webBlock.Snapshot();
        if (domains.Count == 0)
        {
            if (_seenHosts.Count > 0)
                _seenHosts.Clear();
            // EnforceWorker Apply clears PINs; this is a belt-and-suspenders heal if
            // WebBlockState is empty but orphan PIN-* rules remain (e.g. crash mid-clear).
            if (!_clearedWhileEmpty)
            {
                var clear = DomainBlockEnforcer.ClearPinnedIps();
                _clearedWhileEmpty = true;
                if (clear.Changed)
                {
                    await _store.AuditAsync("web_dns_pin_clear", clear.Detail, ct);
                    _logger.LogInformation("DNS pin cleared while domains empty ({Detail})", clear.Detail);
                }
            }
            return;
        }

        _clearedWhileEmpty = false;

        List<string> matchedHosts;
        if (OperatingSystem.IsLinux())
        {
            // No Windows DNS-client cache API — re-resolve published apexes (+ www)
            // so rotating CDN A/AAAA still land in the nft set.
            matchedHosts = new List<string>();
            foreach (var d in domains)
            {
                matchedHosts.Add(d);
                if (!d.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                    matchedHosts.Add("www." + d);
            }
        }
        else
        {
            var cacheNames = DnsCacheReader.ListHostNames();
            if (cacheNames.Count == 0)
                return;

            matchedHosts = new List<string>();
            foreach (var host in cacheNames)
            {
                if (!DomainBlockEnforcer.HostMatchesBlockedDomains(host, domains))
                    continue;
                matchedHosts.Add(host);
            }

            if (matchedHosts.Count == 0)
                return;
        }

        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in matchedHosts)
        {
            foreach (var ip in DomainBlockEnforcer.ResolvePublicIps(host, forceRefresh: false))
                ips.Add(ip);
            _seenHosts.Add(host);
        }

        if (ips.Count == 0)
            return;

        // Stale generation → Apply already cleared PINs for a newer policy; drop this batch.
        var newlyPinned = DomainBlockEnforcer.PinRemoteIps(ips, generation);
        var killed = 0;
        if (newlyPinned > 0 || matchedHosts.Count > 0)
            killed = TcpConnectionKiller.KillToRemoteIps(ips);

        if (newlyPinned > 0 || killed > 0)
        {
            await _store.AuditAsync(
                "web_dns_pin",
                $"gen={generation} hosts={matchedHosts.Count} pin_new={newlyPinned} ips={ips.Count} tcp_kill={killed}",
                ct);

            if (DateTimeOffset.UtcNow - _lastLog > TimeSpan.FromSeconds(20))
            {
                _lastLog = DateTimeOffset.UtcNow;
                _logger.LogInformation(
                    "DNS pin: gen={Gen} matched={Matched} newIps={New} totalPinnedBatch={Ips} tcpKill={Kill} sample={Sample}",
                    generation, matchedHosts.Count, newlyPinned, ips.Count, killed,
                    string.Join(",", matchedHosts.Take(3)));
            }
        }
    }
}
