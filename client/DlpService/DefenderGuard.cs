using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace AlphaAITracker.Service;

/// <summary>
/// 24/7 antivirus guard (Windows). The OS antivirus engine — Microsoft Defender — does the
/// on-access scanning (every file is scanned when it is opened / run / downloaded / copied
/// and blocked + quarantined when malicious). This class keeps that engine ON at maximum
/// strength and reports what it finds:
/// <list type="bullet">
/// <item><see cref="ReadStatus"/> — engine health via Defender's WMI provider
/// (MSFT_MpComputerStatus / MSFT_MpPreference) + Security Center (third-party AV).
/// All Defender reads/writes go through <see cref="DefenderWmi"/> — no encoded PowerShell,
/// which Defender flags as Trojan:Win32/PowExcEnv.</item>
/// <item><see cref="Enforce"/> — reverts every weakening change: real-time / behaviour /
/// download / script scanning off, cloud protection lowered, PUA off, network protection
/// off, threat action set to Allow / NoAction, unauthorized exclusions, per-threat allow
/// overrides, and the Group Policy registry values that turn Defender off.</item>
/// <item><see cref="ReadEvents"/> — Defender Operational log: threats detected / acted on
/// and protection-turned-off events.</item>
/// </list>
/// Never weakens Defender; never touches a third-party AV. With Tamper Protection ON some
/// changes are refused by Windows itself — the guard then reports the drift instead of looping.
/// </summary>
internal static class DefenderGuard
{
    private const string DefenderLog = "Microsoft-Windows-Windows Defender/Operational";
    private const string PolicyRoot = @"SOFTWARE\Policies\Microsoft\Windows Defender";

    public sealed record AvStatus(
        bool Available,
        string ProductName,
        string RunningMode,
        bool ServiceEnabled,
        bool AntivirusEnabled,
        bool RealTimeEnabled,
        bool BehaviorMonitorEnabled,
        bool OnAccessEnabled,
        bool IoavEnabled,
        bool TamperProtected,
        bool CloudEnabled,
        bool PuaEnabled,
        bool NetworkProtectionEnabled,
        string SignatureVersion,
        int SignatureAgeDays,
        DateTimeOffset? SignatureUpdatedAt,
        DateTimeOffset? QuickScanAt,
        DateTimeOffset? FullScanAt,
        string EngineVersion,
        IReadOnlyList<string> ThirdPartyProducts,
        int ExclusionCount,
        // Advanced hardening / threat state (Phase 2).
        int AsrConfigured,
        int AsrBlock,
        string ControlledFolderAccess,  // off | audit | block
        string NetworkProtectionMode,   // off | audit | block
        bool SmartScreenEnabled,
        bool MemoryIntegrityEnabled,
        bool CredentialGuardEnabled,
        bool EmailScanEnabled,
        bool ExploitProtectionEnabled,
        int QuarantinedThreatCount,
        string Error);

    public sealed record EnforceResult(
        IReadOnlyList<string> Changed,
        IReadOnlyList<string> RemainingDrift,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Hardened);

    /// <summary>
    /// Advanced-hardening switches applied alongside the real-time enforcement
    /// (ASR / Controlled Folder Access / scan tuning / quarantine retention).
    /// </summary>
    public sealed record HardeningOptions(
        bool Enabled,
        bool AsrAudit,
        bool ControlledFolderAccess,
        bool CfaAudit,
        int ScanCpuLimit,
        int QuarantinePurgeDays)
    {
        public static readonly HardeningOptions Off = new(false, false, false, false, 0, 0);
    }

