using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AlphaAITracker.Service;

/// <summary>
/// P2 web domain block — production path:
/// 1) hosts DNS sink (A+AAAA → loopback) with <c># AlphaAI-DLP</c> markers
/// 2) Windows: outbound firewall <c>AlphaAI-DLP-*</c> + Chrome/Edge policies + DNS flush;
///    Linux: nftables inet drop-set for resolved CDN/apex IPs (weaker without DNS-cache API)
/// Requires LocalSystem / root.
/// </summary>
internal static class DomainBlockEnforcer
{
    public const string MarkerPrefix = "# AlphaAI-DLP";
    public const string FirewallRulePrefix = "AlphaAI-DLP-";
    private const string BeginMarker = "# AlphaAI-DLP-BEGIN";
    private const string EndMarker = "# AlphaAI-DLP-END";
    private const string ChromePolicyKey = @"SOFTWARE\Policies\Google\Chrome";
    private const string EdgePolicyKey = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string ChromeUrlBlockKey = ChromePolicyKey + @"\URLBlocklist";
    private const string EdgeUrlBlockKey = EdgePolicyKey + @"\URLBlocklist";
    private const string ChromeUrlAllowKey = ChromePolicyKey + @"\URLAllowlist";
    private const string EdgeUrlAllowKey = EdgePolicyKey + @"\URLAllowlist";

    /// <summary>How long resolved A/AAAA stay cached before a background refresh.</summary>
    private static readonly TimeSpan IpCacheTtl = TimeSpan.FromMinutes(20);

    private static readonly HttpClient DnsHttp = CreateDnsClient();
    private static readonly ConcurrentDictionary<string, IpCacheEntry> IpCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last applied domain→IP fingerprint per firewall rule (incremental diff).</summary>
    private static readonly object FwStateGate = new();
    private static Dictionary<string, string> _lastFwFingerprints =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Live CDN IPs discovered via DNS cache (rr*---sn-*.googlevideo.com, …).
    /// Separate from apex resolve rules — hosts cannot wildcard these names.
    /// Rule names are <c>AlphaAI-DLP-PIN-0</c>… (IP chunks), not per-site — clear by prefix.
    /// </summary>
    private const string PinnedRulePrefix = FirewallRulePrefix + "PIN-";
    private static readonly ConcurrentDictionary<string, byte> PinnedIps =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxIpsPerPinnedRule = 40;

    /// <summary>
    /// Serializes Apply pin-clear vs DnsBlockPinWorker writes so Inactive cannot race
    /// with PIN recreation. Bumped at the start of every <see cref="Apply"/>.
    /// </summary>
    private static readonly object PinGate = new();
    private static long _policyGeneration;
    private static List<string>? _ownedRuleCache;
    private static DateTimeOffset _ownedRuleCacheAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan OwnedRuleCacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>Current apply generation — pin worker must pass this into <see cref="PinRemoteIps"/>.</summary>
    public static long CurrentGeneration => Interlocked.Read(ref _policyGeneration);

    private sealed record IpCacheEntry(IReadOnlyList<string> Ips, DateTimeOffset ExpiresUtc);

    public sealed record Result(
        bool Changed,
        int DesiredCount,
        int AppliedCount,
        string Detail);

