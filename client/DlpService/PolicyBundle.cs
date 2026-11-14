using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlphaAITracker.Service;

internal sealed class PolicyBundle
{
    public long PolicyVersion { get; init; }
    public IReadOnlyList<PolicyRule> Rules { get; init; } = Array.Empty<PolicyRule>();
    public IReadOnlyList<string> ShadowDenyDomains { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Published lockdown flag. <c>null</c> / <c>true</c> = default-deny ON after login;
    /// <c>false</c> = admin published an explicit open (opt-out). Missing field = ON.
    /// </summary>
    public bool? Lockdown { get; init; }
    public IReadOnlyList<string> PlatformAllowHosts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowHosts { get; init; } = Array.Empty<string>();
    /// <summary>Published app allow entries (path / publisher / product / sha256).</summary>
    public IReadOnlyList<AllowAppEntry> AllowApps { get; init; } = Array.Empty<AllowAppEntry>();
    /// <summary>Published hardware/port control entries (block/allow/alert a class or instance).</summary>
    public IReadOnlyList<DeviceControlEntry> DeviceControl { get; init; } = Array.Empty<DeviceControlEntry>();

    /// <summary>True unless the published bundle explicitly sets <c>lockdown: false</c>.</summary>
    public bool LockdownEnabled => Lockdown != false;
}

internal sealed class DeviceControlEntry
{
    public string Kind { get; init; } = "";     // "class" | "instance"
    public string Value { get; init; } = "";     // pnp class / setup-class GUID, or instance id
    public string PnpClass { get; init; } = "";
    public string Action { get; init; } = "block"; // block | allow | alert
}

internal sealed class AllowAppEntry
{
    public string Publisher { get; init; } = "";
    public string Product { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Path { get; init; } = "";
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

        // Missing / absent lockdown = ON (fail-closed after login). Explicit false = admin opt-out.
        bool? lockdown = null;
        if (root.TryGetProperty("lockdown", out var ld))
        {
            if (ld.ValueKind == JsonValueKind.True) lockdown = true;
            else if (ld.ValueKind == JsonValueKind.False) lockdown = false;
        }
        var platformHosts = root.TryGetProperty("platformAllow", out var pa)
            ? ReadStringArray(pa, "hosts")
            : Array.Empty<string>();
        IReadOnlyList<string> allowHosts = Array.Empty<string>();
        IReadOnlyList<AllowAppEntry> allowApps = Array.Empty<AllowAppEntry>();
        if (root.TryGetProperty("allow", out var al2) && al2.ValueKind == JsonValueKind.Object)
        {
            allowHosts = ReadStringArray(al2, "hosts");
            allowApps = ReadAllowApps(al2);
        }

        var deviceControl = ReadDeviceControl(root);

        return new PolicyBundle
        {
            PolicyVersion = version,
            Rules = rules,
            ShadowDenyDomains = shadowDomains,
            Lockdown = lockdown,
            PlatformAllowHosts = platformHosts,
            AllowHosts = allowHosts,
            AllowApps = allowApps,
            DeviceControl = deviceControl,
        };
    }


    private static IReadOnlyList<DeviceControlEntry> ReadDeviceControl(JsonElement root)
    {
        if (!root.TryGetProperty("deviceControl", out var dc) || dc.ValueKind != JsonValueKind.Array)
            return Array.Empty<DeviceControlEntry>();
        var list = new List<DeviceControlEntry>();
        foreach (var el in dc.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var kind = (el.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "").Trim().ToLowerInvariant();
            var value = (el.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "").Trim();
            var cls = (el.TryGetProperty("pnpClass", out var c) ? c.GetString() ?? "" : "").Trim();
            var action = (el.TryGetProperty("action", out var a) ? a.GetString() ?? "block" : "block").Trim().ToLowerInvariant();
            if (value.Length == 0 || (kind != "class" && kind != "instance")) continue;
            if (action != "block" && action != "allow" && action != "alert") action = "block";
            list.Add(new DeviceControlEntry { Kind = kind, Value = value, PnpClass = cls, Action = action });
        }
        return list;
    }