    /// <summary>
    /// Attack Surface Reduction rule GUIDs the guard enforces (Microsoft's documented,
    /// stable rule ids). Short code = first GUID segment, used in audit/drift strings.
    /// </summary>
    private static readonly string[] AsrRuleIds =
    [
        "be9ba2d9-53ea-4cdc-84e5-9b1eeee46550", // Block executable content from email/webmail
        "d4f940ab-401b-4efc-aadc-ad5f3c50688a", // Block Office apps creating child processes
        "3b576869-a4ec-4529-8536-b80a7769e899", // Block Office apps creating executable content
        "75668c1f-73b5-4cf0-bb93-3ecf5cb7cc84", // Block Office apps injecting code into other processes
        "d3e037e1-3eb8-44c8-a917-57927947596d", // Block JS/VBScript launching downloaded executables
        "5beb7efe-fd9a-4556-801d-275e5ffc04cc", // Block execution of obfuscated scripts
        "92e97fa1-2edf-4476-bdd6-9dd0b4dddc7b", // Block Win32 API calls from Office macros
        "9e6c4e1f-7d60-472f-ba1a-a39ef669e4b2", // Block credential stealing from LSASS
        "d1e49aac-8f56-4280-b9ba-993a6d77406c", // Block process creations from PSExec and WMI
        "b2b3f03d-6a65-4f7b-a9c7-1c7ef74a9ba4", // Block untrusted/unsigned processes from USB
        "c1db55ab-c21a-4637-bb3f-a12568109d35", // Advanced ransomware protection
        "26190899-1602-49e8-8b27-eb1d0a1ce869", // Block Office comms app creating child processes
        "7674ba52-37eb-4a4f-a9a1-f0f9a1619a2c", // Block Adobe Reader creating child processes
        "e6db77e5-3df2-4cf1-b95a-636979351e5b", // Block persistence via WMI event subscription
        "56a863a9-875e-4185-98a7-b882c64b5ce5", // Block abuse of vulnerable signed drivers
        "01443614-cd74-433a-b99e-2ecdc07bfc25", // Block untrusted/rare executables (prevalence)
    ];

    public sealed record DefenderEvent(
        long RecordId,
        int EventId,
        DateTimeOffset At,
        Dictionary<string, string> Data);

    // ── Status ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Engine status through Defender's own WMI provider (MSFT_MpComputerStatus /
    /// MSFT_MpPreference / MSFT_MpThreat) plus Device Guard, Security Center and registry —
    /// the same fields the former PowerShell probe returned, without spawning PowerShell.
    /// </summary>
    public static AvStatus ReadStatus()
    {
        if (!OperatingSystem.IsWindows())
            return Empty("unsupported_os");

        var third = ReadThirdPartyAv();
        var memoryIntegrity = false;
        var credentialGuard = false;
        try
        {
            foreach (var dg in DefenderWmi.Query(@"\.
oot\Microsoft\Windows\DeviceGuard",
                         "SELECT SecurityServicesRunning FROM Win32_DeviceGuard"))
            {
                using (dg)
                {
                    var running = DefenderWmi.Longs(dg, "SecurityServicesRunning");
                    memoryIntegrity |= running.Contains(2);
                    credentialGuard |= running.Contains(1);
                }
            }
        }
        catch { /* Device Guard not present */ }

        var smartScreen = false;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer");
            var v = k?.GetValue("SmartScreenEnabled") as string;
            smartScreen = !string.IsNullOrEmpty(v) && !v.Equals("Off", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* ignore */ }

        var exploit = ReadSystemDepOn();

        try
        {
            using var s = DefenderWmi.ReadComputerStatus();
            if (s is null)
                return Empty("defender_status_unavailable") with
                {
                    ProductName = third.FirstOrDefault() ?? "",
                    ThirdPartyProducts = third,
                    MemoryIntegrityEnabled = memoryIntegrity,
                    CredentialGuardEnabled = credentialGuard,
                    SmartScreenEnabled = smartScreen,
                    ExploitProtectionEnabled = exploit,
                };
            var p = DefenderWmi.ReadPrefs();
            var quarantined = 0;
            try { quarantined = DefenderWmi.CountThreats(); } catch { /* best-effort */ }
            var asrConfigured = p.AsrRuleActions.Count(a => a is 1 or 2);
            var asrBlock = p.AsrRuleActions.Count(a => a == 1);
            static string Mode(int v) => v switch { 1 => "block", 2 => "audit", _ => "off" };

            return new AvStatus(
                Available: true,
                ProductName: "Microsoft Defender Antivirus",
                RunningMode: DefenderWmi.S(s, "AMRunningMode"),
                ServiceEnabled: DefenderWmi.B(s, "AMServiceEnabled"),
                AntivirusEnabled: DefenderWmi.B(s, "AntivirusEnabled"),
                RealTimeEnabled: DefenderWmi.B(s, "RealTimeProtectionEnabled"),
                BehaviorMonitorEnabled: DefenderWmi.B(s, "BehaviorMonitorEnabled"),
                OnAccessEnabled: DefenderWmi.B(s, "OnAccessProtectionEnabled"),
                IoavEnabled: DefenderWmi.B(s, "IoavProtectionEnabled"),
                TamperProtected: DefenderWmi.B(s, "IsTamperProtected"),
                CloudEnabled: p.MAPSReporting > 0,
                PuaEnabled: p.PUAProtection == 1,
                NetworkProtectionEnabled: p.EnableNetworkProtection == 1,
                SignatureVersion: DefenderWmi.S(s, "AntivirusSignatureVersion"),
                SignatureAgeDays: DefenderWmi.I(s, "AntivirusSignatureAge"),
                SignatureUpdatedAt: DefenderWmi.D(s, "AntivirusSignatureLastUpdated"),
                QuickScanAt: DefenderWmi.D(s, "QuickScanEndTime"),
                FullScanAt: DefenderWmi.D(s, "FullScanEndTime"),
                EngineVersion: DefenderWmi.S(s, "AMProductVersion"),
                ThirdPartyProducts: third,
                ExclusionCount: p.ExclusionPath.Count + p.ExclusionProcess.Count + p.ExclusionExtension.Count,
                AsrConfigured: asrConfigured,
                AsrBlock: asrBlock,
                ControlledFolderAccess: Mode(p.EnableControlledFolderAccess),
                NetworkProtectionMode: Mode(p.EnableNetworkProtection),
                SmartScreenEnabled: smartScreen,
                MemoryIntegrityEnabled: memoryIntegrity,
                CredentialGuardEnabled: credentialGuard,
                EmailScanEnabled: !p.DisableEmailScanning,
                ExploitProtectionEnabled: exploit,
                QuarantinedThreatCount: quarantined,
                Error: "");
        }
        catch (Exception ex)
        {
            return Empty("status_wmi_failed " + Trim(ex.Message, 200)) with
            {
                ProductName = third.FirstOrDefault() ?? "",
                ThirdPartyProducts = third,
            };
        }
    }

