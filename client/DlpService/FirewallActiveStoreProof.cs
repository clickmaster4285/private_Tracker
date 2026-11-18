using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Parses Windows Firewall ActiveStore observations. Enforcing requires every profile
/// to exist, be enabled, and have DefaultOutboundAction=Block, plus an explicit UDP 443
/// block. Product UDP-443 punch-through Allows (AlphaAI-DLP-LOCK-* / DEST-UDP leftovers)
/// fail the gate. Stock Store/shell app Allows with UDP/Any are not treated as a
/// product punch-through — they are residual OS noise under default Block.
/// Local/persistent Block with an active Allow is "active store disagreed".
/// </summary>
internal static class FirewallActiveStoreProof
{
    internal const string DisagreeDetail = "active store disagreed";
    internal const string DisabledDetail = "active_store_profile_disabled";
    internal const string MissingDetail = "active_store_profile_missing";
    internal const string QueryFailedDetail = "active_store_query_failed";
    internal const string NotBlockDetail = "default_outbound_not_block";
    internal const string QuicMissingDetail = "quic_rule_missing";
    internal const string Udp443AllowDetail = "udp_443_allow_present";
    internal const string OkDetail = "active_store_block;quic=block";
    internal const string JsonMarker = "ALPHA_ACTIVE_STORE_JSON:";

    internal sealed record ProfileRow(string Name, bool Enabled, bool OutboundBlock);
    internal sealed record AllowRow(string Name, string Protocol, string RemotePort);
    internal sealed record QuicRow(
        bool Present,
        bool Enabled,
        string Action,
        string Direction,
        string Protocol,
        string RemotePort);

    internal sealed record Verdict(
        bool Ok,
        bool OutboundBlocked,
        bool QuicBlockPresent,
        IReadOnlyList<string> Udp443AllowNames,
        string Detail);

    private static readonly string[] RequiredProfiles = ["Domain", "Private", "Public"];

    internal static bool ProtocolIsUdpOrAny(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            return true;
        var p = protocol.Trim();
        return p.Equals("UDP", StringComparison.OrdinalIgnoreCase)
               || p.Equals("Any", StringComparison.OrdinalIgnoreCase)
               || p == "*"
               || p == "17"
               || p == "256";
    }

    /// <summary>True when an outbound allow would pass UDP destination port 443.</summary>
    internal static bool AllowCoversUdp443(string? protocol, string? remotePort)
    {
        if (!ProtocolIsUdpOrAny(protocol))
            return false;
        return PortSpecIncludes443(remotePort);
    }

