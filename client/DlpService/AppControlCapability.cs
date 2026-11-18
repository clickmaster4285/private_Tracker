using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AlphaAITracker.Service;

internal enum AppLockerChannel
{
    Unknown = 0,
    Local = 1,
    Mdm = 2,
    Gp = 3,
}

/// <summary>
/// Facts for the AppLocker capability probe. A null build, a failed hotfix query with no
/// pinned UBR floor, or an unknown channel must not be treated as interim enforcement.
/// </summary>
internal sealed record AppControlFacts(
    int? CurrentBuild,
    int? Ubr,
    bool HotfixQuerySucceeded,
    IReadOnlyCollection<string>? InstalledKbIds,
    AppLockerChannel Channel);

internal sealed record AppControlAssessment(bool CanClaimInterim, string Reason);

internal sealed record AppLockdownDecision(
    bool OverallEnforcing,
    bool ArmWatch,
    string AppLockerCode,
    string AppLockerDetail,
    string WdacCode,
    string WdacDetail);

/// <summary>
/// Runtime AppLocker capability. Does not hard-code "Pro = unsupported".
/// Windows 10 before build 19041 cannot enforce on Pro. KB 5024351's edition-check
/// removal is detected only by the exact KBs named in that article, or by a pinned
/// UBR floor for that CurrentBuild (a later cumulative moves UBR; a higher KB number
/// is not evidence). Windows 11 23H2+ (build 22631+) includes the change in the image.
/// Group Policy deployment on Pro is still not a supported management claim.
/// </summary>
internal static class AppControlCapability
{
    internal const int Win10MinBuild = 19041;
    internal const int Win11InboxBuild = 22631;

    /// <summary>
    /// Exact packages named by Microsoft KB5024351. Not a "newer KB id" heuristic.
    /// </summary>
    internal static readonly string[] ExactCapabilityKbs =
    [
        "KB5024351",
        "KB5017389",
        "KB5018482",
        "KB5018483",
    ];

    /// <summary>
    /// UBR floors from the LCUs KB5024351 documents. Same CurrentBuild and a higher
    /// UBR means that LCU or a superseding cumulative, which is the build number,
    /// not a KB name.
    /// 19042/19043/19044: KB5018482 (1904x.2193). 19045 is the 22H2 enablement on
    /// that same LCU family. 22000: KB5018483 (22000.1165). 22621: KB5017389 (22621.608).
    /// </summary>
    internal static readonly IReadOnlyDictionary<int, int> UbrFloor =
        new Dictionary<int, int>
        {
            [19042] = 2193,
            [19043] = 2193,
            [19044] = 2193,
            [19045] = 2193,
            [22000] = 1165,
            [22621] = 608,
        };

    private static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(10);
    private static readonly object ProbeGate = new();
    private static AppControlFacts? _cached;
    private static DateTimeOffset _cachedUtc = DateTimeOffset.MinValue;

    internal static AppControlAssessment Assess(AppControlFacts facts)
    {
        if (facts.CurrentBuild is null)
            return new AppControlAssessment(false, "build_unknown");
        var build = facts.CurrentBuild.Value;
        if (build < Win10MinBuild)
            return new AppControlAssessment(false, "build_too_old");

        if (facts.HotfixQuerySucceeded && facts.InstalledKbIds is not null)
        {
            foreach (var id in facts.InstalledKbIds)
            {
                if (IsExactCapabilityKb(id))
                    return new AppControlAssessment(true, "kb_exact");
            }
        }

        if (facts.Ubr is int ubr && MeetsUbrFloor(build, ubr))
            return new AppControlAssessment(true, "ubr_baseline");

        if (build >= Win11InboxBuild)
            return new AppControlAssessment(true, "win11_inbox");

        if (!facts.HotfixQuerySucceeded || facts.InstalledKbIds is null)
            return new AppControlAssessment(false, "kb_unknown");

        return new AppControlAssessment(false, "kb_absent");
    }