    private static List<string> ReadThirdPartyAv()
    {
        var list = new List<string>();
        try
        {
            foreach (var av in DefenderWmi.Query(@"\.
oot\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct"))
            {
                using (av)
                {
                    var name = DefenderWmi.S(av, "displayName");
                    if (name.Length > 0 &&
                        !name.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("Microsoft Defender", StringComparison.OrdinalIgnoreCase))
                        list.Add(name);
                }
            }
        }
        catch { /* Security Center absent (Server SKU) */ }
        return list;
    }

    /// <summary>
    /// System DEP mitigation as Get-ProcessMitigation -System reports it: the low nibble of
    /// the kernel MitigationOptions value (1 = ON, 2 = OFF, 0 = NOTSET).
    /// </summary>
    private static bool ReadSystemDepOn()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel");
            return k?.GetValue("MitigationOptions") is byte[] { Length: > 0 } b && (b[0] & 0x0F) == 1;
        }
        catch { return false; }
    }

    /// <summary>
    /// True when Windows itself has handed protection to a registered third-party AV
    /// (Defender passive / disabled). The guard then reports instead of fighting that product.
    /// </summary>
    public static bool ThirdPartyOwnsProtection(AvStatus s) =>
        s.ThirdPartyProducts.Count > 0 &&
        (!s.Available || !s.AntivirusEnabled ||
         s.RunningMode.Contains("Passive", StringComparison.OrdinalIgnoreCase));

    /// <summary>Protected = some engine has on-access scanning ON with fresh signatures.</summary>
    public static string Health(AvStatus s)
    {
        if (ThirdPartyOwnsProtection(s)) return "third_party";
        if (!s.Available) return "unavailable";
        if (!s.ServiceEnabled || !s.AntivirusEnabled) return "disabled";
        if (!s.RealTimeEnabled || !s.OnAccessEnabled) return "realtime_off";
        if (s.SignatureAgeDays > 3) return "outdated";
        return "protected";
    }

    // ── Enforcement ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reverts every Defender weakening in one PowerShell process. Only drifted settings are
    /// written (no churn), then preferences are re-read and anything Windows refused is
    /// returned as <see cref="EnforceResult.RemainingDrift"/>.
    /// </summary>
    public static EnforceResult Enforce(IReadOnlyCollection<string> allowedExclusions)
        => Enforce(allowedExclusions, HardeningOptions.Off);

