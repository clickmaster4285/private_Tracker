using System.Security.Cryptography;
using System.Text;

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
    private static readonly TimeSpan FastPinWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FastPinInterval = TimeSpan.FromSeconds(1.5);

    private readonly DlpConfig _cfg;
    private readonly WebBlockState _webBlock;
    private readonly PolicyStore _store;
    private readonly ILogger<DnsBlockPinWorker> _logger;
    private readonly HashSet<string> _seenHosts = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastLog = DateTimeOffset.MinValue;
    private bool _clearedWhileEmpty;
    private DateTimeOffset _fastUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTcpKill = DateTimeOffset.MinValue;
    private string _lastCacheHash = "\0";

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
            "DnsBlockPinWorker running ({Mode}; idle={Idle}s tcpKill={Tcp}s)",
            OperatingSystem.IsLinux() ? "resolve→nft pin" : "DNS-cache → firewall IP pin",
            _cfg.DnsPinSec, _cfg.TcpKillSec);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _cfg.RefreshLocalSwitches();
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
                var delay = DateTimeOffset.UtcNow < _fastUntil
                    ? FastPinInterval
                    : TimeSpan.FromSeconds(Math.Max(1, _cfg.DnsPinSec));
                await Task.Delay(delay, stoppingToken);
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
            _lastCacheHash = "\0";
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

            var cacheHash = HashHostNames(cacheNames);
            matchedHosts = new List<string>();
            foreach (var host in cacheNames)
            {
                if (!DomainBlockEnforcer.HostMatchesBlockedDomains(host, domains))
                    continue;
                matchedHosts.Add(host);
            }

            if (matchedHosts.Count == 0)
            {
                _lastCacheHash = cacheHash;
                return;
            }

            // Unchanged DNS-cache name set and no prior pin activity this generation → skip DoH/pin.
            if (cacheHash == _lastCacheHash && DateTimeOffset.UtcNow >= _fastUntil)
            {
                // Still throttle TCP kill for long-lived sockets while matches exist.
                await MaybeThrottledTcpKillAsync(matchedHosts, generation, ct);
                return;
            }
            _lastCacheHash = cacheHash;
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
        var now = DateTimeOffset.UtcNow;
        if (newlyPinned > 0)
        {
            killed = TcpConnectionKiller.KillToRemoteIps(ips);
            _lastTcpKill = now;
            _fastUntil = now.Add(FastPinWindow);
        }
        else if (matchedHosts.Count > 0 &&
                 now - _lastTcpKill >= TimeSpan.FromSeconds(Math.Max(5, _cfg.TcpKillSec)))
        {
            killed = TcpConnectionKiller.KillToRemoteIps(ips);
            _lastTcpKill = now;
        }

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

    private async Task MaybeThrottledTcpKillAsync(
        List<string> matchedHosts, long generation, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastTcpKill < TimeSpan.FromSeconds(Math.Max(5, _cfg.TcpKillSec)))
            return;

        var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in matchedHosts)
        {
            foreach (var ip in DomainBlockEnforcer.ResolvePublicIps(host, forceRefresh: false))
                ips.Add(ip);
        }
        if (ips.Count == 0)
            return;

        var killed = TcpConnectionKiller.KillToRemoteIps(ips);
        _lastTcpKill = now;
        if (killed > 0)
        {
            await _store.AuditAsync(
                "web_dns_pin",
                $"gen={generation} hosts={matchedHosts.Count} pin_new=0 ips={ips.Count} tcp_kill={killed} throttle=1",
                ct);
        }
    }

    private static string HashHostNames(IReadOnlyList<string> names)
    {
        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var n in sorted)
            sb.Append(n).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
