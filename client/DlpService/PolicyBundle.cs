using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlphaAITracker.Service;

internal sealed class PolicyBundle
{
    public long PolicyVersion { get; init; }
    public IReadOnlyList<PolicyRule> Rules { get; init; } = Array.Empty<PolicyRule>();
    public IReadOnlyList<string> ShadowDenyDomains { get; init; } = Array.Empty<string>();
}

internal sealed class PolicyRule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Trigger { get; init; } = "";
    public string Action { get; init; } = "alert";
    public string Severity { get; init; } = "medium";
    public bool Alert { get; init; } = true;
    public IReadOnlyList<string> Domains { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    public RuleSchedule? Schedule { get; init; }
}

internal sealed class RuleSchedule
{
    public bool Enabled { get; init; }
    public IReadOnlyList<string> Days { get; init; } = Array.Empty<string>();
    public string Start { get; init; } = "";
    public string End { get; init; } = "";
    public string Timezone { get; init; } = "UTC";
}

internal static class PolicyBundleParser
{
    private static readonly Regex HostLike = new(
        @"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]*[a-z0-9])?)*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static PolicyBundle Parse(string bundleJson)
    {
        if (string.IsNullOrWhiteSpace(bundleJson))
            return new PolicyBundle();

        using var doc = JsonDocument.Parse(bundleJson);
        var root = doc.RootElement;
        var version = root.TryGetProperty("policyVersion", out var pv) ? pv.GetInt64() : 0;
        var rules = new List<PolicyRule>();
        if (root.TryGetProperty("rules", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var match = el.TryGetProperty("match", out var m) ? m : default;
                rules.Add(new PolicyRule
                {
                    Id = el.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Enabled = !el.TryGetProperty("enabled", out var en) || en.GetBoolean(),
                    Trigger = el.TryGetProperty("trigger", out var tr) ? tr.GetString() ?? "" : "",
                    Action = el.TryGetProperty("action", out var ac) ? ac.GetString() ?? "alert" : "alert",
                    Severity = el.TryGetProperty("severity", out var sev) ? sev.GetString() ?? "medium" : "medium",
                    Alert = !el.TryGetProperty("alert", out var al) || al.GetBoolean(),
                    Domains = ReadStringArray(match, "domains"),
                    Paths = ReadStringArray(match, "paths"),
                    Schedule = ReadSchedule(match),
                });
            }
        }

        var shadowDomains = Array.Empty<string>();
        if (root.TryGetProperty("shadowDeny", out var sd) &&
            sd.ValueKind == JsonValueKind.Object &&
            sd.TryGetProperty("domains", out var sdd))
        {
            shadowDomains = ReadStringArrayElement(sdd).ToArray();
        }

        return new PolicyBundle
        {
            PolicyVersion = version,
            Rules = rules,
            ShadowDenyDomains = shadowDomains,
        };
    }

    /// <summary>True when any enabled USB trigger asks for block / alert_block.</summary>
    public static bool WantsUsbStorageBlock(PolicyBundle bundle) =>
        bundle.Rules.Any(r =>
            r.Enabled
            && (r.Trigger is "usb_storage" or "usb_file")
            && (r.Action is "block" or "alert_block"));

    public static PolicyRule? FirstUsbBlockRule(PolicyBundle bundle) =>
        bundle.Rules.FirstOrDefault(r =>
            r.Enabled
            && (r.Trigger is "usb_storage" or "usb_file")
            && (r.Action is "block" or "alert_block"));

    /// <summary>
    /// Domains that should be hard-blocked right now (web_navigate block/alert_block +
    /// shadowDeny), respecting optional per-rule schedules.
    /// </summary>
    public static IReadOnlyList<string> ActiveWebBlockDomains(PolicyBundle bundle, DateTimeOffset utcNow)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in bundle.Rules)
        {
            if (!r.Enabled || r.Trigger != "web_navigate")
                continue;
            if (r.Action is not ("block" or "alert_block"))
                continue;
            if (!IsScheduleActive(r.Schedule, utcNow))
                continue;
            foreach (var d in r.Domains)
            {
                foreach (var n in NormalizeDomain(d))
                    set.Add(n);
            }
        }