    /// <summary>
    /// Product / global punch-through only. Windows Store "UDP Any" app rules are ignored.
    /// </summary>
    internal static bool IsProductUdp443PunchThrough(string? name, string? protocol, string? remotePort)
    {
        if (!AllowCoversUdp443(protocol, remotePort))
            return false;
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var n = name.Trim();
        if (n.Equals("Block QUIC", StringComparison.OrdinalIgnoreCase)
            || n.Equals("AlphaAI-DLP-LOCK-BLOCK-QUIC", StringComparison.OrdinalIgnoreCase))
            return false;
        if (n.Contains("ALLOW-DEST-UDP", StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.StartsWith("AlphaAI-DLP-LOCK-", StringComparison.OrdinalIgnoreCase)
            && AllowCoversUdp443(protocol, remotePort)
            && ProtocolIsUdpOrAny(protocol))
            return true;
        return false;
    }

    internal static bool PortSpecIncludes443(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return true;
        foreach (var part in spec.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Any", StringComparison.OrdinalIgnoreCase) || part is "*" or "-")
                return true;
            if (part == "443")
                return true;
            var dash = part.IndexOf('-');
            if (dash > 0
                && int.TryParse(part[..dash], out var lo)
                && int.TryParse(part[(dash + 1)..], out var hi)
                && lo <= 443 && 443 <= hi)
                return true;
        }
        return false;
    }

    internal static bool QuicRuleBlocksUdp443(QuicRow? quic)
    {
        if (quic is not { Present: true, Enabled: true })
            return false;
        if (!quic.Action.Equals("Block", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!quic.Direction.Equals("Outbound", StringComparison.OrdinalIgnoreCase))
            return false;
        return AllowCoversUdp443(quic.Protocol, quic.RemotePort);
    }

    internal static Verdict QueryFailed(string? reason)
    {
        var r = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        if (r.Length > 160)
            r = r[..160];
        return Failed(QueryFailedDetail + ":" + r, false, []);
    }

    internal static Verdict Evaluate(
        bool querySucceeded,
        IReadOnlyList<ProfileRow>? active,
        IReadOnlyList<ProfileRow>? persistent,
        QuicRow? quic,
        IReadOnlyList<AllowRow>? outboundAllows)
    {
        if (!querySucceeded || active is null)
            return QueryFailed("no_payload");

        var persistentRows = persistent ?? [];
        foreach (var name in RequiredProfiles)
        {
            var row = FindProfile(active, name);
            if (row is null)
                return Failed(MissingDetail + ":" + name, false, []);
            if (!row.Enabled)
                return Failed(DisabledDetail + ":" + name, false, []);
            if (!row.OutboundBlock)
            {
                var local = FindProfile(persistentRows, name);
                var detail = local is { Enabled: true, OutboundBlock: true }
                    ? DisagreeDetail
                    : NotBlockDetail + ":" + name;
                return Failed(detail, false, []);
            }
        }

        var quicOk = QuicRuleBlocksUdp443(quic);
        var allowNames = new List<string>();
        if (outboundAllows is not null)
        {
            foreach (var allow in outboundAllows)
            {
                if (IsProductUdp443PunchThrough(allow.Name, allow.Protocol, allow.RemotePort))
                    allowNames.Add(string.IsNullOrWhiteSpace(allow.Name) ? "(unnamed)" : allow.Name);
            }
        }

        if (!quicOk)
            return Failed(QuicMissingDetail, false, allowNames);
        if (allowNames.Count > 0)
            return Failed(Udp443AllowDetail + ":" + allowNames[0], true, allowNames);

        return new Verdict(true, true, true, allowNames, OkDetail);
    }

    internal static string? ExtractJson(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith(JsonMarker, StringComparison.Ordinal))
                return line[JsonMarker.Length..].Trim();
        }
        // Fallback: first line that looks like a JSON object.
        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith('{') && line.Contains("\"active\"", StringComparison.Ordinal))
                return line;
        }
        var trimmed = stdout.Trim();
        if (trimmed.StartsWith('{') && trimmed.Contains("\"active\"", StringComparison.Ordinal))
            return trimmed;
        return null;
    }

    internal static Verdict Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return QueryFailed("empty_json");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var active = ReadProfiles(root, "active");
            var persistent = ReadProfiles(root, "persistent");
            var quic = ReadQuic(root);
            var allows = ReadAllows(root, "allows");
            return Evaluate(true, active, persistent, quic, allows);
        }
        catch (JsonException)
        {
            return QueryFailed("json_parse");
        }
    }

    private static Verdict Failed(string detail, bool quicPresent, IReadOnlyList<string> allows) =>
        new(false, false, quicPresent, allows, detail);

    private static ProfileRow? FindProfile(IReadOnlyList<ProfileRow> rows, string name)
    {
        foreach (var row in rows)
        {
            if (row.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return row;
        }
        return null;
    }

    private static List<ProfileRow> ReadProfiles(JsonElement root, string property)
    {
        var list = new List<ProfileRow>();
        if (!root.TryGetProperty(property, out var node) || node.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return list;
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (TryProfile(node, out var one))
                list.Add(one);
            return list;
        }
        if (node.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in node.EnumerateArray())
        {
            if (TryProfile(item, out var row))
                list.Add(row);
        }
        return list;
    }

    private static bool TryProfile(JsonElement item, out ProfileRow row)
    {
        row = new ProfileRow("", false, false);
        if (item.ValueKind != JsonValueKind.Object)
            return false;
        var name = ReadString(item, "Name");
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var enabled = ReadBool(item, "Enabled");
        var action = ReadString(item, "DefaultOutboundAction");
        var block = action.Equals("Block", StringComparison.OrdinalIgnoreCase) || action == "4";
        row = new ProfileRow(name, enabled, block);
        return true;
    }

    private static QuicRow ReadQuic(JsonElement root)
    {
        if (!root.TryGetProperty("quic", out var q) || q.ValueKind != JsonValueKind.Object)
            return new QuicRow(false, false, "", "", "", "");
        return new QuicRow(
            ReadBool(q, "present"),
            ReadBool(q, "enabled"),
            ReadString(q, "action"),
            ReadString(q, "direction"),
            ReadString(q, "protocol"),
            ReadString(q, "remotePort"));
    }

    private static List<AllowRow> ReadAllows(JsonElement root, string property)
    {
        var list = new List<AllowRow>();
        if (!root.TryGetProperty(property, out var node) || node.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return list;
        if (node.ValueKind == JsonValueKind.Object)
        {
            list.Add(ReadAllow(node));
            return list;
        }
        if (node.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                list.Add(ReadAllow(item));
        }
        return list;
    }

    private static AllowRow ReadAllow(JsonElement item) =>
        new(ReadString(item, "name"), ReadString(item, "protocol"), ReadString(item, "remotePort"));

    private static string ReadString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var v))
            return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => "",
        };
    }

    private static bool ReadBool(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var v))
            return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
            JsonValueKind.String => v.GetString() is "1" or "True" or "true",
            _ => false,
        };
    }
}
