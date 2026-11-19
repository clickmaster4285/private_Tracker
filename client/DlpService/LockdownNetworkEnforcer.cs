using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AlphaAITracker.Service;

/// <summary>
/// Total outbound default-deny via Windows Advanced Firewall — Windows only.
///
/// Sets <c>DefaultOutboundAction=Block</c> on Domain/Private/Public profiles, then installs
/// verified explicit ALLOW rules for DHCP/DHCPv6, NDP (ICMPv6), NTP, protective DNS /
/// approved DNS, system-update hosts, the API bootstrap host, and published allow.hosts.
/// Legacy complement <c>DENY-*</c> rules from older builds are removed on apply.
///
/// No provider CDN allow and no "block all Cloudflare" rule. Every CDN IP, including
/// Cloudflare, stays denied because DefaultOutbound=Block. A CDN IP is reachable only
/// when an approved domain's IP expansion adds it. Shared-CDN vhosts are a residual risk.
/// Allowlisted names are pinned in the hosts file (<c># AlphaAI-DLP-ALLOW</c>).
/// Block proof is <c>Get-NetFirewallProfile -PolicyStore ActiveStore</c> (enabled + Block
/// on Domain, Private, and Public) plus an explicit outbound UDP 443 block. netsh only writes.
///
/// Generation lock: <see cref="Apply"/> and <see cref="Clear"/> bump <c>_generation</c>; an
/// in-flight apply whose generation went stale (Clear ran during DNS) no-ops. Fingerprints
/// advance only after OS verification of default-outbound Block + expected allow rules.
/// </summary>
internal static class LockdownNetworkEnforcer
{
    /// <summary>Namespace for every lockdown firewall rule (<c>AlphaAI-DLP-LOCK-*</c>).</summary>
    public const string RulePrefix = DomainBlockEnforcer.FirewallRulePrefix + "LOCK-";
    private const string AllowDhcp = RulePrefix + "ALLOW-DHCP";
    private const string AllowDhcpV6 = RulePrefix + "ALLOW-DHCPV6";
    private const string AllowNdp = RulePrefix + "ALLOW-NDP";
    private const string AllowNtp = RulePrefix + "ALLOW-NTP";
    private const string AllowDnsUdpBase = RulePrefix + "ALLOW-DNS-UDP";
    private const string AllowDnsTcpBase = RulePrefix + "ALLOW-DNS-TCP";
    private const string AllowDotBase = RulePrefix + "ALLOW-DOT";
    internal const string AllowMgmtTcpBase = RulePrefix + "ALLOW-MGMT-TCP";
    internal const string AllowWebTcpBase = RulePrefix + "ALLOW-WEB-TCP";
    /// <summary>Allow-list entries with an explicit port (<c>host:3000</c>) → that one TCP port.</summary>
    internal const string AllowWebPortBase = RulePrefix + "ALLOW-WEBPORT-TCP";
    internal const string AllowSysTcpBase = RulePrefix + "ALLOW-SYS-TCP";
    /// <summary>Lifecycle name. Display name is <see cref="QuicBlockDisplayName"/>.</summary>
    internal const string QuicBlockRuleName = RulePrefix + "BLOCK-QUIC";
    internal const string QuicBlockDisplayName = "Block QUIC";
    // Legacy complement-deny prefixes — deleted on every Apply so upgrades heal.
    // Pre-2.9.1 ALLOW-DEST-TCP / ALLOW-DEST-UDP names are not in the desired set, so the
    // removal loop deletes them and does not recreate an all-port UDP carve-out.
    private const string LegacyDenyTcpBase = RulePrefix + "DENY-TCP";
    private const string LegacyDenyUdpBase = RulePrefix + "DENY-UDP";
    private const string LegacyDenyDnsUdpBase = RulePrefix + "DENY-DNS-UDP";
    private const string LegacyDenyDnsTcpBase = RulePrefix + "DENY-DNS-TCP";
    private const string LegacyDenyDotBase = RulePrefix + "DENY-DOT";

    /// <summary>Max resolved allow IPs kept live. Bootstrap IPs are kept first.</summary>
    public const int MaxAllowIps = 4096;

    /// <summary>Max characters of remoteip list per netsh rule (command-line headroom).</summary>
    private const int MaxRemoteIpChars = 5000;

    private static readonly TimeSpan DnsTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan VerifyEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CleanVerifyEvery = TimeSpan.FromMinutes(5);

    private static readonly Regex HostLike = new(
        @"^[a-z0-9_]([a-z0-9_\-]*[a-z0-9_])?(\.[a-z0-9_]([a-z0-9_\-]*[a-z0-9_])?)*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record DnsEntry(IReadOnlyList<IPAddress> Ips, DateTimeOffset ExpiresUtc);

    private static readonly ConcurrentDictionary<string, DnsEntry> DnsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();
    private static long _generation;
    private static string _lastFingerprint = "";
    private static Dictionary<string, string> _lastRules = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset _lastVerifyUtc = DateTimeOffset.MinValue;
    private static bool _rulesMaybePresent = true; // unknown at boot → verify once
    private static DateTimeOffset _cleanVerifiedUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastTcpKillUtc = DateTimeOffset.MinValue;

    public sealed record Result(
        bool Changed,
        bool Applied,
        int AllowIps,
        bool Overflow,
        string Detail,
        bool RulesPresent = false,
        string HostsDetail = "");

    public static long CurrentGeneration => Interlocked.Read(ref _generation);

    /// <summary>
    /// Apply lockdown. <paramref name="policyHosts"/> = platformAllow + allow hosts;
    /// <paramref name="serverUrl"/> supplies the bootstrap host.
    /// </summary>
    public static Result Apply(IEnumerable<string> policyHosts, string? serverUrl)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, 0, false, "unsupported_os");

        var chromium = DomainBlockEnforcer.ApplyChromiumTransportLockdown();
        if (!chromium.Detail.StartsWith("chromium_doh_quic", StringComparison.Ordinal))
            return new Result(false, false, 0, false, chromium.Detail);

        var gen = Interlocked.Increment(ref _generation);

        // Bootstrap first so it survives the overflow cap. A failed resolve still
        // applies deny-all (or the last saved server IPs) and stays not-ready.
        var bootstrapHost = ExtractHost(serverUrl);
        var freshBootstrap = bootstrapHost is null
            ? new List<IPAddress>()
            : Resolve(bootstrapHost).ToList();
        var bootstrapUnresolved = freshBootstrap.Count == 0;
        List<IPAddress> bootstrapIps;
        if (!bootstrapUnresolved)
        {
            bootstrapIps = freshBootstrap;
            if (bootstrapHost is not null)
                SaveBootstrapIps(bootstrapHost, freshBootstrap);
        }
        else
        {
            bootstrapIps = LoadBootstrapIps(bootstrapHost);
        }