        foreach (var d in bundle.ShadowDenyDomains)
        {
            foreach (var n in NormalizeDomain(d))
                set.Add(n);
        }

        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static PolicyRule? FirstWebBlockRule(PolicyBundle bundle, DateTimeOffset utcNow) =>
        bundle.Rules.FirstOrDefault(r =>
            r.Enabled
            && r.Trigger == "web_navigate"
            && (r.Action is "block" or "alert_block")
            && IsScheduleActive(r.Schedule, utcNow)
            && r.Domains.Count > 0);

    /// <summary>
    /// Stable fingerprint of the currently active domain set (for EnforceWorker change detection).
    /// </summary>
    public static string WebBlockFingerprint(IReadOnlyList<string> domains) =>
        string.Join("|", domains);

    public static bool IsScheduleActive(RuleSchedule? schedule, DateTimeOffset utcNow)
    {
        if (schedule == null || !schedule.Enabled)
            return true;

        DateTime local;
        try
        {
            var tz = ResolveTimeZone(schedule.Timezone);
            local = TimeZoneInfo.ConvertTime(utcNow, tz).DateTime;
        }
        catch
        {
            local = utcNow.UtcDateTime;
        }

        if (schedule.Days.Count > 0)
        {
            var dayKey = local.DayOfWeek switch
            {
                DayOfWeek.Monday => "mon",
                DayOfWeek.Tuesday => "tue",
                DayOfWeek.Wednesday => "wed",
                DayOfWeek.Thursday => "thu",
                DayOfWeek.Friday => "fri",
                DayOfWeek.Saturday => "sat",
                _ => "sun",
            };
            if (!schedule.Days.Any(d =>
                    string.Equals(d, dayKey, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (!TryParseHm(schedule.Start, out var start) || !TryParseHm(schedule.End, out var end))
            return true;

        var tod = local.TimeOfDay;
        if (end <= start)
        {
            // Overnight window e.g. 22:00–06:00
            return tod >= start || tod < end;
        }
        return tod >= start && tod < end;
    }

    /// <summary>
    /// Normalize a domain or *.domain pattern into hosts-file sink entries (exact hosts only).
    /// </summary>
    public static IEnumerable<string> NormalizeDomain(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        var s = raw.Trim().ToLowerInvariant();
        s = s.Replace("https://", "", StringComparison.Ordinal)
             .Replace("http://", "", StringComparison.Ordinal);
        var slash = s.IndexOf('/');
        if (slash >= 0)
            s = s[..slash];
        var colon = s.IndexOf(':');
        if (colon >= 0)
            s = s[..colon];
        s = s.Trim('.');
        if (s.Length == 0)
            yield break;

        var wildcard = s.StartsWith("*.", StringComparison.Ordinal);
        if (wildcard)
            s = s[2..];

        if (!HostLike.IsMatch(s))
            yield break;

        yield return s;
        if (!s.StartsWith("www.", StringComparison.Ordinal))
            yield return "www." + s;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();
        if (!parent.TryGetProperty(name, out var el))
            return Array.Empty<string>();
        return ReadStringArrayElement(el);
    }

    private static IReadOnlyList<string> ReadStringArrayElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var v = item.GetString();
                if (!string.IsNullOrWhiteSpace(v))
                    list.Add(v.Trim());
            }
        }
        return list;
    }

    private static RuleSchedule? ReadSchedule(JsonElement match)
    {
        if (match.ValueKind != JsonValueKind.Object)
            return null;
        if (!match.TryGetProperty("schedule", out var s) || s.ValueKind != JsonValueKind.Object)
            return null;

        var enabled = s.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
        return new RuleSchedule
        {
            Enabled = enabled,
            Days = ReadStringArrayElement(
                s.TryGetProperty("days", out var days) ? days : default),
            Start = s.TryGetProperty("start", out var st) ? st.GetString() ?? "" : "",
            End = s.TryGetProperty("end", out var ed) ? ed.GetString() ?? "" : "",
            Timezone = s.TryGetProperty("timezone", out var tz) ? tz.GetString() ?? "UTC" : "UTC",
        };
    }

    private static bool TryParseHm(string raw, out TimeSpan ts)
    {
        ts = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        return TimeSpan.TryParseExact(
                   raw.Trim(),
                   new[] { @"hh\:mm", @"h\:mm" },
                   CultureInfo.InvariantCulture,
                   out ts)
               || TimeSpan.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out ts);
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