    public static EnforceResult Enforce(IReadOnlyCollection<string> allowedExclusions, HardeningOptions hardening)
    {
        if (!OperatingSystem.IsWindows())
            return new EnforceResult([], [], ["unsupported_os"], []);

        var changed = new List<string>();
        var errors = new List<string>();
        var hardened = new List<string>();

        // Group Policy values override Set-MpPreference, so a "Turn off Defender" GPO /
        // registry edit is the strongest bypass. Remove weakening values first.
        RemovePolicyOverrides(changed, errors);
        EnsureDefenderServiceRunning(changed, errors);

        // Product self-exclusions are always ensured (and never removed). IT extras from
        // ALPHA_AV_ALLOWED_EXCLUSIONS stay on the allowlist; everything else is removed.
        var product = ProductAvExclusions.BuildCached();
        var allowed = DefenderPlan.BuildAllowed(product, allowedExclusions);

        DefenderWmi.Prefs prefs;
        try
        {
            prefs = DefenderWmi.ReadPrefs();
        }
        catch (Exception ex)
        {
            errors.Add("enforce_read_failed " + Trim(ex.Message, 200));
            return new EnforceResult(changed, [], errors, hardened);
        }

        // One snapshot drives every decision; only drifted / missing values are written.
        var ops = new List<DefenderPlan.Op>();
        ops.AddRange(DefenderPlan.TamperFixes(prefs, allowed));
        ops.AddRange(DefenderPlan.MissingProductExclusions(prefs, product));
        ops.AddRange(DefenderPlan.Hardening(prefs, hardening, AsrRuleIds, product.CfaAllowedApps));
        foreach (var op in ops)
        {
            if (Apply(op, errors))
                (op.Hardening ? hardened : changed).Add(op.Name);
        }

        var drift = new List<string>();
        if (changed.Count > 0)
        {
            try { drift = DefenderPlan.RemainingDrift(DefenderWmi.ReadPrefs(), allowed); }
            catch (Exception ex) { errors.Add("drift_read_failed " + Trim(ex.Message, 120)); }
        }
        return new EnforceResult(changed, drift, errors, hardened);
    }

    /// <summary>Execute one plan operation through WMI. Errors are collected, never thrown.</summary>
    private static bool Apply(DefenderPlan.Op op, List<string> errors)
    {
        try
        {
            switch (op.Kind)
            {
                case DefenderPlan.OpKind.Set: DefenderWmi.Set(op.Param, op.Value); break;
                case DefenderPlan.OpKind.Add: DefenderWmi.Add(op.Param, op.Value); break;
                case DefenderPlan.OpKind.Remove: DefenderWmi.Remove(op.Param, op.Value); break;
                case DefenderPlan.OpKind.AddAsrRule: DefenderWmi.AddAsrRule(op.Param, (byte)op.Value); break;
            }
            return true;
        }
        catch (Exception ex)
        {
            errors.Add(op.Name + ": " + Trim(ex.Message, 160));
            return false;
        }
    }