        var hostIps = new List<(string Host, IReadOnlyList<IPAddress> Ips)>();
        if (bootstrapHost is not null && bootstrapIps.Count > 0)
            hostIps.Add((bootstrapHost, bootstrapIps));

        var hostSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        // host:port entries (e.g. an intranet app on 192.168.1.10:3000) open only that TCP port.
        var portHosts = new SortedDictionary<int, SortedSet<string>>();
        foreach (var raw in policyHosts)
        {
            var (h, port) = ParseAllowHost(raw);
            if (h is null)
                continue;
            if (port is int p)
            {
                if (!portHosts.TryGetValue(p, out var set))
                    portHosts[p] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(h);
                continue;
            }
            if (!h.Equals(bootstrapHost, StringComparison.OrdinalIgnoreCase))
                hostSet.Add(h);
        }

        var webIps = new List<IPAddress>();
        var seenWeb = new HashSet<IPAddress>();
        foreach (var h in hostSet)
        {
            var ips = Resolve(h);
            if (ips.Count > 0)
                hostIps.Add((h, ips));
            foreach (var ip in ips)
            {
                if (seenWeb.Add(ip))
                    webIps.Add(ip);
            }
        }
        webIps.Sort((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));

        var portIps = new SortedDictionary<int, List<IPAddress>>();
        foreach (var (port, hosts) in portHosts)
        {
            var list = new List<IPAddress>();
            foreach (var h in hosts)
            {
                var ips = Resolve(h);
                if (ips.Count > 0 && !hostIps.Any(x => x.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
                    hostIps.Add((h, ips));
                foreach (var ip in ips)
                {
                    if (!list.Contains(ip))
                        list.Add(ip);
                }
            }
            if (list.Count > 0)
                portIps[port] = list;
        }

        // Protective DNS stays on 53/853 only. System hosts get TCP 80+443.
        // Management IPs are not also opened on those wider sets.
        var dnsAllow = ResolveProtectiveDns();
        var systemAllow = ResolveSystemAllowHosts();
        var mgmtSet = new HashSet<IPAddress>(bootstrapIps);
        var webOnly = webIps.Where(ip => !mgmtSet.Contains(ip)).ToList();
        var systemOnly = systemAllow.Where(ip => !mgmtSet.Contains(ip)).ToList();

        var ordered = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();
        void Take(IEnumerable<IPAddress> ips)
        {
            foreach (var ip in ips)
            {
                if (seen.Add(ip))
                    ordered.Add(ip);
            }
        }
        Take(bootstrapIps);
        Take(dnsAllow);
        Take(systemOnly);
        Take(webOnly);
        foreach (var list in portIps.Values)
            Take(list);

        var overflow = false;
        if (ordered.Count > MaxAllowIps)
        {
            overflow = true;
            ordered = ordered.Take(MaxAllowIps).ToList();
        }

        var kept = new HashSet<IPAddress>(ordered);
        var mgmtPort = ManagementTcpPort(serverUrl);
        var desired = BuildDesiredAllowRules(
            bootstrapIps.Where(kept.Contains).ToList(),
            mgmtPort,
            webOnly.Where(kept.Contains).ToList(),
            systemOnly.Where(kept.Contains).ToList(),
            dnsAllow.Where(kept.Contains).ToList(),
            portIps.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<IPAddress>)kv.Value.Where(kept.Contains).ToList()));
        var fp = "v299-web80-ports\n" +
                 string.Join("\n", desired.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(kv => kv.Key + "=" + kv.Value));