    internal static bool IsExactCapabilityKb(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        var token = id.Trim();
        if (!token.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
            token = "KB" + token;
        foreach (var known in ExactCapabilityKbs)
        {
            if (token.Equals(known, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static bool MeetsUbrFloor(int build, int ubr)
    {
        if (ubr < 0)
            return false;
        return UbrFloor.TryGetValue(build, out var floor) && ubr >= floor;
    }

    internal static AppLockerChannel ClassifyChannel(bool? mdmEnrolledWithCsp, bool? domainGpOwnsAppLocker)
    {
        if (mdmEnrolledWithCsp is null || domainGpOwnsAppLocker is null)
            return AppLockerChannel.Unknown;
        if (mdmEnrolledWithCsp == true)
            return AppLockerChannel.Mdm;
        if (domainGpOwnsAppLocker == true)
            return AppLockerChannel.Gp;
        return AppLockerChannel.Local;
    }

    internal static AppLockdownDecision Decide(
        bool production,
        bool isPro,
        bool hasOrgCip,
        bool? secureBoot,
        bool cipFingerprintMatch,
        bool canaryDenied,
        bool canaryThrew,
        AppControlAssessment assessment,
        AppLockerChannel channel,
        bool blockRulesAttested,
        bool acceptAppLockerInterim = false)
    {
        var tamperResistant = cipFingerprintMatch && secureBoot == true;
        var secureBootRecovery = production && hasOrgCip && secureBoot != true;
        var proNeedsCip = production && isPro && !hasOrgCip;
        var blocks = blockRulesAttested
            ? "wdac_block_rules=verified"
            : "wdac_block_rules=unverified";

        var applockerEnforcing = false;
        var appCode = "failed";
        var appDetail = "verify_not_enforcing;" + assessment.Reason;

        if (canaryThrew)
        {
            applockerEnforcing = false;
            appCode = "failed";
            appDetail = "canary_probe_failed";
        }
        else if (isPro && channel == AppLockerChannel.Gp && !canaryDenied)
        {
            applockerEnforcing = false;
            appCode = "failed";
            appDetail = "applocker_gp_unsupported_on_pro";
        }
        else if (channel == AppLockerChannel.Unknown)
        {
            applockerEnforcing = false;
            appCode = "failed";
            appDetail = "channel_unknown";
        }
        else if (isPro && !assessment.CanClaimInterim && !hasOrgCip)
        {
            applockerEnforcing = false;
            appCode = "failed";
            appDetail = "no_reliable_app_control;" + assessment.Reason;
        }
        else if (canaryDenied && (!isPro || assessment.CanClaimInterim || hasOrgCip))
        {
            applockerEnforcing = true;
            var interim = isPro && !hasOrgCip;
            appCode = interim ? "interim" : "ok";
            appDetail = interim
                ? "applocker_interim;" + assessment.Reason + ";channel=" + channel.ToString().ToLowerInvariant()
                : "verify=enforcing;channel=" + channel.ToString().ToLowerInvariant();
            if (isPro && channel == AppLockerChannel.Gp)
                appDetail += ";gp_deployment_unsupported";
        }

        // Deployments that accept AppLocker as the enforcement level (machines that cannot
        // run a signed WDAC policy — Windows Home, or Pro without Secure Boot / code-signing,
        // e.g. VMs) let a genuinely-enforcing AppLocker satisfy the gate. WDAC stays a
        // best-effort hardening (reported "off"), never a hard requirement that can never be met.
        var appLockerSatisfies = acceptAppLockerInterim && applockerEnforcing;

        string wdacCode;
        string wdacDetail;
        if (secureBootRecovery && !appLockerSatisfies)
        {
            wdacCode = "recovery_required";
            wdacDetail = "wdac_secure_boot_off;" + blocks;
        }
        else if (tamperResistant)
        {
            wdacCode = "ok";
            wdacDetail = "wdac_enforcing;" + blocks;
        }
        else if (appLockerSatisfies)
        {
            // AppLocker accepted as the enforcement level — WDAC not available on this
            // machine, so it is best-effort, not a gate failure.
            wdacCode = "off";
            wdacDetail = "wdac_best_effort_applocker_accepted;" + blocks;
        }
        else if (proNeedsCip)
        {
            wdacCode = "failed";
            wdacDetail = "wdac_cip_required_pro;" + blocks;
        }
        else if (hasOrgCip)
        {
            wdacCode = "failed";
            wdacDetail = "wdac_not_enforcing;" + blocks;
        }
        else
        {
            wdacCode = "off";
            wdacDetail = "wdac_unsigned_pending;" + blocks;
        }

        var overall = (applockerEnforcing || tamperResistant)
            && (appLockerSatisfies || (!proNeedsCip && !secureBootRecovery));
        if (overall)
        {
            appCode = "ok";
            if (canaryThrew)
                appDetail = "canary_probe_failed;wdac_enforcing;" + blocks;
            else if (!canaryDenied)
                appDetail = "wdac_enforcing;" + appDetail;
        }

        var armWatch = !(applockerEnforcing || tamperResistant);
        return new AppLockdownDecision(overall, armWatch, appCode, appDetail, wdacCode, wdacDetail);
    }

    internal static AppControlFacts Probe()
    {
        lock (ProbeGate)
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedUtc < ProbeTtl)
                return _cached;
        }

        var facts = ReadFacts();
        lock (ProbeGate)
        {
            _cached = facts;
            _cachedUtc = DateTimeOffset.UtcNow;
        }
        return facts;
    }

    private static AppControlFacts ReadFacts()
    {
        int? build = null;
        int? ubr = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var buildRaw = (key?.GetValue("CurrentBuildNumber") as string)
                           ?? (key?.GetValue("CurrentBuild") as string);
            if (int.TryParse(buildRaw, out var b))
                build = b;
            if (key?.GetValue("UBR") is int u)
                ubr = u;
        }
        catch
        {
            build = null;
            ubr = null;
        }

        var (hotfixOk, kbs) = QueryHotfixes();
        var channel = ProbeChannel();
        return new AppControlFacts(build, ubr, hotfixOk, kbs, channel);
    }

    private static AppLockerChannel ProbeChannel()
    {
        if (!OperatingSystem.IsWindows())
            return AppLockerChannel.Unknown;
        bool? mdm;
        bool? gp;
        try
        {
            mdm = IsMdmEnrolled() && HasAppLockerCsp();
        }
        catch
        {
            mdm = null;
        }
        try
        {
            var joined = IsDomainJoined();
            gp = joined is null ? null : joined.Value && GpHistoryOwnsAppLocker();
        }
        catch
        {
            gp = null;
        }
        return ClassifyChannel(mdm, gp);
    }

    private static bool? IsDomainJoined()
    {
        var status = NetGetJoinInformation(null, out var buffer, out var join);
        try
        {
            if (status != 0)
                return null;
            // 3 = NetSetupDomainName. 1 unjoined, 2 workgroup.
            return join == 3;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                NetApiBufferFree(buffer);
        }
    }

    private static bool IsMdmEnrolled()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
        if (root is null)
            return false;
        foreach (var name in root.GetSubKeyNames())
        {
            using var sub = root.OpenSubKey(name);
            if (sub?.GetValue("EnrollmentState") is int state && state == 1)
                return true;
        }
        return false;
    }

    private static bool HasAppLockerCsp()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\PolicyManager\current\device\AppLocker");
        return key is not null;
    }

