using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AlphaAITracker.Service;

/// <summary>
/// P1 lockdown (default-deny web) via Windows Advanced Firewall — Windows only.
///
/// Windows Firewall evaluates explicit BLOCK rules before explicit ALLOW rules, so a literal
/// "one deny + allow-by-IP" rule set cannot work (the deny would win over every allow).
/// Instead the deny rules carry the COMPLEMENT of the allowed set:
/// <c>remoteip = (all IPv4 + IPv6) − (allowed IPs + LAN/loopback/link-local/multicast)</c>,
/// <c>remoteport = 80,443</c>, one rule per protocol (TCP, UDP/QUIC). Allowed destinations are
/// therefore simply never matched by a block rule. When the complement is too long for one
/// netsh command it is chunked (<c>…-DENY-TCP</c>, <c>…-DENY-TCP-1</c>, …) — still no
/// per-domain rules. Rule names: <c>AlphaAI-DLP-LOCK-DENY-TCP[-n]</c>, <c>…-DENY-UDP[-n]</c>.
///
/// Allowed set = bootstrap host (ALPHA_SERVER_URL) + platformAllow.hosts + allow.hosts, resolved
/// with the system resolver (NOT DoH — DoH is port 443 and would be blocked). Wildcard entries
/// resolve the apex only. The same complement also denies public DNS (UDP/TCP 53) and
/// DNS-over-TLS (TCP 853). Allowlisted names are pinned in the hosts file
/// (<c># AlphaAI-DLP-ALLOW</c>) so they still resolve. If the server name does not resolve,
/// the last saved server IPs are used and the apply stays not-ready (<c>bootstrap_unresolved</c>)
/// — public 80/443/53/853 are still denied.
///
/// Generation lock: <see cref="Apply"/> and <see cref="Clear"/> bump <c>_generation</c>; an
/// in-flight apply whose generation went stale (Clear ran during DNS) no-ops.
/// </summary>
internal static class LockdownNetworkEnforcer
{
    /// <summary>Namespace for every lockdown firewall rule (<c>AlphaAI-DLP-LOCK-*</c>).</summary>
    public const string RulePrefix = DomainBlockEnforcer.FirewallRulePrefix + "LOCK-";
    private const string DenyTcpBase = RulePrefix + "DENY-TCP";
    private const string DenyUdpBase = RulePrefix + "DENY-UDP";
    private const string DenyDnsUdpBase = RulePrefix + "DENY-DNS-UDP";
    private const string DenyDnsTcpBase = RulePrefix + "DENY-DNS-TCP";
    private const string DenyDotBase = RulePrefix + "DENY-DOT";

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
        foreach (var raw in policyHosts)
        {
            var h = NormalizeHost(raw);
            if (h is not null && !h.Equals(bootstrapHost, StringComparison.OrdinalIgnoreCase))
                hostSet.Add(h);
        }

        var ordered = new List<IPAddress>(bootstrapIps);
        var seen = new HashSet<IPAddress>(bootstrapIps);
        var otherIps = new List<IPAddress>();
        foreach (var h in hostSet)
        {
            var ips = Resolve(h);
            if (ips.Count > 0)
                hostIps.Add((h, ips));
            foreach (var ip in ips)
            {
                if (seen.Add(ip))
                    otherIps.Add(ip);
            }
        }
        otherIps.Sort((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));
        ordered.AddRange(otherIps);

        var overflow = false;
        if (ordered.Count > MaxAllowIps)
        {
            overflow = true;
            ordered = ordered.Take(MaxAllowIps).ToList();
        }