    private static void EnsureDefenderServiceRunning(List<string> changed, List<string> errors)
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("WinDefend");
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running) return;
            sc.Start();
            changed.Add("service_start");
        }
        catch (InvalidOperationException) { /* not installed / protected — Windows owns it */ }
        catch (Exception ex) { errors.Add("service_start: " + ex.GetType().Name); }
    }

    private static void RemovePolicyOverrides(List<string> changed, List<string> errors)
    {
        // (subkey, value) pairs where a non-zero DWORD turns protection off.
        (string Sub, string Name)[] disables =
        [
            ("", "DisableAntiSpyware"),
            ("", "DisableAntiVirus"),
            ("", "DisableRoutinelyTakingAction"),
            ("Real-Time Protection", "DisableRealtimeMonitoring"),
            ("Real-Time Protection", "DisableBehaviorMonitoring"),
            ("Real-Time Protection", "DisableOnAccessProtection"),
            ("Real-Time Protection", "DisableIOAVProtection"),
            ("Real-Time Protection", "DisableScanOnRealtimeEnable"),
            ("Real-Time Protection", "DisableScriptScanning"),
            ("Spynet", "DisableBlockAtFirstSeen"),
        ];
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(PolicyRoot, writable: true);
            if (root is null) return;

            foreach (var (sub, name) in disables)
            {
                try
                {
                    using var key = sub.Length == 0 ? null : root.OpenSubKey(sub, writable: true);
                    var target = sub.Length == 0 ? root : key;
                    if (target?.GetValue(name) is int v && v != 0)
                    {
                        target.DeleteValue(name, throwOnMissingValue: false);
                        changed.Add($"policy:{(sub.Length == 0 ? "" : sub + "\\")}{name}");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"policy:{name}: {ex.GetType().Name}");
                }
            }

            // Policy-level "never send / never report" cloud switches.
            try
            {
                using var spy = root.OpenSubKey("Spynet", writable: true);
                if (spy?.GetValue("SpynetReporting") is int sr && sr == 0)
                {
                    spy.DeleteValue("SpynetReporting", false);
                    changed.Add("policy:Spynet\\SpynetReporting");
                }
            }
            catch (Exception ex)
            {
                errors.Add("policy:SpynetReporting: " + ex.GetType().Name);
            }

            // Policy exclusions (Paths / Processes / Extensions) are a silent bypass —
            // Remove-MpPreference cannot delete them, so drop the GPO values here.
            try
            {
                using var exKey = root.OpenSubKey("Exclusions", writable: true);
                if (exKey is not null)
                {
                    foreach (var kind in new[] { "Paths", "Processes", "Extensions", "IpAddresses" })
                    {
                        using var k = exKey.OpenSubKey(kind, writable: true);
                        if (k is null) continue;
                        foreach (var n in k.GetValueNames())
                        {
                            k.DeleteValue(n, false);
                            changed.Add($"policy_exclusion:{kind}:{n}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add("policy:Exclusions: " + ex.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            errors.Add("policy_root: " + ex.GetType().Name);
        }
    }

    // ── Signatures / scans ──────────────────────────────────────────────────

    public static (bool Ok, string Detail) UpdateSignatures() =>
        Wmi(() => DefenderWmi.UpdateSignatures(TimeSpan.FromMinutes(15)));

    /// <summary>Blocking quick scan (hours at most). Run on a background task.</summary>
    public static (bool Ok, string Detail) QuickScan() =>
        Wmi(() => DefenderWmi.StartScan(1, null, TimeSpan.FromHours(2)));

    /// <summary>Blocking full scan (can take hours). Run on a background task.</summary>
    public static (bool Ok, string Detail) FullScan() =>
        Wmi(() => DefenderWmi.StartScan(2, null, TimeSpan.FromHours(12)));

    /// <summary>
    /// Scan one path on demand (removable drive, download folder, flagged file). The path is
    /// validated as an existing file/dir and passed as a typed WMI argument (no script text).
    /// </summary>
    public static (bool Ok, string Detail) CustomScan(string path)
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path)))
            return (false, "path_not_found");
        return Wmi(() => DefenderWmi.StartScan(3, path, TimeSpan.FromHours(4)));
    }

    /// <summary>Remove all active threats from quarantine/disk (admin remediate-all).</summary>
    public static (bool Ok, string Detail) RemoveThreats()
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
        try
        {
            var active = DefenderWmi.CountActiveThreats();
            if (active == 0) return (true, "ok: no active threats");
            DefenderWmi.RemoveActiveThreats(TimeSpan.FromMinutes(30));
            return (true, "ok: removed " + active);
        }
        catch (Exception ex)
        {
            return (false, Trim(ex.Message, 300));
        }
    }

    private static (bool Ok, string Detail) Wmi(Action action)
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
        try
        {
            action();
            return (true, "ok");
        }
        catch (Exception ex)
        {
            return (false, Trim(ex.Message, 300));
        }
    }

    /// <summary>
    /// Fast self-protection pass (no full guard enforce): ensure path / process / ASR-only
    /// exclusions for every product folder — only the ones actually missing — then restore
    /// any product file Defender has quarantined (by original file path, so unrelated threats
    /// are never restored). Native WMI + MpCmdRun; no PowerShell.
    /// </summary>
    public static (bool Ok, IReadOnlyList<string> Added, IReadOnlyList<string> Restored, IReadOnlyList<string> Errors)
        EnsureSelfProtection()
    {
        if (!OperatingSystem.IsWindows())
            return (false, [], [], ["unsupported_os"]);

        var product = ProductAvExclusions.BuildCached();
        var added = new List<string>();
        var restored = new List<string>();
        var errors = new List<string>();

        try
        {
            var prefs = DefenderWmi.ReadPrefs();
            foreach (var op in DefenderPlan.MissingProductExclusions(prefs, product))
            {
                if (Apply(op, errors))
                    added.Add(op.Name);
            }
        }
        catch (Exception ex)
        {
            errors.Add("self_protect_read_failed " + Trim(ex.Message, 200));
            return (false, added, restored, errors);
        }

        // Restore product files Defender quarantined (exclusions above stop a re-detect).
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var r in DefenderWmi.ThreatDetectionResources())
            {
                if (!r.StartsWith("file:_", StringComparison.OrdinalIgnoreCase)) continue;
                var f = ProductAvExclusions.NormalizePath(r[6..]);
                if (ProductAvExclusions.IsProductPath(f, product) && !File.Exists(f))
                    files.Add(f);
            }
        }
        catch (Exception ex)
        {
            errors.Add("threat_list: " + Trim(ex.Message, 160));
        }

        if (files.Count > 0)
        {
            var mp = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Windows Defender", "MpCmdRun.exe");
            foreach (var f in files)
            {
                if (!File.Exists(mp)) { errors.Add("mpcmdrun_missing"); break; }
                var (_, output) = RunProcess(mp, ["-Restore", "-FilePath", f], TimeSpan.FromMinutes(2));
                if (File.Exists(f)) restored.Add(f);
                else errors.Add("restore:" + f + ": " + Trim(output, 160));
            }
        }

        return (errors.Count == 0, added, restored, errors);
    }

    /// <summary>
    /// Restore one quarantined item (admin false-positive). ThreatID is validated numeric and
    /// passed through an environment variable (never interpolated into the script). Rare,
    /// admin-triggered — the only Defender action here without a WMI method.
    /// </summary>
    public static (bool Ok, string Detail) RestoreThreat(string threatId)
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
        if (string.IsNullOrWhiteSpace(threatId) || !long.TryParse(threatId.Trim(), out _))
            return (false, "invalid_threat_id");
        const string script = """
            $ErrorActionPreference = 'Stop'
            Restore-MpThreat -ThreatID ([int64]$env:ALPHA_AV_THREAT_ID) -ErrorAction Stop
            'ok'
            """;
        var (exit, stdout, stderr) = RunPowerShell(script, TimeSpan.FromMinutes(10),
            new Dictionary<string, string> { ["ALPHA_AV_THREAT_ID"] = threatId.Trim() });
        var ok = exit == 0 && stdout.Contains("ok", StringComparison.OrdinalIgnoreCase);
        return (ok, ok ? "ok" : Trim(stdout + " " + stderr, 300));
    }

    /// <summary>
    /// Enable the safe baseline of system-wide exploit-protection mitigations (EMET successor:
    /// DEP, SEHOP, bottom-up + high-entropy ASLR, Control Flow Guard). Skipped when system DEP
    /// is already ON (baseline set), so the 12-hourly refresh normally starts no process.
    /// </summary>
    public static (bool Ok, string Detail) EnsureExploitProtection()
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
        if (ReadSystemDepOn()) return (true, "ok: already set");
        const string script = """
            $ErrorActionPreference = 'Stop'
            try {
              Set-ProcessMitigation -System -Enable DEP, SEHOP, BottomUp, HighEntropy, CFG -ErrorAction Stop
              'ok'
            } catch { 'err: ' + $_.Exception.Message }
            """;
        var (exit, stdout, stderr) = RunPowerShell(script, TimeSpan.FromSeconds(120));
        var ok = exit == 0 && stdout.Contains("ok", StringComparison.OrdinalIgnoreCase)
                 && !stdout.Contains("err:", StringComparison.OrdinalIgnoreCase);
        return (ok, ok ? "ok" : Trim(stdout + " " + stderr, 300));
    }

    // ── Event log ───────────────────────────────────────────────────────────

    /// <summary>
    /// Defender Operational events we act on:
    /// 1006/1116 malware detected, 1007/1117 action taken, 1008/1118/1119 action failed,
    /// 5001 real-time protection disabled, 5010/5012 scanning disabled, 5013 tamper
    /// protection blocked a change. (5004/5007 config-change events are not watched — they
    /// fire for every routine change including the guard's own; drift is caught by Enforce.)
    /// </summary>
    public static readonly int[] WatchedEventIds =
        [1006, 1007, 1008, 1116, 1117, 1118, 1119, 1121, 5001, 5010, 5012, 5013];

    /// <summary>Events with RecordId &gt; <paramref name="afterRecordId"/>, oldest first.</summary>
    public static List<DefenderEvent> ReadEvents(long afterRecordId, int max = 200)
    {
        var list = new List<DefenderEvent>();
        if (!OperatingSystem.IsWindows()) return list;

        var ids = string.Join(" or ", WatchedEventIds.Select(i => $"EventID={i}"));
        var xpath = afterRecordId > 0
            ? $"*[System[({ids}) and EventRecordID > {afterRecordId}]]"
            : $"*[System[({ids})]]";
        var query = new EventLogQuery(DefenderLog, PathType.LogName, xpath);
        using var reader = new EventLogReader(query);
        for (var rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
        {
            using (rec)
            {
                var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var xml = XDocument.Parse(rec.ToXml());
                    foreach (var d in xml.Descendants().Where(e => e.Name.LocalName == "Data"))
                    {
                        var name = d.Attribute("Name")?.Value;
                        if (!string.IsNullOrWhiteSpace(name))
                            data[name] = d.Value;
                    }
                }
                catch { /* keep id/time even if the payload is unreadable */ }

                list.Add(new DefenderEvent(
                    rec.RecordId ?? 0,
                    rec.Id,
                    rec.TimeCreated is { } t ? new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.UtcNow,
                    data));
            }
            if (list.Count >= max) break;
        }
        return list.OrderBy(e => e.RecordId).ToList();
    }

    /// <summary>Highest record id currently in the log (baseline on first run).</summary>
    public static long LatestRecordId()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var query = new EventLogQuery(DefenderLog, PathType.LogName) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        using var rec = reader.ReadEvent();
        return rec?.RecordId ?? 0;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Time-boxed PowerShell for the two rare admin actions that have no WMI method.
    /// The script is written to a temp <c>.ps1</c> and run with <c>-File</c> — an
    /// <c>-EncodedCommand</c> blob is a classic malware pattern that Defender behaviour
    /// monitoring flags. Concurrent stream drain + tree-kill on timeout.
    /// </summary>
    private static (int Exit, string StdOut, string StdErr) RunPowerShell(string script, TimeSpan timeout)
        => RunPowerShell(script, timeout, null);

    private static (int Exit, string StdOut, string StdErr) RunPowerShell(
        string script, TimeSpan timeout, IReadOnlyDictionary<string, string>? env)
    {
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(exe)) exe = "powershell.exe";
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(DlpDataPaths.DlpRoot);
            tmp = Path.Combine(DlpDataPaths.DlpRoot, "av-" + Guid.NewGuid().ToString("n") + ".ps1");
            File.WriteAllText(tmp, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return RunProcess(exe, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", tmp], timeout, env);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (tmp is not null)
            {
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>Run a native executable with argument list (no shell parsing).</summary>
    private static (int Exit, string Output) RunProcess(string exe, string[] args, TimeSpan timeout)
    {
        var r = RunProcess(exe, args, timeout, null);
        return (r.Exit, (r.StdOut + " " + r.StdErr).Trim());
    }

    private static (int Exit, string StdOut, string StdErr) RunProcess(
        string exe, string[] args, TimeSpan timeout, IReadOnlyDictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "", "process_start_failed");
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds)))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (-1, "", "timeout");
            }
            var drained = Task.WaitAll([outTask, errTask], TimeSpan.FromSeconds(5));
            return (proc.ExitCode,
                drained ? outTask.Result : "",
                drained ? errTask.Result : "stream_drain_timeout");
        }
        catch (Exception ex)
        {
            return (-1, "", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static AvStatus Empty(string error) => new(
        false, "", "", false, false, false, false, false, false, false, false, false, false,
        "", 0, null, null, null, "", [], 0,
        0, 0, "off", "off", false, false, false, false, false, 0, error);

    private static string Trim(string s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max];
    }
}