    public static string HostsPath
    {
        get
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return "/etc/hosts";
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return Path.Combine(windir, "System32", "drivers", "etc", "hosts");
        }
    }

    /// <param name="domains">Explicit deny-list hosts (web_navigate / shadowDeny).</param>
    /// <param name="lockdownAllowHosts">
    /// When non-null, lockdown is on: Chrome/Edge get <c>URLBlocklist=*</c> plus deny patterns
    /// and <c>URLAllowlist</c> for these hosts (already minus explicit blocks). Null clears
    /// URLAllowlist and keeps today's deny-list-only browser policy.
    /// </param>
    public static Result Apply(IReadOnlyList<string> domains, IReadOnlyList<string>? lockdownAllowHosts = null)
    {
        if (OperatingSystem.IsLinux())
            return ApplyLinux(domains);
        if (!OperatingSystem.IsWindows())
            return new Result(false, domains.Count, 0, "unsupported_os");

        var desired = domains
            .SelectMany(PolicyBundleParser.NormalizeDomain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Bump generation under PinGate FIRST so in-flight PinRemoteIps(oldGen) no-ops,
        // then wipe PIN-* (same as manual netsh delete of AlphaAI-DLP-PIN-N) + apex FW.
        Result pinClear;
        Result fwResult;
        lock (PinGate)
        {
            Interlocked.Increment(ref _policyGeneration);
            pinClear = ClearPinnedIpsUnlocked();
            if (pinClear.Detail.StartsWith("pin_clear_failed", StringComparison.Ordinal) ||
                pinClear.Detail.StartsWith("access_denied", StringComparison.Ordinal))
            {
                return new Result(false, desired.Count, 0, pinClear.Detail);
            }

            fwResult = ApplyFirewallIncremental(desired, forceIpRefresh: true);
            InvalidateOwnedRuleCache();
        }

        if (fwResult.Detail.StartsWith("access_denied", StringComparison.Ordinal) ||
            fwResult.Detail.StartsWith("fw_clear_incomplete", StringComparison.Ordinal) ||
            fwResult.Detail.StartsWith("fw_error", StringComparison.Ordinal))
        {
            return new Result(false, desired.Count, 0, fwResult.Detail);
        }

        var browserResult = ApplyBrowserPolicies(desired, lockdownAllowHosts);
        var tcpKilled = 0;
        if (desired.Count > 0 && !fwResult.Detail.StartsWith("access_denied", StringComparison.Ordinal))
            tcpKilled = KillEstablishedTcp(desired);

        var hostsResult = ApplyHosts(desired);
        FlushDns();

        var changed = hostsResult.Changed || fwResult.Changed || browserResult.Changed
                      || pinClear.Changed || tcpKilled > 0;
        var detail =
            $"gen={CurrentGeneration}; pin={pinClear.Detail}; browser={browserResult.Detail}; " +
            $"hosts={hostsResult.Detail}; fw={fwResult.Detail}; tcp_kill={tcpKilled}";
        if (hostsResult.Detail.StartsWith("access_denied", StringComparison.Ordinal) ||
            browserResult.Detail.StartsWith("access_denied", StringComparison.Ordinal))
            detail = "access_denied_need_admin";
        else if (browserResult.Detail.StartsWith("browser_error", StringComparison.Ordinal))
            detail = browserResult.Detail;

        return new Result(changed, desired.Count, desired.Count, detail);
    }

    private static Result ApplyLinux(IReadOnlyList<string> domains)
    {
        var desired = domains
            .SelectMany(PolicyBundleParser.NormalizeDomain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (PinGate)
        {
            Interlocked.Increment(ref _policyGeneration);
            PinnedIps.Clear();
        }

        var hostsResult = ApplyHosts(desired);
        if (hostsResult.Detail.StartsWith("access_denied", StringComparison.Ordinal))
            return new Result(false, desired.Count, 0, "access_denied_need_admin");

        LinuxNftEnforcer.Result nft;
        if (desired.Count == 0)
        {
            nft = LinuxNftEnforcer.Clear();
        }
        else
        {
            var v4 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var v6 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in desired)
            {
                foreach (var ip in ResolvePublicIps(d, forceRefresh: true))
                {
                    if (IPAddress.TryParse(ip, out var addr))
                    {
                        if (addr.AddressFamily == AddressFamily.InterNetwork) v4.Add(ip);
                        else if (addr.AddressFamily == AddressFamily.InterNetworkV6) v6.Add(ip);
                    }
                }
            }
            nft = LinuxNftEnforcer.EnsureTableAndApply(v4, v6);
        }

        FlushDns();
        var changed = hostsResult.Changed || nft.Changed;
        var detail = $"gen={CurrentGeneration}; hosts={hostsResult.Detail}; nft={nft.Detail}";
        return new Result(changed, desired.Count, desired.Count, detail);
    }

    /// <summary>Teardown clear: web-block artifacts AND any AlphaAI-DLP-LOCK-* lockdown rules.</summary>
    public static Result Clear()
    {
        var lockClear = LockdownNetworkEnforcer.Clear();
        var web = Apply(Array.Empty<string>());
        return new Result(
            web.Changed || lockClear.Changed,
            web.DesiredCount,
            web.AppliedCount,
            $"{web.Detail}; lockdown={lockClear.Detail}");
    }

    /// <summary>
    /// True when hosts / owned firewall / Chrome·Edge URL policies still show AlphaAI web-block
    /// artifacts after policy wants zero domains. Used by EnforceWorker to self-heal stale blocks.
    /// </summary>
    /// <param name="ignoreBrowserPolicies">
    /// When true (lockdown on), skip Chrome/Edge registry probes — lockdown intentionally leaves
    /// URLBlocklist=* and URLAllowlist in place even with an empty deny-list domain set.
    /// </param>
    public static bool HasLeftoverOsArtifacts(bool ignoreBrowserPolicies = false)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return false;

        try
        {
            if (File.Exists(HostsPath))
            {
                var content = File.ReadAllText(HostsPath);
                if (content.Contains(BeginMarker, StringComparison.OrdinalIgnoreCase) ||
                    content.Contains(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            /* unreadable hosts — treat as no leftover for this probe */
        }

        if (OperatingSystem.IsLinux())
            return LinuxNftEnforcer.HasOwnedArtifacts();

        try
        {
            if (ListOwnedFirewallRules(bypassCache: true).Count > 0)
                return true;
        }
        catch
        {
            /* netsh failed */
        }

        if (ignoreBrowserPolicies)
            return false;

        try
        {
            using (var chrome = Registry.LocalMachine.OpenSubKey(ChromeUrlBlockKey))
            {
                if (chrome is not null) return true;
            }
            using (var edge = Registry.LocalMachine.OpenSubKey(EdgeUrlBlockKey))
            {
                if (edge is not null) return true;
            }
            using (var chromeAllow = Registry.LocalMachine.OpenSubKey(ChromeUrlAllowKey))
            {
                if (chromeAllow is not null) return true;
            }
            using var edgeAllow = Registry.LocalMachine.OpenSubKey(EdgeUrlAllowKey);
            if (edgeAllow is not null) return true;
        }
        catch
        {
            /* registry probe failed */
        }

        return false;
    }

    /// <summary>
    /// Reconcile check: OS hosts + apex FW match <paramref name="domains"/>.
    /// Empty desired ⇒ no AlphaAI artifacts (including PIN-*). Non-empty ⇒ hosts set equal
    /// and no orphan apex rules (PIN-* allowed — pin worker re-learns under current gen).
    /// When <paramref name="lockdownAllowHosts"/> is non-null, browser policy leftovers are
    /// expected (URLAllowlist / URLBlocklist=*) and are not treated as drift.
    /// </summary>
    public static bool OsStateMatchesDesired(
        IReadOnlyList<string> domains,
        IReadOnlyList<string>? lockdownAllowHosts = null)
    {
        var lockdownOn = lockdownAllowHosts is not null;
        if (OperatingSystem.IsLinux())
        {
            var desiredLinux = domains
                .SelectMany(PolicyBundleParser.NormalizeDomain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (desiredLinux.Count == 0)
                return !HasLeftoverOsArtifacts(ignoreBrowserPolicies: lockdownOn);
            return ReadHostsBlockedDomains().SetEquals(desiredLinux);
        }

        if (!OperatingSystem.IsWindows())
            return true;

        var desired = domains
            .SelectMany(PolicyBundleParser.NormalizeDomain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (desired.Count == 0)
            return !HasLeftoverOsArtifacts(ignoreBrowserPolicies: lockdownOn);

        var hostsDomains = ReadHostsBlockedDomains();
        if (!hostsDomains.SetEquals(desired))
            return false;

        var desiredRuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in SelectResolveTargets(desired.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()))
            desiredRuleNames.Add(FirewallRulePrefix + SanitizeRuleName(d));

        foreach (var name in ListOwnedFirewallRules(bypassCache: true))
        {
            if (name.StartsWith(PinnedRulePrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!desiredRuleNames.Contains(name))
                return false;
        }

        return true;
    }

    /// <summary>Domains listed inside the AlphaAI hosts block (empty if none).</summary>
    public static HashSet<string> ReadHostsBlockedDomains()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(HostsPath))
                return set;
            var content = File.ReadAllText(HostsPath);
            var inBlock = false;
            foreach (var rawLine in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var t = rawLine.Trim();
                if (t.Equals(BeginMarker, StringComparison.OrdinalIgnoreCase))
                {
                    inBlock = true;
                    continue;
                }
                if (t.Equals(EndMarker, StringComparison.OrdinalIgnoreCase))
                {
                    inBlock = false;
                    continue;
                }
                if (!inBlock && !t.Contains(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                // "127.0.0.1 host # AlphaAI-DLP" or "::1 host # AlphaAI-DLP"
                var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;
                if (parts[0] is not ("127.0.0.1" or "::1" or "0.0.0.0" or "::"))
                    continue;
                var host = parts[1].Trim().TrimEnd('.');
                if (host.Length > 0 && host[0] != '#')
                    set.Add(host.ToLowerInvariant());
            }
        }
        catch
        {
            /* unreadable */
        }
        return set;
    }

    /// <summary>
    /// While the domain set is unchanged, periodically refresh CDN IPs and patch firewall
    /// rules so rotating googlevideo addresses stay blocked (no full re-apply).
    /// </summary>
    public static Result RefreshStaleFirewallIps(IReadOnlyList<string> domains)
    {
        if (domains.Count == 0)
            return new Result(false, 0, 0, "skip");

        if (OperatingSystem.IsLinux())
        {
            var desired = domains
                .SelectMany(PolicyBundleParser.NormalizeDomain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var v4 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var v6 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in desired)
            {
                foreach (var ip in ResolvePublicIps(d, forceRefresh: false))
                {
                    if (IPAddress.TryParse(ip, out var addr))
                    {
                        if (addr.AddressFamily == AddressFamily.InterNetwork) v4.Add(ip);
                        else if (addr.AddressFamily == AddressFamily.InterNetworkV6) v6.Add(ip);
                    }
                }
            }
            var nft = LinuxNftEnforcer.EnsureTableAndApply(v4, v6);
            return new Result(nft.Changed, desired.Count, desired.Count, nft.Detail);
        }

        if (!OperatingSystem.IsWindows())
            return new Result(false, domains.Count, 0, "skip");

        var winDesired = domains
            .SelectMany(PolicyBundleParser.NormalizeDomain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Result fw;
        lock (PinGate)
        {
            fw = ApplyFirewallIncremental(winDesired, forceIpRefresh: false, onlyIfStale: true);
            InvalidateOwnedRuleCache();
        }
        if (!fw.Changed)
            return fw;
        var killed = KillEstablishedTcp(winDesired);
        return new Result(true, winDesired.Count, fw.AppliedCount, $"{fw.Detail}; tcp_kill={killed}");
    }

    /// <summary>
    /// True when <paramref name="host"/> equals a blocked apex or is a subdomain of one
    /// (e.g. rr1---sn-x.googlevideo.com matches googlevideo.com).
    /// </summary>
    public static bool HostMatchesBlockedDomains(string host, IReadOnlyList<string> domains)
    {
        if (string.IsNullOrWhiteSpace(host) || domains.Count == 0)
            return false;
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var raw in domains)
        {
            var apex = NormalizeApex(raw);
            if (apex.Length == 0)
                continue;
            if (host.Equals(apex, StringComparison.OrdinalIgnoreCase))
                return true;
            if (host.EndsWith("." + apex, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Pin live CDN IPs into outbound firewall rules. No-ops when
    /// <paramref name="expectedGeneration"/> is stale (Apply already bumped gen / cleared PINs).
    /// </summary>
    public static int PinRemoteIps(IEnumerable<string> ips, long expectedGeneration)
    {
        if (OperatingSystem.IsLinux())
        {
            lock (PinGate)
            {
                if (expectedGeneration != Interlocked.Read(ref _policyGeneration))
                    return 0;
                var list = new List<string>();
                foreach (var raw in ips)
                {
                    if (!IPAddress.TryParse(raw, out var addr)) continue;
                    if (addr.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                        continue;
                    if (IPAddress.IsLoopback(addr)) continue;
                    var ip = addr.ToString();
                    if (PinnedIps.TryAdd(ip, 0))
                        list.Add(ip);
                }
                if (list.Count == 0) return 0;
                var pin = LinuxNftEnforcer.PinIps(list);
                return pin.Changed ? list.Count : 0;
            }
        }

        if (!OperatingSystem.IsWindows())
            return 0;

        lock (PinGate)
        {
            if (expectedGeneration != Interlocked.Read(ref _policyGeneration))
                return 0;

            var added = 0;
            foreach (var raw in ips)
            {
                if (!IPAddress.TryParse(raw, out var addr))
                    continue;
                if (addr.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                    continue;
                if (IPAddress.IsLoopback(addr) || addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any))
                    continue;
                var ip = addr.ToString();
                if (PinnedIps.TryAdd(ip, 0))
                    added++;
            }
            if (added == 0)
                return 0;

            RebuildPinnedFirewallRulesUnlocked();
            InvalidateOwnedRuleCache();
            return added;
        }
    }

    /// <summary>
    /// Delete every <c>AlphaAI-DLP-PIN-*</c> firewall rule (by prefix — no per-site name needed)
    /// and clear in-memory pin set. Verifies leftovers; does not swallow failures.
    /// </summary>
    public static Result ClearPinnedIps()
    {
        if (OperatingSystem.IsLinux())
        {
            lock (PinGate)
            {
                PinnedIps.Clear();
                var nft = LinuxNftEnforcer.Clear();
                return new Result(nft.Changed, 0, 0, nft.Detail);
            }
        }
        if (!OperatingSystem.IsWindows())
            return new Result(false, 0, 0, "unsupported_os");
        lock (PinGate)
            return ClearPinnedIpsUnlocked();
    }

    private static Result ClearPinnedIpsUnlocked()
    {
        PinnedIps.Clear();
        try
        {
            var pins = ListOwnedFirewallRules(bypassCache: true)
                .Where(n => n.StartsWith(PinnedRulePrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var name in pins)
                RunNetsh($"advfirewall firewall delete rule name=\"{EscapeNetsh(name)}\"");

            InvalidateOwnedRuleCache();
            var still = ListOwnedFirewallRules(bypassCache: true)
                .Where(n => n.StartsWith(PinnedRulePrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (still.Count > 0)
                return new Result(false, 0, 0, "pin_clear_failed:" + string.Join(",", still));

            return new Result(pins.Count > 0, 0, 0, pins.Count > 0 ? "pin_cleared" : "pin_absent");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new Result(false, 0, 0, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, 0, 0, "pin_clear_failed:" + ex.Message);
        }
    }

    private static void RebuildPinnedFirewallRulesUnlocked()
    {
        var all = PinnedIps.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in ListOwnedFirewallRules(bypassCache: true)
                     .Where(n => n.StartsWith(PinnedRulePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{EscapeNetsh(name)}\"");
        }

        for (var i = 0; i < all.Count; i += MaxIpsPerPinnedRule)
        {
            var chunk = all.Skip(i).Take(MaxIpsPerPinnedRule).ToList();
            var ruleName = PinnedRulePrefix + (i / MaxIpsPerPinnedRule);
            var remote = string.Join(",", chunk);
            var args =
                $"advfirewall firewall add rule name=\"{EscapeNetsh(ruleName)}\" " +
                $"dir=out action=block protocol=any remoteip={remote} " +
                $"enable=yes profile=any";
            RunNetsh(args);
        }
    }

    private static string NormalizeApex(string raw)
    {
        var d = raw.Trim().ToLowerInvariant();
        if (d.StartsWith("www.", StringComparison.Ordinal)) d = d[4..];
        if (d.StartsWith("m.", StringComparison.Ordinal)) d = d[2..];
        return d.TrimEnd('.');
    }

    /// <summary>
    /// Tear down live TCP sockets to blocked remotes (SetTcpEntry DELETE_TCB). Complements
    /// firewall (which mainly stops new flows / UDP-QUIC) so HTTPS mid-watch dies quickly.
    /// </summary>
    private static int KillEstablishedTcp(IReadOnlyList<string> desired)
    {
        try
        {
            var ips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var domain in SelectResolveTargets(desired))
            {
                foreach (var ip in ResolvePublicIps(domain))
                    ips.Add(ip);
            }
            return TcpConnectionKiller.KillToRemoteIps(ips);
        }
        catch
        {
            return 0;
        }
    }

    private static Result ApplyHosts(IReadOnlyList<string> desired)
    {
        string original;
        try
        {
            original = File.Exists(HostsPath)
                ? File.ReadAllText(HostsPath, Encoding.UTF8)
                : "";
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new Result(false, desired.Count, 0, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, desired.Count, 0, "error_read:" + ex.Message);
        }

        var stripped = StripOwnedBlock(original);
        var next = stripped.TrimEnd() + "\r\n";
        if (desired.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(BeginMarker);
            foreach (var d in desired)
            {
                sb.Append("127.0.0.1 ").Append(d).Append(' ').AppendLine(MarkerPrefix);
                sb.Append("::1 ").Append(d).Append(' ').AppendLine(MarkerPrefix);
            }
            sb.AppendLine(EndMarker);
            next += sb.ToString();
        }

        if (NormalizeNewlines(next) == NormalizeNewlines(original))
            return new Result(false, desired.Count, desired.Count, "already_applied");

        try
        {
            var tmp = HostsPath + ".alpha-dlp.tmp";
            File.WriteAllText(tmp, next, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Copy(tmp, HostsPath, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
            return new Result(true, desired.Count, desired.Count,
                desired.Count == 0 ? "hosts_cleared" : "hosts_updated");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new Result(false, desired.Count, 0, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, desired.Count, 0, "error_write:" + ex.Message);
        }
    }

    /// <summary>
    /// Diff-based firewall: remove obsolete AlphaAI-DLP rules, add/replace only changed domains.
    /// Avoids delete-all + recreate on every policy tick (CPU + brief unprotected window).
    /// </summary>
    private static Result ApplyFirewallIncremental(
        IReadOnlyList<string> desired,
        bool forceIpRefresh = false,
        bool onlyIfStale = false)
    {
        try
        {
            var resolveTargets = SelectResolveTargets(desired);
            if (desired.Count == 0)
            {
                var ownedWhenEmpty = ListOwnedFirewallRules(bypassCache: true);
                foreach (var name in ownedWhenEmpty)
                    RunNetsh($"advfirewall firewall delete rule name=\"{EscapeNetsh(name)}\"");
                InvalidateOwnedRuleCache();
                lock (FwStateGate)
                    _lastFwFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                IpCache.Clear();
                var leftover = ListOwnedFirewallRules(bypassCache: true);
                if (leftover.Count > 0)
                    return new Result(false, 0, 0,
                        "fw_clear_incomplete:" + string.Join(",", leftover.Take(8)));
                return new Result(ownedWhenEmpty.Count > 0, 0, 0,
                    ownedWhenEmpty.Count > 0 ? "fw_cleared" : "fw_empty");
            }

            var desiredByRule = new Dictionary<string, (string Domain, string IpCsv, string Fp)>(
                StringComparer.OrdinalIgnoreCase);
            var anyStale = false;
            foreach (var domain in resolveTargets)
            {
                if (IsStale(domain))
                    anyStale = true;
                var ips = ResolvePublicIps(domain, forceRefresh: forceIpRefresh);
                if (ips.Count == 0)
                    continue;
                var ruleName = FirewallRulePrefix + SanitizeRuleName(domain);
                var ipCsv = string.Join(",", ips.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                var fp = domain.ToLowerInvariant() + "|" + ipCsv;
                desiredByRule[ruleName] = (domain, ipCsv, fp);
            }

            if (onlyIfStale && !anyStale && !forceIpRefresh)
            {
                lock (FwStateGate)
                {
                    if (SameRuleSet(_lastFwFingerprints, desiredByRule))
                        return new Result(false, desired.Count, desiredByRule.Count, "fw_fresh");
                }
            }

            var existing = ListOwnedFirewallRules(bypassCache: true);
            var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            var removed = 0;
            var added = 0;
            var updated = 0;

            Dictionary<string, string> prevFp;
            lock (FwStateGate)
                prevFp = new Dictionary<string, string>(_lastFwFingerprints, StringComparer.OrdinalIgnoreCase);

            foreach (var name in existing)
            {
                if (desiredByRule.ContainsKey(name))
                    continue;
                RunNetsh($"advfirewall firewall delete rule name=\"{EscapeNetsh(name)}\"");
                removed++;
            }

            foreach (var (ruleName, entry) in desiredByRule)
            {
                var needsWrite = !existingSet.Contains(ruleName)
                    || !prevFp.TryGetValue(ruleName, out var oldFp)
                    || !string.Equals(oldFp, entry.Fp, StringComparison.Ordinal);
                if (!needsWrite)
                    continue;

                if (existingSet.Contains(ruleName))
                {
                    RunNetsh($"advfirewall firewall delete rule name=\"{EscapeNetsh(ruleName)}\"");
                    updated++;
                }
                else
                {
                    added++;
                }

                var args =
                    $"advfirewall firewall add rule name=\"{EscapeNetsh(ruleName)}\" " +
                    $"dir=out action=block protocol=any remoteip={entry.IpCsv} " +
                    $"enable=yes profile=any";
                RunNetsh(args);
            }

            InvalidateOwnedRuleCache();
            var nextFp = desiredByRule.ToDictionary(
                kv => kv.Key, kv => kv.Value.Fp, StringComparer.OrdinalIgnoreCase);
            lock (FwStateGate)
                _lastFwFingerprints = nextFp;

            // Orphan apex rules (not PIN) must be gone after shrink-set apply.
            var orphans = ListOwnedFirewallRules(bypassCache: true)
                .Where(n => !n.StartsWith(PinnedRulePrefix, StringComparison.OrdinalIgnoreCase)
                            && !desiredByRule.ContainsKey(n))
                .ToList();
            if (orphans.Count > 0)
                return new Result(false, desired.Count, desiredByRule.Count,
                    "fw_clear_incomplete:" + string.Join(",", orphans.Take(8)));

            var changed = removed > 0 || added > 0 || updated > 0;
            return new Result(changed, desired.Count, desiredByRule.Count,
                $"fw_add={added} upd={updated} rm={removed} rules={desiredByRule.Count}");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new Result(false, desired.Count, 0, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, desired.Count, 0, "fw_error:" + ex.Message);
        }
    }

    private static bool SameRuleSet(
        Dictionary<string, string> prev,
        Dictionary<string, (string Domain, string IpCsv, string Fp)> desired)
    {
        if (prev.Count != desired.Count)
            return false;
        foreach (var (name, entry) in desired)
        {
            if (!prev.TryGetValue(name, out var fp) ||
                !string.Equals(fp, entry.Fp, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Domains to resolve for firewall IPs. Skip www./m. twins when the apex is also
    /// in the set (same A records usually) — fewer DNS queries, same block coverage.
    /// Keep alternate apexes (googlevideo.com, youtu.be, …) — those cut mid-watch CDN.
    /// </summary>
    private static List<string> SelectResolveTargets(IReadOnlyList<string> desired)
    {
        var set = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var domain in desired)
        {
            if (domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                var apex = domain[4..];
                if (set.Contains(apex))
                    continue;
            }
            if (domain.StartsWith("m.", StringComparison.OrdinalIgnoreCase))
            {
                var apex = domain[2..];
                if (set.Contains(apex))
                    continue;
            }
            list.Add(domain);
        }
        return list;
    }

    private static bool IsStale(string domain)
    {
        if (!IpCache.TryGetValue(domain, out var e))
            return true;
        return DateTimeOffset.UtcNow >= e.ExpiresUtc;
    }

    /// <summary>
    /// Resolve A/AAAA via Cloudflare DNS-over-HTTPS JSON so we get real IPs even when
    /// local hosts already sinks the name (needed for firewall vs browser DoH).
    /// Results cached for <see cref="IpCacheTtl"/>.
    /// </summary>
    internal static IReadOnlyList<string> ResolvePublicIps(string domain, bool forceRefresh = false)
    {
        IpCache.TryGetValue(domain, out var cached);
        if (!forceRefresh &&
            cached is not null &&
            DateTimeOffset.UtcNow < cached.ExpiresUtc)
        {
            return cached.Ips;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var type in new[] { "A", "AAAA" })
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(domain)}&type={type}");
                req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
                using var resp = DnsHttp.Send(req);
                if (!resp.IsSuccessStatusCode)
                    continue;
                using var stream = resp.Content.ReadAsStream();
                using var doc = JsonDocument.Parse(stream);
                if (!doc.RootElement.TryGetProperty("Answer", out var answers) ||
                    answers.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var a in answers.EnumerateArray())
                {
                    if (!a.TryGetProperty("data", out var data))
                        continue;
                    var ip = data.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(ip))
                        continue;
                    if (IPAddress.TryParse(ip, out var addr) &&
                        addr.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 &&
                        !IPAddress.IsLoopback(addr) &&
                        !ip.Equals("0.0.0.0", StringComparison.Ordinal))
                    {
                        set.Add(addr.ToString());
                    }
                }
            }
        }
        catch
        {
            try
            {
                foreach (var addr in Dns.GetHostAddresses(domain))
                {
                    if (addr.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 &&
                        !IPAddress.IsLoopback(addr) &&
                        !addr.Equals(IPAddress.Any) &&
                        !addr.Equals(IPAddress.IPv6Any))
                        set.Add(addr.ToString());
                }
            }
            catch { /* ignore */ }
        }

        var list = set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        // Keep previous IPs if resolve failed (CDN blip) so we don't drop mid-session blocks.
        if (list.Count == 0 && cached is { Ips.Count: > 0 })
            return cached.Ips;

        IpCache[domain] = new IpCacheEntry(list, DateTimeOffset.UtcNow.Add(IpCacheTtl));
        return list;
    }

    private static void InvalidateOwnedRuleCache()
    {
        _ownedRuleCache = null;
        _ownedRuleCacheAt = DateTimeOffset.MinValue;
    }

    private static List<string> ListOwnedFirewallRules(bool bypassCache = false)
    {
        if (!bypassCache &&
            _ownedRuleCache is not null &&
            DateTimeOffset.UtcNow - _ownedRuleCacheAt < OwnedRuleCacheTtl)
        {
            return _ownedRuleCache;
        }

        var list = new List<string>();
        var (ok, output) = RunNetsh("advfirewall firewall show rule name=all");
        if (!ok || string.IsNullOrEmpty(output))
        {
            _ownedRuleCache = list;
            _ownedRuleCacheAt = DateTimeOffset.UtcNow;
            return list;
        }

        foreach (Match m in Regex.Matches(output, @"^Rule Name:\s*(.+)\s*$", RegexOptions.Multiline))
        {
            var name = m.Groups[1].Value.Trim();
            // AlphaAI-DLP-LOCK-* belongs to LockdownNetworkEnforcer — never diff/delete it here.
            if (name.StartsWith(FirewallRulePrefix, StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith(LockdownNetworkEnforcer.RulePrefix, StringComparison.OrdinalIgnoreCase))
                list.Add(name);
        }
        _ownedRuleCache = list;
        _ownedRuleCacheAt = DateTimeOffset.UtcNow;
        return list;
    }

    internal static (bool Ok, string Output) RunNetsh(string args)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            p.Start();
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);
            return (p.ExitCode == 0, stdout + stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static void FlushDns()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "resolvectl",
                    Arguments = "flush-caches",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                p?.WaitForExit(10_000);
                return;
            }

            using var win = Process.Start(new ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            win?.WaitForExit(10_000);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Chrome/Edge machine policies (HKLM Policies\Google\Chrome and Policies\Microsoft\Edge).
    /// Deny-list only: DoH off, QuicAllowed=0, URLBlocklist wildcards per apex.
    /// Lockdown (<paramref name="lockdownAllowHosts"/> non-null): URLBlocklist starts with
    /// <c>*</c> (default-deny address bar) + deny patterns; URLAllowlist = allow hosts.
    /// Chrome/Edge: Allowlist takes precedence over Blocklist for a matching URL — so hosts
    /// that must stay blocked are subtracted from the allow set by the caller. Other browsers
    /// have no enterprise allow API here; network lockdown (P1) still denies them.
    /// </summary>
    private static Result ApplyBrowserPolicies(
        IReadOnlyList<string> blockDomains,
        IReadOnlyList<string>? lockdownAllowHosts)
    {
        try
        {
            var changed = false;
            var lockdown = lockdownAllowHosts is not null;

            if (!lockdown && blockDomains.Count == 0)
            {
                changed |= ClearBrowserPolicies(ChromePolicyKey, ChromeUrlBlockKey, ChromeUrlAllowKey);
                changed |= ClearBrowserPolicies(EdgePolicyKey, EdgeUrlBlockKey, EdgeUrlAllowKey);
                return new Result(changed, 0, 0, changed ? "browser_cleared" : "browser_absent");
            }

            changed |= SetDohMode(ChromePolicyKey, "off");
            changed |= SetDohMode(EdgePolicyKey, "off");
            changed |= SetQuicAllowed(ChromePolicyKey, allowed: false);
            changed |= SetQuicAllowed(EdgePolicyKey, allowed: false);

            List<string> blockPatterns;
            if (lockdown)
            {
                // Default-deny address bar; explicit deny apexes still listed for clarity.
                blockPatterns = new List<string> { "*" };
                blockPatterns.AddRange(BuildUrlBlockPatterns(blockDomains));
                var allowPatterns = BuildUrlAllowPatterns(lockdownAllowHosts!);
                changed |= SetUrlList(ChromeUrlAllowKey, allowPatterns);
                changed |= SetUrlList(EdgeUrlAllowKey, allowPatterns);
                changed |= SetUrlList(ChromeUrlBlockKey, blockPatterns);
                changed |= SetUrlList(EdgeUrlBlockKey, blockPatterns);
                return new Result(changed, blockDomains.Count, blockPatterns.Count,
                    $"lockdown=1; urlallow={allowPatterns.Count}; urlblock={blockPatterns.Count}; doh=off; quic=off");
            }

            changed |= ClearUrlList(ChromeUrlAllowKey);
            changed |= ClearUrlList(EdgeUrlAllowKey);
            blockPatterns = BuildUrlBlockPatterns(blockDomains);
            changed |= SetUrlList(ChromeUrlBlockKey, blockPatterns);
            changed |= SetUrlList(EdgeUrlBlockKey, blockPatterns);
            return new Result(changed, blockDomains.Count, blockPatterns.Count,
                $"urlblock={blockPatterns.Count}; urlallow=cleared; doh=off; quic=off");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new Result(false, blockDomains.Count, 0, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, blockDomains.Count, 0, "browser_error:" + ex.Message);
        }
    }

    private static List<string> BuildUrlBlockPatterns(IReadOnlyList<string> domains)
    {
        var apexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in domains)
        {
            var d = raw.Trim().ToLowerInvariant();
            if (d.StartsWith("www.", StringComparison.Ordinal)) d = d[4..];
            if (d.StartsWith("m.", StringComparison.Ordinal)) d = d[2..];
            if (string.IsNullOrWhiteSpace(d) || !d.Contains('.'))
                continue;
            apexes.Add(d);
        }

        var patterns = new List<string>();
        foreach (var a in apexes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            // Enterprise URLBlocklist patterns — subdomains of googlevideo/youtube included.
            patterns.Add(a);
            patterns.Add($"*.{a}");
            patterns.Add($"*://{a}/*");
            patterns.Add($"*://*.{a}/*");
        }
        return patterns;
    }

    /// <summary>
    /// Allowlist patterns for lockdown. Chrome/Edge match these before URLBlocklist=* .
    /// </summary>
    private static List<string> BuildUrlAllowPatterns(IReadOnlyList<string> hosts)
    {
        var apexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in hosts)
        {
            foreach (var n in PolicyBundleParser.NormalizeDomain(raw))
            {
                var d = n.Trim().ToLowerInvariant();
                if (d.StartsWith("www.", StringComparison.Ordinal)) d = d[4..];
                if (d.StartsWith("m.", StringComparison.Ordinal)) d = d[2..];
                if (string.IsNullOrWhiteSpace(d))
                    continue;
                // IP literals: allow by host form only.
                if (System.Net.IPAddress.TryParse(d, out _))
                {
                    apexes.Add(d);
                    continue;
                }
                if (!d.Contains('.'))
                    continue;
                apexes.Add(d);
            }
        }

        var patterns = new List<string>();
        foreach (var a in apexes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (System.Net.IPAddress.TryParse(a, out _))
            {
                patterns.Add(a);
                patterns.Add($"*://{a}/*");
                patterns.Add($"https://{a}/*");
                patterns.Add($"http://{a}/*");
                continue;
            }
            patterns.Add(a);
            patterns.Add($"*.{a}");
            patterns.Add($"*://{a}/*");
            patterns.Add($"*://*.{a}/*");
            patterns.Add($"https://{a}/*");
            patterns.Add($"https://*.{a}/*");
            patterns.Add($"http://{a}/*");
            patterns.Add($"http://*.{a}/*");
        }
        return patterns;
    }

    private static bool SetUrlList(string keyPath, IReadOnlyList<string> patterns)
    {
        // Replace whole list so removals stick (Chrome reads numbered REG_SZ values).
        try { Registry.LocalMachine.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
        catch { /* ignore */ }

        if (patterns.Count == 0)
            return true;

        using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
        if (key is null) return false;
        for (var i = 0; i < patterns.Count; i++)
            key.SetValue((i + 1).ToString(), patterns[i], RegistryValueKind.String);
        return true;
    }

    private static bool ClearUrlList(string keyPath)
    {
        using var existing = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
        if (existing is null) return false;
        Registry.LocalMachine.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        using var still = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
        if (still is not null)
            throw new InvalidOperationException("urllist_clear_failed:" + keyPath);
        return true;
    }

    private static bool SetQuicAllowed(string keyPath, bool allowed)
    {
        using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
        if (key is null) return false;
        var want = allowed ? 1 : 0;
        var prev = key.GetValue("QuicAllowed");
        if (prev is int i && i == want)
            return false;
        key.SetValue("QuicAllowed", want, RegistryValueKind.DWord);
        return true;
    }

    /// <summary>
    /// Removes URLBlocklist + URLAllowlist + DoH/Quic keys we wrote. Failures must throw
    /// (no silent catch) so Apply → EnforceWorker refuse to fingerprint-advance and retry.
    /// </summary>
    private static bool ClearBrowserPolicies(string policyKey, string urlBlockKey, string urlAllowKey)
    {
        var changed = false;
        changed |= ClearUrlList(urlBlockKey);
        changed |= ClearUrlList(urlAllowKey);

        using var key = Registry.LocalMachine.OpenSubKey(policyKey, writable: true);
        if (key is null) return changed;
        if (key.GetValue("DnsOverHttpsMode") is not null)
        {
            key.DeleteValue("DnsOverHttpsMode", throwOnMissingValue: false);
            changed = true;
        }
        if (key.GetValue("QuicAllowed") is not null)
        {
            key.DeleteValue("QuicAllowed", throwOnMissingValue: false);
            changed = true;
        }
        return changed;
    }

    private static bool SetDohMode(string keyPath, string mode)
    {
        using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
        if (key is null) return false;
        var prev = key.GetValue("DnsOverHttpsMode") as string;
        if (string.Equals(prev, mode, StringComparison.OrdinalIgnoreCase))
            return false;
        key.SetValue("DnsOverHttpsMode", mode, RegistryValueKind.String);
        return true;
    }

    internal static string StripOwnedBlock(string content)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        var inBlock = false;
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Equals(BeginMarker, StringComparison.OrdinalIgnoreCase))
            {
                inBlock = true;
                continue;
            }
            if (t.Equals(EndMarker, StringComparison.OrdinalIgnoreCase))
            {
                inBlock = false;
                continue;
            }
            if (inBlock)
                continue;
            if (t.Contains(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(line);
        }
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1]))
            kept.RemoveAt(kept.Count - 1);
        return string.Join("\r\n", kept);
    }

    /// <summary>
    /// Firewall rule name suffix. Long domains get a stable hash suffix so two names
    /// truncated at 40 chars cannot collide into one AlphaAI-DLP-* rule.
    /// </summary>
    private static string SanitizeRuleName(string domain)
    {
        var lower = domain.Trim().ToLowerInvariant();
        var s = Regex.Replace(lower, @"[^a-zA-Z0-9.\-_]", "_");
        // Avoid colliding with the AlphaAI-DLP-LOCK-* lockdown namespace.
        if (s.StartsWith("lock-", StringComparison.OrdinalIgnoreCase))
            s = "d_" + s;
        if (s.Length <= 40)
            return s;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lower)))[..8]
            .ToLowerInvariant();
        return s[..31] + "-" + hash;
    }

    private static string EscapeNetsh(string s) => s.Replace("\"", "'");

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd() + "\n";

    private static HttpClient CreateDnsClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AlphaAITracker-DLP/1");
        return c;
    }

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is System.Security.SecurityException
        || ex.Message.Contains("access is not allowed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);
}