        lock (Gate)
        {
            if (gen != Interlocked.Read(ref _generation))
                return new Result(false, false, ordered.Count, overflow, "stale_generation");

            var now = DateTimeOffset.UtcNow;
            if (fp == _lastFingerprint && now - _lastVerifyUtc < VerifyEvery
                && QueryActiveNetworkProof().Ok)
                return Finish(false, ordered.Count, overflow, "lock_fresh",
                    bootstrapUnresolved, hostIps, ordered);

            try
            {
                // Fail closed first: default outbound Block before rewriting allow carve-outs.
                var blockSet = SetDefaultOutboundAction(block: true);
                if (!blockSet.Ok)
                    return Fail(blockSet.Output, ordered.Count, overflow);

                var existing = ListLockRules();
                var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                var quic = EnsureQuicBlock();
                if (!quic.Ok)
                    return Fail(quic.Output, ordered.Count, overflow);

                if (fp == _lastFingerprint &&
                    desired.Keys.All(existingSet.Contains) &&
                    existing.All(n => desired.ContainsKey(n) || IsLegacyDenyRule(n) || IsQuicBlockRuleName(n)) &&
                    QueryActiveNetworkProof().Ok)
                {
                    // Heal leftover complement-deny rules from older builds.
                    foreach (var name in existing.Where(IsLegacyDenyRule))
                        DomainBlockEnforcer.RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
                    _lastVerifyUtc = now;
                    return Finish(false, ordered.Count, overflow, "lock_verified",
                        bootstrapUnresolved, hostIps, ordered);
                }

                var added = 0;
                var updated = 0;
                // Add/update ALLOW rules first so there is never an open window with Block
                // and no carve-out for DHCP / API / protective DNS.
                foreach (var (name, remoteIp) in desired)
                {
                    var (proto, port, localPort) = SpecForAllow(name);
                    var exists = existingSet.Contains(name);
                    if (exists && _lastRules.TryGetValue(name, out var prev) && prev == remoteIp)
                        continue;

                    var localPortArg = string.IsNullOrEmpty(localPort) ? "" : $" localport={localPort}";
                    var remotePortArg = string.IsNullOrEmpty(port) ? "" : $" remoteport={port}";
                    var remoteIpArg = string.IsNullOrEmpty(remoteIp) || remoteIp == "any"
                        ? " remoteip=any"
                        : $" remoteip={remoteIp}";

                    (bool Ok, string Output) r;
                    if (exists)
                    {
                        r = DomainBlockEnforcer.RunNetsh(
                            $"advfirewall firewall set rule name=\"{name}\" new action=allow " +
                            $"protocol={proto}{remoteIpArg}{remotePortArg}{localPortArg} enable=yes");
                        updated++;
                    }
                    else
                    {
                        r = DomainBlockEnforcer.RunNetsh(
                            $"advfirewall firewall add rule name=\"{name}\" dir=out action=allow " +
                            $"protocol={proto}{remoteIpArg}{remotePortArg}{localPortArg} enable=yes profile=any");
                        added++;
                    }
                    if (!r.Ok)
                        return Fail(r.Output, ordered.Count, overflow);
                }

                var removed = 0;
                foreach (var name in existing)
                {
                    if (desired.ContainsKey(name) || IsQuicBlockRuleName(name))
                        continue;
                    DomainBlockEnforcer.RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
                    removed++;
                }

                var proof = QueryActiveNetworkProof();
                if (!proof.Ok)
                    return new Result(false, false, ordered.Count, overflow, proof.Detail);

                // Confirm expected allow rules are present before advancing fingerprint.
                var after = new HashSet<string>(ListLockRules(), StringComparer.OrdinalIgnoreCase);
                if (!desired.Keys.All(after.Contains))
                    return new Result(false, false, ordered.Count, overflow,
                        "lock_error:allow_rules_missing");

                _lastRules = new Dictionary<string, string>(desired, StringComparer.OrdinalIgnoreCase);
                _lastFingerprint = fp;
                _lastVerifyUtc = now;
                _rulesMaybePresent = true;
                var changed = added > 0 || updated > 0 || removed > 0 || blockSet.Changed;
                return Finish(changed, ordered.Count, overflow,
                    $"lock_add={added} upd={updated} rm={removed} rules={desired.Count} ips={ordered.Count};outbound=block",
                    bootstrapUnresolved, hostIps, ordered);
            }
            catch (Exception ex)
            {
                return new Result(false, false, ordered.Count, overflow, "lock_error:" + ex.Message);
            }
        }
    }

    /// <summary>
    /// Delete every <c>AlphaAI-DLP-LOCK-*</c> rule and restore default outbound Allow
    /// (lab / recovery only — production Clear is gated by <see cref="ProductionFailClosed"/>).
    /// </summary>
    public static Result Clear()
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, 0, false, "unsupported_os");

        lock (Gate)
        {
            Interlocked.Increment(ref _generation); // invalidate in-flight Apply
            _lastFingerprint = "";
            _lastRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var hostsCleared = RemoveAllowHosts();

            try { DomainBlockEnforcer.ClearChromiumTransportLockdown(); }
            catch { /* firewall clear still runs */ }
            RemoveQuicBlock();
            RestoreForeignOutboundAllows();

            var now = DateTimeOffset.UtcNow;
            if (!_rulesMaybePresent && !hostsCleared && now - _cleanVerifiedUtc < CleanVerifyEvery
                && !VerifyDefaultOutboundBlocked())
                return new Result(false, false, 0, false, "lock_absent");

            try
            {
                var existing = ListLockRules();
                foreach (var name in existing)
                    DomainBlockEnforcer.RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");

                var still = existing.Count == 0 ? existing : ListLockRules();
                if (still.Count > 0)
                    return new Result(false, false, 0, false,
                        "lock_clear_failed:" + string.Join(",", still.Take(8)));

                SetDefaultOutboundAction(block: false);
                _rulesMaybePresent = false;
                _cleanVerifiedUtc = now;
                return new Result(existing.Count > 0, false, 0, false,
                    existing.Count > 0 ? "lock_cleared" : "lock_absent");
            }
            catch (Exception ex)
            {
                return new Result(false, false, 0, false, "lock_clear_failed:" + ex.Message);
            }
        }
    }

    // ---------------------------------------------------------------- rule building

    /// <summary>
    /// Explicit ALLOW carve-outs under default-outbound Block. Values are remoteip lists
    /// (or "any" for DHCP/NDP/NTP helpers that must reach link-local / broadcast).
    /// </summary>
    /// <summary>
    /// Explicit ALLOW carve-outs. Management is one TCP port. Published web hosts are
    /// TCP 80 + 443 (plain-HTTP intranet sites are common); a published <c>host:port</c>
    /// opens exactly that TCP port to that host. The system-update/CRL set is TCP 80 and
    /// 443. Protective DNS is 53/853 only. There is no all-port dest rule and no UDP web
    /// rule (that re-opened QUIC).
    /// </summary>
    internal static Dictionary<string, string> BuildDesiredAllowRules(
        IReadOnlyList<IPAddress> mgmtIps,
        int mgmtPort,
        IReadOnlyList<IPAddress> webIps,
        IReadOnlyList<IPAddress> systemIps,
        IReadOnlyList<IPAddress> dnsIps,
        IReadOnlyDictionary<int, IReadOnlyList<IPAddress>>? webPortIps = null)
    {
        if (mgmtPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(mgmtPort));

        var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AllowDhcp] = "any",
            [AllowDhcpV6] = "any",
            [AllowNdp] = "any",
            [AllowNtp] = "any",
        };

        void AddChunks(string baseName, IReadOnlyList<IPAddress> ips)
        {
            if (ips.Count == 0)
                return;
            var chunks = ChunkRemoteIps(ips);
            for (var i = 0; i < chunks.Count; i++)
            {
                var suffix = i == 0 ? "" : "-" + i;
                rules[baseName + suffix] = chunks[i];
            }
        }

        AddChunks(AllowDnsUdpBase, dnsIps);
        AddChunks(AllowDnsTcpBase, dnsIps);
        AddChunks(AllowDotBase, dnsIps);
        AddChunks(AllowMgmtTcpBase + "-" + mgmtPort, mgmtIps);
        AddChunks(AllowWebTcpBase, webIps);
        AddChunks(AllowSysTcpBase, systemIps);
        if (webPortIps is not null)
        {
            foreach (var (port, ips) in webPortIps)
            {
                if (port is < 1 or > 65535)
                    throw new ArgumentOutOfRangeException(nameof(webPortIps));
                AddChunks(AllowWebPortBase + "-" + port, ips);
            }
        }
        return rules;
    }

    private static List<string> ChunkRemoteIps(IReadOnlyList<IPAddress> ips)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var ip in ips.OrderBy(a => a.AddressFamily).ThenBy(a => a.ToString(), StringComparer.Ordinal))
        {
            var s = ip.ToString();
            if (sb.Length > 0 && sb.Length + 1 + s.Length > MaxRemoteIpChars)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(',');
            sb.Append(s);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    internal static (string Proto, string Port, string LocalPort) SpecForAllow(string name)
    {
        if (name.Equals(AllowDhcp, StringComparison.OrdinalIgnoreCase))
            return ("udp", "67", "68");
        if (name.Equals(AllowDhcpV6, StringComparison.OrdinalIgnoreCase))
            return ("udp", "547", "546");
        if (name.Equals(AllowNdp, StringComparison.OrdinalIgnoreCase))
            return ("icmpv6", "", "");
        if (name.Equals(AllowNtp, StringComparison.OrdinalIgnoreCase))
            return ("udp", "123", "");
        if (name.StartsWith(AllowDnsUdpBase, StringComparison.OrdinalIgnoreCase))
            return ("udp", "53", "");
        if (name.StartsWith(AllowDnsTcpBase, StringComparison.OrdinalIgnoreCase))
            return ("tcp", "53", "");
        if (name.StartsWith(AllowDotBase, StringComparison.OrdinalIgnoreCase))
            return ("tcp", "853", "");
        if (name.StartsWith(AllowWebTcpBase, StringComparison.OrdinalIgnoreCase))
            return ("tcp", "80,443", "");
        if (name.StartsWith(AllowWebPortBase + "-", StringComparison.OrdinalIgnoreCase))
        {
            var rest = name[(AllowWebPortBase.Length + 1)..];
            var dash = rest.IndexOf('-');
            var portToken = dash >= 0 ? rest[..dash] : rest;
            if (!int.TryParse(portToken, out var port) || port is < 1 or > 65535)
                throw new InvalidOperationException("web_tcp_port:" + name);
            return ("tcp", portToken, "");
        }
        if (name.StartsWith(AllowSysTcpBase, StringComparison.OrdinalIgnoreCase))
            return ("tcp", "80,443", "");
        if (name.StartsWith(AllowMgmtTcpBase + "-", StringComparison.OrdinalIgnoreCase))
        {
            var rest = name[(AllowMgmtTcpBase.Length + 1)..];
            var dash = rest.IndexOf('-');
            var portToken = dash >= 0 ? rest[..dash] : rest;
            if (!int.TryParse(portToken, out var port) || port is < 1 or > 65535)
                throw new InvalidOperationException("mgmt_tcp_port:" + name);
            return ("tcp", portToken, "");
        }
        // Never fall open to every port. Unknown names are a programming error.
        throw new InvalidOperationException("allow_spec:" + name);
    }

    internal static bool IsQuicBlockRuleName(string name) =>
        name.Equals(QuicBlockRuleName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(QuicBlockDisplayName, StringComparison.OrdinalIgnoreCase);

    internal static int ManagementTcpPort(string? serverUrl) => PortFromServerUrl(serverUrl);

    private static bool IsLegacyDenyRule(string name) =>
        name.StartsWith(LegacyDenyTcpBase, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(LegacyDenyUdpBase, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(LegacyDenyDnsUdpBase, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(LegacyDenyDnsTcpBase, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(LegacyDenyDotBase, StringComparison.OrdinalIgnoreCase);

    private static (bool Ok, bool Changed, string Output) SetDefaultOutboundAction(bool block)
    {
        var action = block ? "block" : "allow";
        var changed = false;
        // Profiles must be ON. Lab PCs had Block with Enabled=False — rules never applied.
        foreach (var profile in new[] { "domainprofile", "privateprofile", "publicprofile" })
        {
            var (stateOk, _) = DomainBlockEnforcer.RunNetsh($"advfirewall set {profile} state on");
            if (stateOk)
                changed = true;
        }
        DomainBlockEnforcer.RunNetsh("advfirewall set allprofiles state on");
        // Best-effort NetSecurity path mirrors the lab command when netsh alone is stale.
        RunPowerShell("""
            $ErrorActionPreference = 'Continue'
            Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True | Out-Null
            """);

        foreach (var profile in new[] { "domainprofile", "privateprofile", "publicprofile" })
        {
            var (ok, output) = DomainBlockEnforcer.RunNetsh(
                $"advfirewall set {profile} firewallpolicy blockinbound,{action}outbound");
            // Older netsh also accepts DefaultOutboundAction alone; try both styles.
            if (!ok)
            {
                (ok, output) = DomainBlockEnforcer.RunNetsh(
                    $"advfirewall set {profile} settings inboundblock outbound{action}");
            }
            if (!ok)
                return (false, changed, output);
            changed = true;
        }
        // Also set the current-profile knob used by some Win10 builds.
        DomainBlockEnforcer.RunNetsh(
            $"advfirewall set allprofiles firewallpolicy blockinbound,{(block ? "block" : "allow")}outbound");
        return (true, changed, action);
    }

    /// <summary>
    /// ActiveStore proof: every profile exists, is enabled, and blocks outbound.
    /// Also requires the explicit QUIC block and no product UDP 443 punch-through.
    /// </summary>
    public static bool VerifyDefaultOutboundBlocked() => QueryActiveNetworkProof().OutboundBlocked;

    internal static FirewallActiveStoreProof.Verdict QueryActiveNetworkProof()
    {
        var run = RunPowerShellDetailed(ActiveStoreScript);
        if (!run.Ok)
        {
            var reason = FirstNonEmptyLine(run.Stderr) ?? FirstNonEmptyLine(run.Stdout) ?? run.Detail;
            return FirewallActiveStoreProof.QueryFailed(reason);
        }
        var json = FirewallActiveStoreProof.ExtractJson(run.Stdout);
        if (string.IsNullOrWhiteSpace(json))
        {
            var reason = FirstNonEmptyLine(run.Stderr) ?? "missing_json_marker";
            return FirewallActiveStoreProof.QueryFailed(reason);
        }
        return FirewallActiveStoreProof.Parse(json);
    }

    private static string? FirstNonEmptyLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.Length > 0)
                return t.Length > 160 ? t[..160] : t;
        }
        return null;
    }

    /// <summary>
    /// Pure egress decision. A public TCP connect is failure. Management success is a
    /// completed TCP connect. DNS failure is success only when <paramref name="usedLastSavedIp"/>
    /// is set and that last IP actually connected.
    /// </summary>
    internal static (bool Ok, string Detail) EvaluateBehavioralEgress(
        bool publicConnected,
        bool managementConnected,
        bool usedLastSavedIp)
    {
        if (publicConnected)
            return (false, "egress_probe_public_reachable");
        if (managementConnected)
            return (true, usedLastSavedIp
                ? "egress_probe_ok;mgmt=last_ip"
                : "egress_probe_ok;mgmt=resolved");
        // Offline-first: public egress is already proven blocked, so an unreachable
        // management server (PC offline / server down) must not hold the policy gate.
        // Enforcement keeps running from the last good bundle; sync resumes later.
        if (usedLastSavedIp)
            return (true, "egress_probe_ok;mgmt=offline_last_ip");
        return (true, "egress_probe_ok;mgmt=offline");
    }

    /// <summary>
    /// Behavioral egress before the network layer is ready. 1.1.1.1:443 is a public-IP
    /// probe, not a provider CDN allow or block. Management must complete a TCP connect.
    /// When DNS fails, the last saved IP is success only if that TCP connect succeeds.
    /// </summary>
    public static (bool Ok, string Detail) VerifyBehavioralEgress(string? serverUrl) =>
        VerifyBehavioralEgress(serverUrl, QueryActiveNetworkProof());

    internal static (bool Ok, string Detail) VerifyBehavioralEgress(
        string? serverUrl,
        FirewallActiveStoreProof.Verdict proof)
    {
        // Profiles, explicit QUIC block, and "no UDP 443 allow" are one verdict.
        if (!proof.Ok)
            return (false, string.IsNullOrWhiteSpace(proof.Detail)
                ? FirewallActiveStoreProof.QueryFailedDetail
                : proof.Detail);

        // Public IP probe. Not a Cloudflare allow or block rule.
        var publicConnected = TryTcpConnect(IPAddress.Parse("1.1.1.1"), 443, TimeSpan.FromMilliseconds(800));
        var host = HostFromServerUrl(serverUrl);
        if (string.IsNullOrWhiteSpace(host))
            return (false, "egress_probe_mgmt_unconfigured");

        var port = PortFromServerUrl(serverUrl);
        if (IPAddress.TryParse(host, out var literal))
        {
            var connected = TryTcpConnect(literal, port, TimeSpan.FromSeconds(2));
            return EvaluateBehavioralEgress(publicConnected, connected, usedLastSavedIp: false);
        }

        List<IPAddress> addrs;
        var dnsFailed = false;
        try
        {
            addrs = Dns.GetHostAddresses(host).Where(IsUsable).Take(3).ToList();
        }
        catch
        {
            addrs = [];
            dnsFailed = true;
        }
        if (addrs.Count == 0)
            dnsFailed = true;

        if (!dnsFailed)
        {
            var connected = false;
            foreach (var ip in addrs)
            {
                if (TryTcpConnect(ip, port, TimeSpan.FromSeconds(2)))
                {
                    connected = true;
                    break;
                }
            }
            return EvaluateBehavioralEgress(publicConnected, connected, usedLastSavedIp: false);
        }

        var last = LoadBootstrapIps(host);
        var lastConnected = false;
        foreach (var ip in last.Take(3))
        {
            if (TryTcpConnect(ip, port, TimeSpan.FromSeconds(2)))
            {
                lastConnected = true;
                break;
            }
        }
        return EvaluateBehavioralEgress(publicConnected, lastConnected, usedLastSavedIp: true);
    }

    private static string? HostFromServerUrl(string? serverUrl)
    {
        var s = (serverUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return null;
        if (!s.Contains("://", StringComparison.Ordinal))
            s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
            return null;
        return uri.Host;
    }

    private static int PortFromServerUrl(string? serverUrl)
    {
        var s = (serverUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return 443;
        if (!s.Contains("://", StringComparison.Ordinal))
            s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
            return 443;
        if (uri.IsDefaultPort)
            return string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ? 80 : 443;
        return uri.Port;
    }

    private static bool TryTcpConnect(IPAddress ip, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connect = client.ConnectAsync(ip, port);
            if (!connect.Wait(timeout))
                return false;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static List<IPAddress> ResolveProtectiveDns()
    {
        var raw = Environment.GetEnvironmentVariable("ALPHA_DLP_PROTECTIVE_DNS") ?? "";
        var list = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();
        foreach (var part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var host = NormalizeHost(part) ?? part.Trim();
            foreach (var ip in Resolve(host))
            {
                if (seen.Add(ip))
                    list.Add(ip);
            }
        }
        return list;
    }

    private static List<IPAddress> ResolveSystemAllowHosts()
    {
        var raw = Environment.GetEnvironmentVariable("ALPHA_DLP_SYSTEM_ALLOW_HOSTS") ?? "";
        // Minimal built-in exceptions so Windows activation / update / CRL stay reachable
        // when IT has not published a protective DNS gateway yet.
        var defaults = new[]
        {
            "time.windows.com",
            "activation.sls.microsoft.com",
            "crl.microsoft.com",
            "ocsp.digicert.com",
        };
        var hosts = new List<string>(defaults);
        foreach (var part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var h = NormalizeHost(part);
            if (h != null)
                hosts.Add(h);
        }
        var list = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();
        foreach (var h in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var ip in Resolve(h))
            {
                if (seen.Add(ip))
                    list.Add(ip);
            }
        }
        return list;
    }

    /// <summary>
    /// Rules + default outbound Block are in place. Ready when the allowlist hosts pin
    /// was written and observed outbound Block verifies. A missing server resolve still
    /// leaves total outbound denied (fail-closed); detail notes <c>bootstrap_unresolved</c>.
    /// </summary>
    private static Result Finish(
        bool changed,
        int allowIps,
        bool overflow,
        string detail,
        bool bootstrapUnresolved,
        IReadOnlyList<(string Host, IReadOnlyList<IPAddress> Ips)> hostIps,
        IReadOnlyList<IPAddress> allowList)
    {
        // Only admin-allowed ports may leave the PC: switch off Windows/app outbound
        // Allow rules that would otherwise reach any port (Allow beats default Block).
        SweepForeignOutboundAllows(force: changed);
        var hosts = ApplyAllowHosts(hostIps);
        MaybeKillWeb(allowList, force: true);
        var hostsOk = hosts is "hosts_allow_updated" or "hosts_allow_cleared"
            or "hosts_allow_fresh" or "hosts_allow_absent";
        var proof = QueryActiveNetworkProof();
        var serverUrl = Environment.GetEnvironmentVariable("ALPHA_SERVER_URL") ?? "";
        var probe = VerifyBehavioralEgress(serverUrl, proof);
        var ready = hostsOk && proof.Ok && probe.Ok;
        var finalDetail = detail;
        if (!proof.Ok)
            finalDetail = proof.Detail;
        else if (!probe.Ok)
            finalDetail = probe.Detail;
        else if (bootstrapUnresolved)
            finalDetail = string.IsNullOrEmpty(detail) || detail is "lock_fresh" or "lock_verified"
                ? "bootstrap_unresolved"
                : detail + ";bootstrap_unresolved";
        else if (!hostsOk)
            finalDetail = hosts;
        else
            finalDetail = (string.IsNullOrEmpty(detail) ? "lock_ok" : detail) + ";" + probe.Detail;
        return new Result(changed, ready, allowIps, overflow, finalDetail,
            RulesPresent: proof.OutboundBlocked, HostsDetail: hosts);
    }

    // ---------------------------------------------------------------- DNS + hosts

    private static IReadOnlyList<IPAddress> Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var literal))
            return IsUsable(literal) ? new[] { literal } : Array.Empty<IPAddress>();

        DnsCache.TryGetValue(host, out var cached);
        if (cached is not null && DateTimeOffset.UtcNow < cached.ExpiresUtc)
            return cached.Ips;

        var list = new List<IPAddress>();
        try
        {
            var task = Dns.GetHostAddressesAsync(host);
            if (task.Wait(TimeSpan.FromSeconds(6)))
            {
                foreach (var a in task.Result)
                {
                    if (IsUsable(a) && !list.Contains(a))
                        list.Add(a);
                }
            }
        }
        catch
        {
            /* fall through to stale cache */
        }

        // Keep previous IPs on a resolve blip so a transient DNS failure does not shrink the allow set.
        if (list.Count == 0 && cached is { Ips.Count: > 0 })
            return cached.Ips;

        DnsCache[host] = new DnsEntry(list, DateTimeOffset.UtcNow.Add(DnsTtl));
        return list;
    }

    private static bool IsUsable(IPAddress a) =>
        a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
        && !IPAddress.IsLoopback(a)
        && !a.Equals(IPAddress.Any)
        && !a.Equals(IPAddress.IPv6Any);

    /// <summary>Host (or IP literal) from ALPHA_SERVER_URL / DlpConfig.ServerUrl.</summary>
    private static string? ExtractHost(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return null;
        var s = serverUrl.Trim();
        if (!s.Contains("://", StringComparison.Ordinal))
            s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)
            ? u.Host.Trim('[', ']').ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// Allow-list entry → host (or IP) plus an explicit port when one was published
    /// (<c>192.168.1.10:3000</c>, <c>http://intranet:8080/x</c>, <c>[fd00::1]:8443</c>).
    /// Ports 80/443 are folded into the default web rule (null port).
    /// </summary>
    internal static (string? Host, int? Port) ParseAllowHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, null);
        var s = raw.Trim().ToLowerInvariant();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var cut = s.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) s = s[..cut];

        int? port = null;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close > 0 && close + 1 < s.Length && s[close + 1] == ':' &&
                int.TryParse(s[(close + 2)..], out var p6))
                port = p6;
            if (close > 0) s = s[1..close];
        }
        else if (s.Count(c => c == ':') == 1)
        {
            var colon = s.IndexOf(':');
            if (int.TryParse(s[(colon + 1)..], out var p4))
                port = p4;
            s = s[..colon];
        }

        if (port is < 1 or > 65535 or 80 or 443)
            port = null;
        var host = NormalizeHost(s);
        return (host, host is null ? null : port);
    }

    /// <summary>Strip scheme/path/port and a leading <c>*.</c>; null when not a host or IP.</summary>
    private static string? NormalizeHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var s = raw.Trim().ToLowerInvariant();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        if (IPAddress.TryParse(s, out var ip))
            return ip.ToString();
        var colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        if (s.StartsWith("*.", StringComparison.Ordinal)) s = s[2..];
        s = s.Trim('.');
        return s.Length > 0 && HostLike.IsMatch(s) ? s : null;
    }

    // ---------------------------------------------------------------- allow hosts + bootstrap cache + TCP kill

    private static string BootstrapCachePath =>
        Path.Combine(DlpDataPaths.DlpRoot, "bootstrap-ips.txt");

    private static void SaveBootstrapIps(string host, IReadOnlyList<IPAddress> ips)
    {
        try
        {
            Directory.CreateDirectory(DlpDataPaths.DlpRoot);
            var sb = new StringBuilder();
            sb.AppendLine(host.Trim().ToLowerInvariant());
            foreach (var ip in ips)
                sb.AppendLine(ip.ToString());
            File.WriteAllText(BootstrapCachePath, sb.ToString());
        }
        catch
        {
            /* cache is best-effort; the live resolve already succeeded */
        }
    }

    private static List<IPAddress> LoadBootstrapIps(string? host)
    {
        var list = new List<IPAddress>();
        if (string.IsNullOrWhiteSpace(host) || !File.Exists(BootstrapCachePath))
            return list;
        try
        {
            var lines = File.ReadAllLines(BootstrapCachePath);
            if (lines.Length == 0 ||
                !lines[0].Trim().Equals(host.Trim(), StringComparison.OrdinalIgnoreCase))
                return list;
            for (var i = 1; i < lines.Length; i++)
            {
                if (IPAddress.TryParse(lines[i].Trim(), out var ip) && IsUsable(ip) && !list.Contains(ip))
                    list.Add(ip);
            }
        }
        catch
        {
            list.Clear();
        }
        return list;
    }

    /// <summary>
    /// Pin allowlisted names at the addresses just resolved. Marker is separate from the
    /// web-blocker <c># AlphaAI-DLP-BEGIN</c> block so a hosts rewrite does not drop it.
    /// </summary>
    private static string ApplyAllowHosts(
        IReadOnlyList<(string Host, IReadOnlyList<IPAddress> Ips)> hostIps)
    {
        var lines = new List<string>();
        foreach (var (host, ips) in hostIps)
        {
            if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _))
                continue;
            foreach (var ip in ips)
            {
                if (!IsUsable(ip))
                    continue;
                lines.Add(ip + " " + host.Trim().ToLowerInvariant() + " " + DomainBlockEnforcer.AllowLineMarker);
            }
        }

        string original;
        try
        {
            original = File.Exists(DomainBlockEnforcer.HostsPath)
                ? File.ReadAllText(DomainBlockEnforcer.HostsPath, Encoding.UTF8)
                : "";
        }
        catch (Exception ex)
        {
            return "error_read:" + ex.Message;
        }

        var stripped = StripAllowBlock(original);
        var next = stripped.TrimEnd() + "\r\n";
        if (lines.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(DomainBlockEnforcer.AllowBeginMarker);
            foreach (var line in lines.Distinct(StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(line);
            sb.AppendLine(DomainBlockEnforcer.AllowEndMarker);
            next += sb.ToString();
        }

        if (NormalizeNewlines(next) == NormalizeNewlines(original))
            return lines.Count == 0 ? "hosts_allow_absent" : "hosts_allow_fresh";

        try
        {
            var tmp = DomainBlockEnforcer.HostsPath + ".alpha-dlp-allow.tmp";
            File.WriteAllText(tmp, next, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Copy(tmp, DomainBlockEnforcer.HostsPath, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
            return lines.Count == 0 ? "hosts_allow_cleared" : "hosts_allow_updated";
        }
        catch (UnauthorizedAccessException)
        {
            return "access_denied_need_admin";
        }
        catch (Exception ex)
        {
            return "error_write:" + ex.Message;
        }
    }

    /// <returns>True when an allow block was removed.</returns>
    private static bool RemoveAllowHosts()
    {
        var detail = ApplyAllowHosts(Array.Empty<(string, IReadOnlyList<IPAddress>)>());
        return detail == "hosts_allow_cleared";
    }

    private static string StripAllowBlock(string content)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        var inAllow = false;
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Equals(DomainBlockEnforcer.AllowBeginMarker, StringComparison.OrdinalIgnoreCase))
            {
                inAllow = true;
                continue;
            }
            if (t.Equals(DomainBlockEnforcer.AllowEndMarker, StringComparison.OrdinalIgnoreCase))
            {
                inAllow = false;
                continue;
            }
            if (inAllow)
                continue;
            kept.Add(line);
        }
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1]))
            kept.RemoveAt(kept.Count - 1);
        return string.Join("\r\n", kept);
    }

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static void MaybeKillWeb(IReadOnlyList<IPAddress> allow, bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastTcpKillUtc < TimeSpan.FromSeconds(10))
            return;
        _lastTcpKillUtc = now;
        try { TcpConnectionKiller.KillWebExcept(allow); }
        catch { /* firewall still drops new flows */ }
    }

    // ---------------------------------------------------------------- netsh helpers

    private static List<string> ListLockRules()
    {
        var list = new List<string>();
        var (ok, output) = DomainBlockEnforcer.RunNetsh("advfirewall firewall show rule name=all");
        if (!ok || string.IsNullOrEmpty(output))
            return list;
        foreach (Match m in Regex.Matches(output, @"^Rule Name:\s*(.+)\s*$", RegexOptions.Multiline))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase))
                list.Add(name);
        }
        return list;
    }

    // ---------------------------------------------------------------- foreign outbound allows

    internal const string ForeignDisabledMarker = "ALPHA_FOREIGN_DISABLED:";
    private static readonly TimeSpan ForeignSweepEvery = TimeSpan.FromMinutes(5);
    private static DateTimeOffset _foreignSweepUtc = DateTimeOffset.MinValue;

    private static string ForeignDisabledPath =>
        Path.Combine(DlpDataPaths.DlpRoot, "foreign-outbound-disabled.txt");

    /// <summary>
    /// Disable local (PersistentStore) outbound Allow rules that are not product rules and
    /// have no remote-port limit. Core Networking (DNS/DHCP/IPv6) and non TCP/UDP protocols
    /// stay on. Names are remembered so <see cref="RestoreForeignOutboundAllows"/> re-enables
    /// exactly what we turned off. Throttled; Windows/app installs can add rules later.
    /// </summary>
    private static void SweepForeignOutboundAllows(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _foreignSweepUtc < ForeignSweepEvery)
            return;
        _foreignSweepUtc = now;
        try
        {
            var run = RunPowerShellDetailed(DisableForeignAllowsScript);
            var names = ParseForeignDisabled(run.Stdout);
            if (names.Count == 0)
                return;
            Directory.CreateDirectory(DlpDataPaths.DlpRoot);
            var known = File.Exists(ForeignDisabledPath)
                ? new HashSet<string>(File.ReadAllLines(ForeignDisabledPath)
                    .Select(l => l.Trim()).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
                known.Add(n);
            File.WriteAllLines(ForeignDisabledPath, known.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            /* best-effort: default Block + product rules still apply */
        }
    }

    internal static List<string> ParseForeignDisabled(string? stdout)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(stdout))
            return list;
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (!t.StartsWith(ForeignDisabledMarker, StringComparison.Ordinal))
                continue;
            var name = t[ForeignDisabledMarker.Length..].Trim();
            if (name.Length > 0 && !name.StartsWith(DomainBlockEnforcer.FirewallRulePrefix, StringComparison.OrdinalIgnoreCase))
                list.Add(name);
        }
        return list;
    }

    /// <summary>Re-enable every rule the sweep disabled, then forget them.</summary>
    private static void RestoreForeignOutboundAllows()
    {
        _foreignSweepUtc = DateTimeOffset.MinValue;
        try
        {
            if (!File.Exists(ForeignDisabledPath))
                return;
            var names = File.ReadAllLines(ForeignDisabledPath)
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (names.Count > 0)
            {
                var path = Path.Combine(Path.GetTempPath(), "alpha-dlp-restore-" + Guid.NewGuid().ToString("n") + ".txt");
                File.WriteAllLines(path, names);
                try
                {
                    var run = RunPowerShellDetailed(
                        "$ErrorActionPreference = 'Continue'\n" +
                        "Get-Content -LiteralPath '" + path.Replace("'", "''") + "' | ForEach-Object {\n" +
                        "  Enable-NetFirewallRule -Name $_ -ErrorAction SilentlyContinue }\n");
                    if (!run.Ok)
                        return; // keep the list; next Clear retries
                }
                finally
                {
                    try { File.Delete(path); } catch { /* ignore */ }
                }
            }
            File.Delete(ForeignDisabledPath);
        }
        catch
        {
            /* keep the list for the next Clear */
        }
    }

    private const string DisableForeignAllowsScript = """
        $ErrorActionPreference = 'Continue'
        $core = '@FirewallAPI.dll,-25000'
        $ports = @{}
        Get-NetFirewallPortFilter -PolicyStore PersistentStore -All -ErrorAction SilentlyContinue |
          ForEach-Object { $ports[[string]$_.InstanceID] = $_ }
        Get-NetFirewallRule -PolicyStore PersistentStore -Direction Outbound -Action Allow -Enabled True -ErrorAction SilentlyContinue |
          Where-Object {
            # netsh-created product rules get a GUID Name; the prefix is the DisplayName.
            $_.Name -notlike 'AlphaAI-DLP-*' -and
            [string]$_.DisplayName -notlike 'AlphaAI-DLP-*' -and
            [string]$_.Description -notlike 'AlphaAI-DLP-*' -and
            [string]$_.Group -ne $core
          } |
          ForEach-Object {
            $pf = $ports[[string]$_.Name]
            $proto = if ($pf) { [string]$pf.Protocol } else { 'Any' }
            $rp = if ($pf) { ((@($pf.RemotePort) | ForEach-Object { "$_" }) -join ',') } else { 'Any' }
            if (($proto -eq 'TCP' -or $proto -eq 'UDP' -or $proto -eq 'Any') -and ($rp -eq 'Any' -or $rp -eq '')) {
              try {
                Disable-NetFirewallRule -Name $_.Name -ErrorAction Stop
                Write-Output ('ALPHA_FOREIGN_DISABLED:' + $_.Name)
              } catch { }
            }
          }
        """;

    private static (bool Ok, string Output) EnsureQuicBlock()
    {
        var run = RunPowerShellDetailed(EnsureQuicScript);
        var text = (run.Stdout + "\n" + run.Stderr).Trim();
        if (!run.Ok)
            return (false, string.IsNullOrWhiteSpace(text) ? "quic_rule_missing" : text);
        return (true, text);
    }

    private static void RemoveQuicBlock()
    {
        try { RunPowerShell(RemoveQuicScript); }
        catch { /* Clear still deletes netsh LOCK rules */ }
    }

    private static (bool Ok, string Output) RunPowerShell(string script)
    {
        var run = RunPowerShellDetailed(script);
        var text = (run.Stdout + "\n" + run.Stderr).Trim();
        return (run.Ok, string.IsNullOrWhiteSpace(text) ? run.Detail : text);
    }

    private sealed record PowerShellRun(bool Ok, string Stdout, string Stderr, string Detail);

    private static PowerShellRun RunPowerShellDetailed(string script)
    {
        var psExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(psExe))
            psExe = "powershell.exe";
        string? tmp = null;
        try
        {
            tmp = Path.Combine(Path.GetTempPath(), "alpha-dlp-" + Guid.NewGuid().ToString("n") + ".ps1");
            File.WriteAllText(tmp, script);
            using var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = psExe,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + tmp + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (!p.Start())
                return new PowerShellRun(false, "", "", "powershell_start_failed");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(90_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new PowerShellRun(false, stdout, stderr, "powershell_timeout");
            }
            return new PowerShellRun(p.ExitCode == 0, stdout, stderr, "powershell_exit=" + p.ExitCode);
        }
        catch (Exception ex)
        {
            return new PowerShellRun(false, "", "", ex.GetType().Name + ":" + Truncate(ex.Message, 100));
        }
        finally
        {
            if (tmp is not null)
            {
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max];

    private const string EnsureQuicScript = """
        $ErrorActionPreference = 'Stop'
        $name = 'AlphaAI-DLP-LOCK-BLOCK-QUIC'
        function Test-Quic($r) {
          if (-not $r) { return $false }
          $pf = $r | Get-NetFirewallPortFilter
          $port = (@($pf.RemotePort) | ForEach-Object { "$_" }) -join ','
          return ([string]$r.Enabled -eq 'True' -and [string]$r.Action -eq 'Block' -and [string]$r.Direction -eq 'Outbound' -and [string]$pf.Protocol -eq 'UDP' -and $port -eq '443')
        }
        $r = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
        if (Test-Quic $r) { Write-Output 'quic_present'; exit 0 }
        if ($r) { Remove-NetFirewallRule -Name $name -ErrorAction SilentlyContinue }
        New-NetFirewallRule -Name $name -DisplayName 'Block QUIC' -Description $name -Direction Outbound -Action Block -Protocol UDP -RemotePort 443 -Profile Any -Enabled True | Out-Null
        Write-Output 'quic_created'
        """;

    private const string RemoveQuicScript = """
        $ErrorActionPreference = 'Continue'
        $name = 'AlphaAI-DLP-LOCK-BLOCK-QUIC'
        Remove-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
        Get-NetFirewallRule -DisplayName 'Block QUIC' -ErrorAction SilentlyContinue |
          Where-Object { $_.Name -eq $name -or $_.Description -eq $name } |
          Remove-NetFirewallRule -ErrorAction SilentlyContinue
        """;

    // Emits one marked JSON line. Only product LOCK allow rules are listed for the
    // UDP-443 punch-through check — not every Store/shell UDP Any allow.
    private const string ActiveStoreScript = """
        $ErrorActionPreference = 'Stop'
        function Convert-Profile($p) {
          [pscustomobject]@{
            Name = [string]$p.Name
            Enabled = [string]$p.Enabled
            DefaultOutboundAction = [string]$p.DefaultOutboundAction
          }
        }
        $active = @(Get-NetFirewallProfile -PolicyStore ActiveStore | ForEach-Object { Convert-Profile $_ })
        $persistent = @(Get-NetFirewallProfile -PolicyStore PersistentStore | ForEach-Object { Convert-Profile $_ })
        $q = Get-NetFirewallRule -PolicyStore ActiveStore -Name 'AlphaAI-DLP-LOCK-BLOCK-QUIC' -ErrorAction SilentlyContinue
        if (-not $q) {
          $byDisplay = @(Get-NetFirewallRule -PolicyStore ActiveStore -DisplayName 'Block QUIC' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'AlphaAI-DLP-LOCK-BLOCK-QUIC' -or $_.Description -eq 'AlphaAI-DLP-LOCK-BLOCK-QUIC' })
          if ($byDisplay.Count -gt 0) { $q = $byDisplay[0] }
        }
        if ($q) {
          $pf = $q | Get-NetFirewallPortFilter
          $quic = [pscustomobject]@{
            present = $true
            enabled = [string]$q.Enabled
            action = [string]$q.Action
            direction = [string]$q.Direction
            protocol = [string]$pf.Protocol
            remotePort = ((@($pf.RemotePort) | ForEach-Object { "$_" }) -join ',')
          }
        } else {
          $quic = [pscustomobject]@{ present = $false; enabled = 'False'; action = ''; direction = ''; protocol = ''; remotePort = '' }
        }
        $allows = @()
        Get-NetFirewallRule -PolicyStore ActiveStore -Direction Outbound -Action Allow -Enabled True -ErrorAction SilentlyContinue |
          Where-Object {
            $_.Name -like 'AlphaAI-DLP-LOCK-*' -or
            $_.DisplayName -like 'AlphaAI-DLP-LOCK-*' -or
            $_.Description -like 'AlphaAI-DLP-LOCK-*' -or
            $_.Name -like '*ALLOW-DEST-UDP*' -or
            $_.DisplayName -like '*ALLOW-DEST-UDP*'
          } | ForEach-Object {
            $pf = $_ | Get-NetFirewallPortFilter
            $allows += [pscustomobject]@{
              name = [string]($_.Name)
              protocol = [string]$pf.Protocol
              remotePort = ((@($pf.RemotePort) | ForEach-Object { "$_" }) -join ',')
            }
          }
        $json = [pscustomobject]@{ active = $active; persistent = $persistent; quic = $quic; allows = $allows } | ConvertTo-Json -Depth 6 -Compress
        Write-Output ('ALPHA_ACTIVE_STORE_JSON:' + $json)
        """;

    private static Result Fail(string output, int ips, bool overflow)
    {
        var detail = output.Contains("elevation", StringComparison.OrdinalIgnoreCase) ||
                     output.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
            ? "access_denied_need_admin"
            : "lock_error:" + output.Trim();
        return new Result(false, false, ips, overflow, detail);
    }
}