    private static bool GpHistoryOwnsAppLocker()
    {
        string? guid = null;
        using (var extensions = Registry.LocalMachine.OpenSubKey(
                   @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\GPExtensions"))
        {
            if (extensions is null)
                return false;
            foreach (var name in extensions.GetSubKeyNames())
            {
                using var sub = extensions.OpenSubKey(name);
                var display = sub?.GetValue("DisplayName") as string ?? "";
                if (display.Equals("AppLocker", StringComparison.OrdinalIgnoreCase))
                {
                    guid = name;
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(guid))
            return false;
        using var history = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\History\" + guid);
        if (history is not null)
            return true;
        using var state = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State\Machine\Extension-List\" + guid);
        return state is not null;
    }

    private static (bool Ok, IReadOnlyCollection<string>? Ids) QueryHotfixes()
    {
        if (!OperatingSystem.IsWindows())
            return (false, null);
        try
        {
            var ps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(ps))
                return (false, null);
            using var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ps,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Get-HotFix | Select-Object -ExpandProperty HotFixID\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            p.Start();
            var stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, null);
            }
            if (p.ExitCode != 0)
                return (false, null);
            var ids = new List<string>();
            foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
                    ids.Add(line);
            }
            return (true, ids);
        }
        catch
        {
            return (false, null);
        }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr nameBuffer, out int joinStatus);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