    /// <summary>
    /// Instance ids the policy wants hard-blocked right now.
    /// Instance <c>allow</c> entries win over instance <c>block</c>.
    /// Net adapters are never hard-blocked (use Network Lockdown for internet).
    /// </summary>
    public static IReadOnlyList<string> BlockedInstances(PolicyBundle bundle)
    {
        var allowed = AllowedInstances(bundle);
        return bundle.DeviceControl
            .Where(e => e.Kind == "instance" && e.Action == "block" && !allowed.Contains(e.Value))
            .Where(e => !IsNetAdapterEntry(e))
            .Select(e => e.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// PnP class names / setup-class GUIDs to hard-block.
    /// Class <c>allow</c> removes that class from the deny set; instance allows are handled separately.
    /// Net class is never hard-blocked (use Network Lockdown for internet).
    /// </summary>
    public static IReadOnlyList<string> BlockedClasses(PolicyBundle bundle)
    {
        var allowedClasses = new HashSet<string>(
            bundle.DeviceControl
                .Where(e => e.Kind == "class" && e.Action == "allow")
                .Select(e => e.Value),
            StringComparer.OrdinalIgnoreCase);
        return bundle.DeviceControl
            .Where(e => e.Kind == "class" && e.Action == "block" && !allowedClasses.Contains(e.Value))
            .Where(e => !IsNetAdapterEntry(e))
            .Select(e => e.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True when a deviceControl entry targets a network adapter (class Net / Net GUID).
    /// Those must not be Disable-PnpDevice'd — that cuts the path to the management server.
    /// </summary>
    public static bool IsNetAdapterEntry(DeviceControlEntry e)
    {
        if (e.PnpClass.Equals("Net", StringComparison.OrdinalIgnoreCase)) return true;
        var v = (e.Value ?? "").Trim();
        if (v.Equals("Net", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Equals(PortDeviceEnforcer.NetClassGuid, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Instance ids exempt from class/instance blocks (allow beats block).</summary>
    public static IReadOnlyList<string> AllowedInstances(PolicyBundle bundle) =>
        bundle.DeviceControl
            .Where(e => e.Kind == "instance" && e.Action == "allow")
            .Select(e => e.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<AllowAppEntry> ReadAllowApps(JsonElement allow)
    {
        if (!allow.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
            return Array.Empty<AllowAppEntry>();
        var list = new List<AllowAppEntry>();
        foreach (var el in apps.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var publisher = el.TryGetProperty("publisher", out var p) ? p.GetString() ?? "" : "";
            var product = el.TryGetProperty("product", out var pr) ? pr.GetString() ?? "" : "";
            var sha = el.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "";
            var path = el.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "" : "";
            publisher = publisher.Trim();
            product = product.Trim();
            path = path.Trim();
            sha = sha.Trim().ToLowerInvariant().Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
            if (publisher.Length == 0 && path.Length == 0 && sha.Length != 64) continue;
            list.Add(new AllowAppEntry
            {
                Publisher = publisher,
                Product = product,
                Sha256 = sha,
                Path = path,
            });
        }
        return list;
    }

    /// <summary>True when any enabled USB trigger asks for block / alert_block.</summary>
    /// <summary>
    /// First enabled DLP rule with this trigger whose action blocks (DLP Rules →
    /// File Download / File Upload). Null when none — the browser policy is cleared.
    /// </summary>
    public static PolicyRule? BlockingRule(PolicyBundle bundle, string trigger) =>
        bundle.Rules.FirstOrDefault(r =>
            r.Enabled
            && r.Trigger == trigger
            && (r.Action is "block" or "alert_block"));

    public static bool WantsUsbStorageBlock(PolicyBundle bundle) =>
        bundle.Rules.Any(r =>
            r.Enabled
            && (r.Trigger is "usb_storage" or "usb_file")
            && (r.Action is "block" or "alert_block"))
        || WantsUsbStorageFromDeviceControl(bundle);

    /// <summary>
    /// Port-control "Block all" on USB / USB storage / WPD / DiskDrive should also
    /// flip the USBSTOR service (same durable lever as classic USB DLP rules).
    /// </summary>
    public static bool WantsUsbStorageFromDeviceControl(PolicyBundle bundle)
    {
        foreach (var e in bundle.DeviceControl)
        {
            if (e.Action != "block") continue;
            var v = (e.Value ?? "").Trim();
            var cls = (e.PnpClass ?? "").Trim();
            if (IsUsbStorClassToken(v) || IsUsbStorClassToken(cls))
                return true;
        }
        return false;
    }

    private static bool IsUsbStorClassToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (token.Equals("USB", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Equals("USBDevice", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Equals("WPD", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Equals("PortableDevice", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Equals("SCMDisk", StringComparison.OrdinalIgnoreCase)) return true;
        // Setup-class GUIDs
        if (token.Equals("{36fc9e60-c465-11cf-8056-444553540000}", StringComparison.OrdinalIgnoreCase)) return true; // USB
        if (token.Equals("{88bae032-5a81-49f0-bc3d-a4ff138216d6}", StringComparison.OrdinalIgnoreCase)) return true; // USBDevice
        if (token.Equals("{eec5ad98-8080-425f-922a-dabf3de3f69a}", StringComparison.OrdinalIgnoreCase)) return true; // WPD
        if (token.Equals("{4d36e967-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase)) return true; // DiskDrive
        if (token.Equals("{2accfe60-c130-11d2-b082-00a0c91fef85}", StringComparison.OrdinalIgnoreCase)) return true; // SCMDisk
        return false;
    }

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