        var desired = BuildDesiredRules(ordered);
        var fp = string.Join("\n", desired.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key + "=" + kv.Value));

        lock (Gate)
        {
            if (gen != Interlocked.Read(ref _generation))
                return new Result(false, false, ordered.Count, overflow, "stale_generation");

            var now = DateTimeOffset.UtcNow;
            if (fp == _lastFingerprint && now - _lastVerifyUtc < VerifyEvery)
                return Finish(false, ordered.Count, overflow, "lock_fresh",
                    bootstrapUnresolved, hostIps, ordered);

            try
            {
                var existing = ListLockRules();
                var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

                if (fp == _lastFingerprint && desired.Keys.All(existingSet.Contains)
                    && existingSet.All(desired.ContainsKey))
                {
                    _lastVerifyUtc = now;
                    return Finish(false, ordered.Count, overflow, "lock_verified",
                        bootstrapUnresolved, hostIps, ordered);
                }

                var added = 0;
                var updated = 0;
                foreach (var (name, remoteIp) in desired)
                {
                    var (proto, port) = SpecFor(name);
                    var exists = existingSet.Contains(name);
                    if (exists && _lastRules.TryGetValue(name, out var prev) && prev == remoteIp)
                        continue;

                    (bool Ok, string Output) r;
                    if (exists)
                    {
                        r = DomainBlockEnforcer.RunNetsh(
                            $"advfirewall firewall set rule name=\"{name}\" new " +
                            $"remoteip={remoteIp} remoteport={port} protocol={proto} enable=yes");
                        updated++;
                    }
                    else
                    {
                        r = DomainBlockEnforcer.RunNetsh(
                            $"advfirewall firewall add rule name=\"{name}\" dir=out action=block " +
                            $"protocol={proto} remoteport={port} remoteip={remoteIp} enable=yes profile=any");
                        added++;
                    }
                    if (!r.Ok)
                        return Fail(r.Output, ordered.Count, overflow);
                }

                // Shrunk chunk count / obsolete rules go last so there is never a window
                // where the deny coverage is weaker than the previous generation.
                var removed = 0;
                foreach (var name in existing)
                {
                    if (desired.ContainsKey(name))
                        continue;
                    DomainBlockEnforcer.RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
                    removed++;
                }

                _lastRules = new Dictionary<string, string>(desired, StringComparer.OrdinalIgnoreCase);
                _lastFingerprint = fp;
                _lastVerifyUtc = now;
                _rulesMaybePresent = true;
                var changed = added > 0 || updated > 0 || removed > 0;
                return Finish(changed, ordered.Count, overflow,
                    $"lock_add={added} upd={updated} rm={removed} rules={desired.Count} ips={ordered.Count}",
                    bootstrapUnresolved, hostIps, ordered);
            }
            catch (Exception ex)
            {
                return new Result(false, false, ordered.Count, overflow, "lock_error:" + ex.Message);
            }
        }
    }

    /// <summary>Delete every <c>AlphaAI-DLP-LOCK-*</c> rule. Safe to call every tick.</summary>
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

            var now = DateTimeOffset.UtcNow;
            if (!_rulesMaybePresent && !hostsCleared && now - _cleanVerifiedUtc < CleanVerifyEvery)
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

    private static Dictionary<string, string> BuildDesiredRules(IReadOnlyList<IPAddress> allow)
    {
        var v4 = new List<(uint Lo, uint Hi)>();
        var v6 = new List<(UInt128 Lo, UInt128 Hi)>();

        // Never block LAN / loopback / link-local / multicast (intranet, printers, DHCP helpers).
        v4.Add(Cidr4("0.0.0.0", 8));
        v4.Add(Cidr4("10.0.0.0", 8));
        v4.Add(Cidr4("127.0.0.0", 8));
        v4.Add(Cidr4("169.254.0.0", 16));
        v4.Add(Cidr4("172.16.0.0", 12));
        v4.Add(Cidr4("192.168.0.0", 16));
        v4.Add(Cidr4("224.0.0.0", 3)); // multicast + reserved + broadcast
        v6.Add((UInt128.Zero, UInt128.One)); // :: and ::1
        v6.Add(Cidr6("fc00::", 7));
        v6.Add(Cidr6("fe80::", 10));
        v6.Add(Cidr6("ff00::", 8));

        foreach (var ip in allow)
        {
            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var n = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                v4.Add((n, n));
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var n = BinaryPrimitives.ReadUInt128BigEndian(bytes);
                v6.Add((n, n));
            }
        }

        var ranges = new List<string>();
        foreach (var (lo, hi) in Complement4(v4))
            ranges.Add(lo == hi ? Ip4(lo) : Ip4(lo) + "-" + Ip4(hi));
        foreach (var (lo, hi) in Complement6(v6))
            ranges.Add(lo == hi ? Ip6(lo) : Ip6(lo) + "-" + Ip6(hi));

        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var r in ranges)
        {
            if (sb.Length > 0 && sb.Length + 1 + r.Length > MaxRemoteIpChars)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(',');
            sb.Append(r);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());

        var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string baseName)
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                var suffix = i == 0 ? "" : "-" + i;
                rules[baseName + suffix] = chunks[i];
            }
        }
        // Web + QUIC, then public DNS and DNS-over-TLS. Same complement, so a CDN
        // name can resolve only when its host is pinned below for the allowlist.
        Add(DenyTcpBase);
        Add(DenyUdpBase);
        Add(DenyDnsUdpBase);
        Add(DenyDnsTcpBase);
        Add(DenyDotBase);
        return rules;
    }

    private static (string Proto, string Port) SpecFor(string name)
    {
        if (name.StartsWith(DenyDnsUdpBase, StringComparison.OrdinalIgnoreCase))
            return ("udp", "53");
        if (name.StartsWith(DenyDnsTcpBase, StringComparison.OrdinalIgnoreCase))
            return ("tcp", "53");
        if (name.StartsWith(DenyDotBase, StringComparison.OrdinalIgnoreCase))
            return ("tcp", "853");
        if (name.StartsWith(DenyUdpBase, StringComparison.OrdinalIgnoreCase))
            return ("udp", "80,443");
        return ("tcp", "80,443");
    }

    /// <summary>
    /// Rules are in place. Ready only when the server name resolved this pass and the
    /// allowlist hosts block was written. <paramref name="bootstrapUnresolved"/> keeps
    /// the gate on the error until a later tick resolves the name.
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
        var hosts = ApplyAllowHosts(hostIps);
        MaybeKillWeb(allowList, changed);
        var hostsOk = hosts is "hosts_allow_updated" or "hosts_allow_cleared"
            or "hosts_allow_fresh" or "hosts_allow_absent";
        var ready = !bootstrapUnresolved && hostsOk;
        var finalDetail = detail;
        if (bootstrapUnresolved)
            finalDetail = "bootstrap_unresolved";
        else if (!hostsOk)
            finalDetail = hosts;
        return new Result(changed, ready, allowIps, overflow, finalDetail, RulesPresent: true, HostsDetail: hosts);
    }

    private static IEnumerable<(uint Lo, uint Hi)> Complement4(List<(uint Lo, uint Hi)> allowed)
    {
        allowed.Sort((a, b) => a.Lo.CompareTo(b.Lo));
        ulong next = 0;
        foreach (var (lo, hi) in allowed)
        {
            if (lo > next)
                yield return ((uint)next, lo - 1);
            if ((ulong)hi + 1 > next)
                next = (ulong)hi + 1;
        }
        if (next <= uint.MaxValue)
            yield return ((uint)next, uint.MaxValue);
    }

    private static IEnumerable<(UInt128 Lo, UInt128 Hi)> Complement6(List<(UInt128 Lo, UInt128 Hi)> allowed)
    {
        allowed.Sort((a, b) => a.Lo.CompareTo(b.Lo));
        UInt128 next = UInt128.Zero;
        var exhausted = false;
        foreach (var (lo, hi) in allowed)
        {
            if (exhausted) break;
            if (lo > next)
                yield return (next, lo - UInt128.One);
            if (hi >= next)
            {
                if (hi == UInt128.MaxValue) exhausted = true;
                else next = hi + UInt128.One;
            }
        }
        if (!exhausted)
            yield return (next, UInt128.MaxValue);
    }

    private static (uint Lo, uint Hi) Cidr4(string addr, int prefix)
    {
        var n = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(addr).GetAddressBytes());
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (n & mask, (n & mask) | ~mask);
    }

    private static (UInt128 Lo, UInt128 Hi) Cidr6(string addr, int prefix)
    {
        var n = BinaryPrimitives.ReadUInt128BigEndian(IPAddress.Parse(addr).GetAddressBytes());
        var mask = prefix == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - prefix);
        return (n & mask, (n & mask) | ~mask);
    }

    private static string Ip4(uint n)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, n);
        return new IPAddress(b).ToString();
    }

    private static string Ip6(UInt128 n)
    {
        Span<byte> b = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(b, n);
        return new IPAddress(b).ToString();
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

    private static Result Fail(string output, int ips, bool overflow)
    {
        var detail = output.Contains("elevation", StringComparison.OrdinalIgnoreCase) ||
                     output.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
            ? "access_denied_need_admin"
            : "lock_error:" + output.Trim();
        return new Result(false, false, ips, overflow, detail);
    }
}
