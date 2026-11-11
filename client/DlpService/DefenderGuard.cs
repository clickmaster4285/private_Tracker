using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace AlphaAITracker.Service;

/// <summary>
/// 24/7 antivirus guard (Windows). The OS antivirus engine — Microsoft Defender — does the
/// on-access scanning (every file is scanned when it is opened / run / downloaded / copied
/// and blocked + quarantined when malicious). This class keeps that engine ON at maximum
/// strength and reports what it finds:
/// <list type="bullet">
/// <item><see cref="ReadStatus"/> — engine health via <c>Get-MpComputerStatus</c> +
/// <c>Get-MpPreference</c> + Security Center (third-party AV).</item>
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

    private const string StatusScript = """
        $ErrorActionPreference = 'Stop'
        function Iso($d) { if ($d -and $d.Year -gt 1601) { $d.ToUniversalTime().ToString('o') } else { '' } }
        $o = [ordered]@{ available = $false; error = '' }
        try {
          $s = Get-MpComputerStatus
          $p = Get-MpPreference
          $o.available = $true
          $o.runningMode = [string]$s.AMRunningMode
          $o.serviceEnabled = [bool]$s.AMServiceEnabled
          $o.antivirusEnabled = [bool]$s.AntivirusEnabled
          $o.realTime = [bool]$s.RealTimeProtectionEnabled
          $o.behavior = [bool]$s.BehaviorMonitorEnabled
          $o.onAccess = [bool]$s.OnAccessProtectionEnabled
          $o.ioav = [bool]$s.IoavProtectionEnabled
          $o.tamper = [bool]$s.IsTamperProtected
          $o.cloud = ([int]$p.MAPSReporting -gt 0)
          $o.pua = ([int]$p.PUAProtection -eq 1)
          $o.netProt = ([int]$p.EnableNetworkProtection -eq 1)
          $o.sigVersion = [string]$s.AntivirusSignatureVersion
          $o.sigAge = [int]$s.AntivirusSignatureAge
          $o.sigUpdated = Iso $s.AntivirusSignatureLastUpdated
          $o.quickScan = Iso $s.QuickScanEndTime
          $o.fullScan = Iso $s.FullScanEndTime
          $o.engine = [string]$s.AMProductVersion
          $o.exclusions = @($p.ExclusionPath).Count + @($p.ExclusionProcess).Count + @($p.ExclusionExtension).Count
          $o.emailScan = (-not $p.DisableEmailScanning)
          # ASR rules: count configured (block=1 / audit=2), and block-mode specifically.
          $acts = @($p.AttackSurfaceReductionRules_Actions | ForEach-Object { [int]$_ })
          $o.asrConfigured = @($acts | Where-Object { $_ -eq 1 -or $_ -eq 2 }).Count
          $o.asrBlock = @($acts | Where-Object { $_ -eq 1 }).Count
          $o.cfa = switch ([int]$p.EnableControlledFolderAccess) { 1 { 'block' } 2 { 'audit' } default { 'off' } }
          $o.netMode = switch ([int]$p.EnableNetworkProtection) { 1 { 'block' } 2 { 'audit' } default { 'off' } }
          # Quarantined / active threats (best-effort).
          try { $o.quarantined = @(Get-MpThreat -ErrorAction Stop).Count } catch { $o.quarantined = 0 }
        } catch { $o.error = $_.Exception.Message }
        # Device Guard: memory integrity (HVCI = service 2) + Credential Guard (service 1).
        $o.memoryIntegrity = $false; $o.credentialGuard = $false
        try {
          $dg = Get-CimInstance -Namespace root/Microsoft/Windows/DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction Stop
          $running = @($dg.SecurityServicesRunning | ForEach-Object { [int]$_ })
          $o.memoryIntegrity = ($running -contains 2)
          $o.credentialGuard = ($running -contains 1)
        } catch { }
        # SmartScreen for apps/files (Explorer policy; 'RequireAdmin'/'Warn' = on, 'Off' = off).
        $o.smartScreen = $false
        try {
          $ss = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' -Name SmartScreenEnabled -ErrorAction Stop).SmartScreenEnabled
          $o.smartScreen = ($ss -and $ss -ne 'Off')
        } catch { }
        # Exploit protection: system DEP mitigation ON = baseline active.
        $o.exploitProtection = $false
        try {
          $mp = Get-ProcessMitigation -System -ErrorAction Stop
          $o.exploitProtection = ([string]$mp.Dep.Enable -eq 'ON' -or [int]$mp.Dep.Enable -eq 1)
        } catch { }
        $tp = @()
        try {
          $tp = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop |
                  Where-Object { $_.displayName -and $_.displayName -notmatch 'Windows Defender|Microsoft Defender' } |
                  ForEach-Object { [string]$_.displayName })
        } catch { }
        $o.thirdParty = $tp
        $o | ConvertTo-Json -Compress -Depth 3
        """;

    public static AvStatus ReadStatus()
    {
        if (!OperatingSystem.IsWindows())
            return Empty("unsupported_os");

        var (exit, stdout, stderr) = RunPowerShell(StatusScript, TimeSpan.FromSeconds(60));
        var json = LastJsonLine(stdout);
        if (json is null)
            return Empty($"status_probe_failed exit={exit} {Trim(stderr, 200)}");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var third = new List<string>();
            if (r.TryGetProperty("thirdParty", out var tp))
            {
                if (tp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in tp.EnumerateArray())
                        if (e.GetString() is { Length: > 0 } s) third.Add(s);
                }
                else if (tp.ValueKind == JsonValueKind.String && tp.GetString() is { Length: > 0 } one)
                {
                    third.Add(one);
                }
            }

            var available = Bool(r, "available");
            return new AvStatus(
                Available: available,
                ProductName: available ? "Microsoft Defender Antivirus" : (third.FirstOrDefault() ?? ""),
                RunningMode: Str(r, "runningMode"),
                ServiceEnabled: Bool(r, "serviceEnabled"),
                AntivirusEnabled: Bool(r, "antivirusEnabled"),
                RealTimeEnabled: Bool(r, "realTime"),
                BehaviorMonitorEnabled: Bool(r, "behavior"),
                OnAccessEnabled: Bool(r, "onAccess"),
                IoavEnabled: Bool(r, "ioav"),
                TamperProtected: Bool(r, "tamper"),
                CloudEnabled: Bool(r, "cloud"),
                PuaEnabled: Bool(r, "pua"),
                NetworkProtectionEnabled: Bool(r, "netProt"),
                SignatureVersion: Str(r, "sigVersion"),
                SignatureAgeDays: Int(r, "sigAge"),
                SignatureUpdatedAt: Date(r, "sigUpdated"),
                QuickScanAt: Date(r, "quickScan"),
                FullScanAt: Date(r, "fullScan"),
                EngineVersion: Str(r, "engine"),
                ThirdPartyProducts: third,
                ExclusionCount: Int(r, "exclusions"),
                AsrConfigured: Int(r, "asrConfigured"),
                AsrBlock: Int(r, "asrBlock"),
                ControlledFolderAccess: Str(r, "cfa") is { Length: > 0 } cfa ? cfa : "off",
                NetworkProtectionMode: Str(r, "netMode") is { Length: > 0 } nm ? nm : "off",
                SmartScreenEnabled: Bool(r, "smartScreen"),
                MemoryIntegrityEnabled: Bool(r, "memoryIntegrity"),
                CredentialGuardEnabled: Bool(r, "credentialGuard"),
                EmailScanEnabled: Bool(r, "emailScan"),
                ExploitProtectionEnabled: Bool(r, "exploitProtection"),
                QuarantinedThreatCount: Int(r, "quarantined"),
                Error: Str(r, "error"));
        }
        catch (Exception ex)
        {
            return Empty("status_parse_failed " + ex.GetType().Name);
        }
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

        // Group Policy values override Set-MpPreference, so a "Turn off Defender" GPO /
        // registry edit is the strongest bypass. Remove weakening values first.
        RemovePolicyOverrides(changed, errors);

        // Newline-joined (not JSON): PS 5.1 ConvertFrom-Json wraps arrays in one object.
        var allowList = string.Join("\n", allowedExclusions
            .Where(x => !string.IsNullOrWhiteSpace(x) && !x.Contains('\n')));

        var cfa = !hardening.Enabled || !hardening.ControlledFolderAccess
            ? ""
            : (hardening.CfaAudit ? "AuditMode" : "Enabled");
        var script = EnforceScript
            .Replace("__ALLOWED__", EscapePs(allowList), StringComparison.Ordinal)
            .Replace("__HARDEN__", hardening.Enabled ? "1" : "0", StringComparison.Ordinal)
            .Replace("__ASR_ACTION__", hardening.AsrAudit ? "AuditMode" : "Enabled", StringComparison.Ordinal)
            .Replace("__ASR_IDS__", EscapePs(string.Join("\n", AsrRuleIds)), StringComparison.Ordinal)
            .Replace("__CFA__", EscapePs(cfa), StringComparison.Ordinal)
            .Replace("__CPU__", Clamp(hardening.ScanCpuLimit, 0, 100).ToString(), StringComparison.Ordinal)
            .Replace("__PURGE__", Math.Max(0, hardening.QuarantinePurgeDays).ToString(), StringComparison.Ordinal);

        var (exit, stdout, stderr) = RunPowerShell(script, TimeSpan.FromSeconds(180));
        var json = LastJsonLine(stdout);
        if (json is null)
        {
            errors.Add($"enforce_probe_failed exit={exit} {Trim(stderr, 200)}");
            return new EnforceResult(changed, [], errors, []);
        }

        var drift = new List<string>();
        var hardened = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            changed.AddRange(Strings(doc.RootElement, "changed"));
            errors.AddRange(Strings(doc.RootElement, "errors"));
            drift.AddRange(Strings(doc.RootElement, "drift"));
            hardened.AddRange(Strings(doc.RootElement, "hardened"));
        }
        catch (Exception ex)
        {
            errors.Add("enforce_parse_failed " + ex.GetType().Name);
        }
        return new EnforceResult(changed, drift, errors, hardened);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    // Threat default actions: 1 Clean, 2 Quarantine, 3 Remove, 6 Allow, 8 UserDefined,
    // 9 NoAction, 10 Block. Only "let it run" values are corrected (6/8/9); 0 = Defender's
    // recommended default, which already quarantines.
    private const string EnforceScript = """
        $ErrorActionPreference = 'Continue'
        $allowed = @('__ALLOWED__' -split "`n" | Where-Object { $_ })
        $changed = New-Object System.Collections.ArrayList
        $errors = New-Object System.Collections.ArrayList
        $hardened = New-Object System.Collections.ArrayList
        function Fix([string]$name, [scriptblock]$sb) {
          try { & $sb; [void]$changed.Add($name) } catch { [void]$errors.Add($name + ': ' + $_.Exception.Message) }
        }
        function Weak($p) {
          $d = New-Object System.Collections.ArrayList
          if ($p.DisableRealtimeMonitoring) { [void]$d.Add('realtime') }
          if ($p.DisableBehaviorMonitoring) { [void]$d.Add('behavior') }
          if ($p.DisableIOAVProtection) { [void]$d.Add('download_scan') }
          if ($p.DisableScriptScanning) { [void]$d.Add('script_scan') }
          if ($p.DisableBlockAtFirstSeen) { [void]$d.Add('block_at_first_sight') }
          if ($p.DisableArchiveScanning) { [void]$d.Add('archive_scan') }
          if ($p.DisableRemovableDriveScanning) { [void]$d.Add('removable_scan') }
          if ([int]$p.MAPSReporting -ne 2) { [void]$d.Add('cloud_protection') }
          if ([int]$p.SubmitSamplesConsent -eq 2) { [void]$d.Add('sample_submission') }
          if ([int]$p.CloudBlockLevel -lt 2) { [void]$d.Add('cloud_block_level') }
          if ([int]$p.PUAProtection -ne 1) { [void]$d.Add('pua') }
          if ([int]$p.EnableNetworkProtection -ne 1) { [void]$d.Add('network_protection') }
          foreach ($a in @($p.LowThreatDefaultAction, $p.ModerateThreatDefaultAction, $p.HighThreatDefaultAction, $p.SevereThreatDefaultAction)) {
            if (@(6, 8, 9) -contains [int]$a) { [void]$d.Add('threat_action'); break }
          }
          if (@($p.ThreatIDDefaultAction_Ids | Where-Object { $_ }).Count -gt 0) { [void]$d.Add('threat_overrides') }
          foreach ($x in @($p.ExclusionPath) + @($p.ExclusionProcess) + @($p.ExclusionExtension)) {
            if ($x -and -not ($allowed -contains $x)) { [void]$d.Add('exclusions'); break }
          }
          return ,$d
        }
        try { $svc = Get-Service WinDefend -ErrorAction Stop; if ($svc.Status -ne 'Running') { Fix 'service_start' { Start-Service WinDefend -ErrorAction Stop } } } catch { }
        $p = Get-MpPreference
        if ($p.DisableRealtimeMonitoring) { Fix 'realtime' { Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction Stop } }
        if ($p.DisableBehaviorMonitoring) { Fix 'behavior' { Set-MpPreference -DisableBehaviorMonitoring $false -ErrorAction Stop } }
        if ($p.DisableIOAVProtection) { Fix 'download_scan' { Set-MpPreference -DisableIOAVProtection $false -ErrorAction Stop } }
        if ($p.DisableScriptScanning) { Fix 'script_scan' { Set-MpPreference -DisableScriptScanning $false -ErrorAction Stop } }
        if ($p.DisableBlockAtFirstSeen) { Fix 'block_at_first_sight' { Set-MpPreference -DisableBlockAtFirstSeen $false -ErrorAction Stop } }
        if ($p.DisableArchiveScanning) { Fix 'archive_scan' { Set-MpPreference -DisableArchiveScanning $false -ErrorAction Stop } }
        if ($p.DisableRemovableDriveScanning) { Fix 'removable_scan' { Set-MpPreference -DisableRemovableDriveScanning $false -ErrorAction Stop } }
        if ([int]$p.MAPSReporting -ne 2) { Fix 'cloud_protection' { Set-MpPreference -MAPSReporting Advanced -ErrorAction Stop } }
        if ([int]$p.SubmitSamplesConsent -eq 2) { Fix 'sample_submission' { Set-MpPreference -SubmitSamplesConsent SendSafeSamples -ErrorAction Stop } }
        if ([int]$p.CloudBlockLevel -lt 2) { Fix 'cloud_block_level' { Set-MpPreference -CloudBlockLevel High -ErrorAction Stop } }
        if ([int]$p.CloudExtendedTimeout -lt 50) { Fix 'cloud_timeout' { Set-MpPreference -CloudExtendedTimeout 50 -ErrorAction Stop } }
        if ([int]$p.PUAProtection -ne 1) { Fix 'pua' { Set-MpPreference -PUAProtection Enabled -ErrorAction Stop } }
        if ([int]$p.EnableNetworkProtection -ne 1) { Fix 'network_protection' { Set-MpPreference -EnableNetworkProtection Enabled -ErrorAction Stop } }
        if (@(6, 8, 9) -contains [int]$p.LowThreatDefaultAction) { Fix 'threat_action_low' { Set-MpPreference -LowThreatDefaultAction Quarantine -ErrorAction Stop } }
        if (@(6, 8, 9) -contains [int]$p.ModerateThreatDefaultAction) { Fix 'threat_action_moderate' { Set-MpPreference -ModerateThreatDefaultAction Quarantine -ErrorAction Stop } }
        if (@(6, 8, 9) -contains [int]$p.HighThreatDefaultAction) { Fix 'threat_action_high' { Set-MpPreference -HighThreatDefaultAction Remove -ErrorAction Stop } }
        if (@(6, 8, 9) -contains [int]$p.SevereThreatDefaultAction) { Fix 'threat_action_severe' { Set-MpPreference -SevereThreatDefaultAction Remove -ErrorAction Stop } }
        $ids = @($p.ThreatIDDefaultAction_Ids | Where-Object { $_ })
        if ($ids.Count -gt 0) { Fix 'threat_overrides' { Remove-MpPreference -ThreatIDDefaultAction_Ids $ids -ErrorAction Stop } }
        foreach ($x in @($p.ExclusionPath | Where-Object { $_ -and -not ($allowed -contains $_) })) { Fix ('exclusion_path:' + $x) { Remove-MpPreference -ExclusionPath $x -ErrorAction Stop } }
        foreach ($x in @($p.ExclusionProcess | Where-Object { $_ -and -not ($allowed -contains $_) })) { Fix ('exclusion_process:' + $x) { Remove-MpPreference -ExclusionProcess $x -ErrorAction Stop } }
        foreach ($x in @($p.ExclusionExtension | Where-Object { $_ -and -not ($allowed -contains $_) })) { Fix ('exclusion_extension:' + $x) { Remove-MpPreference -ExclusionExtension $x -ErrorAction Stop } }
        # ── Advanced hardening (ASR / CFA / scan tuning) — reported separately from tamper ──
        function Harden([string]$name, [scriptblock]$sb) {
          try { & $sb; [void]$hardened.Add($name) } catch { [void]$errors.Add($name + ': ' + $_.Exception.Message) }
        }
        if ('__HARDEN__' -eq '1') {
          $ph = Get-MpPreference
          if ($ph.DisableEmailScanning) { Harden 'email_scan' { Set-MpPreference -DisableEmailScanning $false -ErrorAction Stop } }
          if ($ph.DisableCatchupQuickScan) { Harden 'catchup_quick_scan' { Set-MpPreference -DisableCatchupQuickScan $false -ErrorAction Stop } }
          if ($ph.DisableCatchupFullScan) { Harden 'catchup_full_scan' { Set-MpPreference -DisableCatchupFullScan $false -ErrorAction Stop } }
          if (-not $ph.CheckForSignaturesBeforeRunningScan) { Harden 'signature_before_scan' { Set-MpPreference -CheckForSignaturesBeforeRunningScan $true -ErrorAction Stop } }
          $cpu = [int]'__CPU__'
          if ($cpu -gt 0 -and [int]$ph.ScanAvgCPULoadFactor -ne $cpu) { Harden 'scan_cpu_limit' { Set-MpPreference -ScanAvgCPULoadFactor $cpu -ErrorAction Stop } }
          $purge = [int]'__PURGE__'
          if ($purge -gt 0 -and [int]$ph.QuarantinePurgeItemsAfterDelay -ne $purge) { Harden 'quarantine_purge' { Set-MpPreference -QuarantinePurgeItemsAfterDelay $purge -ErrorAction Stop } }

          # Attack Surface Reduction rules — bring each desired rule to the wanted action.
          $asrAction = '__ASR_ACTION__'
          $wantAct = if ($asrAction -eq 'AuditMode') { 2 } else { 1 }
          $asrIds = @('__ASR_IDS__' -split "`n" | Where-Object { $_ })
          $curIds = @($ph.AttackSurfaceReductionRules_Ids)
          $curActs = @($ph.AttackSurfaceReductionRules_Actions)
          $map = @{}
          for ($i = 0; $i -lt $curIds.Count; $i++) { $map[([string]$curIds[$i]).ToLower()] = [int]$curActs[$i] }
          foreach ($gid in $asrIds) {
            if ($map[$gid.ToLower()] -ne $wantAct) {
              Harden ('asr:' + $gid.Substring(0, 8)) { Add-MpPreference -AttackSurfaceReductionRules_Ids $gid -AttackSurfaceReductionRules_Actions $asrAction -ErrorAction Stop }
            }
          }

          # Controlled Folder Access (anti-ransomware) — only when opted in ('' = leave as-is).
          $cfa = '__CFA__'
          if ($cfa -ne '') {
            $cfaWant = switch ($cfa) { 'Enabled' { 1 } 'AuditMode' { 2 } default { 0 } }
            if ([int]$ph.EnableControlledFolderAccess -ne $cfaWant) {
              Harden ('controlled_folder_access:' + $cfa) { Set-MpPreference -EnableControlledFolderAccess $cfa -ErrorAction Stop }
            }
          }
        }

        $drift = @()
        if ($changed.Count -gt 0) { $drift = Weak (Get-MpPreference) }
        [ordered]@{ changed = @($changed); errors = @($errors); drift = @($drift); hardened = @($hardened) } | ConvertTo-Json -Compress -Depth 3
        """;

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

    public static (bool Ok, string Detail) UpdateSignatures()
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
        var (exit, _, stderr) = RunPowerShell(
            "Update-MpSignature -ErrorAction Stop; 'ok'", TimeSpan.FromMinutes(15));
        return (exit == 0, exit == 0 ? "ok" : Trim(stderr, 300));
    }

    /// <summary>Blocking quick scan (hours at most). Run on a background task.</summary>
    public static (bool Ok, string Detail) QuickScan()
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
        var (exit, _, stderr) = RunPowerShell(
            "Start-MpScan -ScanType QuickScan -ErrorAction Stop; 'ok'", TimeSpan.FromHours(2));
        return (exit == 0, exit == 0 ? "ok" : Trim(stderr, 300));
    }

    /// <summary>Blocking full scan (can take hours). Run on a background task.</summary>
    public static (bool Ok, string Detail) FullScan()
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
        var (exit, _, stderr) = RunPowerShell(
            "Start-MpScan -ScanType FullScan -ErrorAction Stop; 'ok'", TimeSpan.FromHours(12));
        return (exit == 0, exit == 0 ? "ok" : Trim(stderr, 300));
    }

    /// <summary>
    /// Scan one path on demand (removable drive, download folder, flagged file). The path is
    /// validated as an existing file/dir and passed as a bound argument (never interpolated).
    /// </summary>
    public static (bool Ok, string Detail) CustomScan(string path)
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path)))
            return (false, "path_not_found");
        // Path travels in an environment variable (a distinct process value, never string-
        // interpolated into the script) so a crafted path cannot inject PowerShell.
        const string script = """
            $ErrorActionPreference = 'Stop'
            $p = $env:ALPHA_AV_SCAN_TARGET
            Start-MpScan -ScanType CustomScan -ScanPath $p -ErrorAction Stop
            'ok'
            """;
        var (exit, _, stderr) = RunPowerShell(script, TimeSpan.FromHours(4),
            new Dictionary<string, string> { ["ALPHA_AV_SCAN_TARGET"] = path });
        return (exit == 0, exit == 0 ? "ok" : Trim(stderr, 300));
    }

    /// <summary>Remove all active threats from quarantine/disk (admin remediate-all).</summary>
    public static (bool Ok, string Detail) RemoveThreats()
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
        const string script = """
            $ErrorActionPreference = 'Stop'
            $active = @(Get-MpThreat | Where-Object { $_.IsActive })
            if ($active.Count -eq 0) { 'ok: no active threats'; return }
            Remove-MpThreat -ErrorAction Stop
            'ok: removed ' + $active.Count
            """;
        var (exit, stdout, stderr) = RunPowerShell(script, TimeSpan.FromMinutes(30));
        var ok = exit == 0 && stdout.Contains("ok:", StringComparison.OrdinalIgnoreCase);
        return (ok, ok ? Trim(stdout, 300) : Trim(stdout + " " + stderr, 300));
    }

    /// <summary>
    /// Restore one quarantined item (admin false-positive). ThreatID is validated numeric and
    /// passed through an environment variable (never interpolated into the script).
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
    /// DEP, SEHOP, bottom-up + high-entropy ASLR, Control Flow Guard). Idempotent — re-running
    /// when already set is a no-op. Not part of the per-tick drift loop (mitigations rarely
    /// change and re-reading them is expensive); the worker calls this occasionally.
    /// </summary>
    public static (bool Ok, string Detail) EnsureExploitProtection()
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
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
        [1006, 1007, 1008, 1116, 1117, 1118, 1119, 5001, 5010, 5012, 5013];

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
    /// Time-boxed PowerShell (absolute path, encoded command, concurrent stream drain,
    /// tree-kill on timeout) — a hung cmdlet can never wedge the guard loop.
    /// </summary>
    private static (int Exit, string StdOut, string StdErr) RunPowerShell(string script, TimeSpan timeout)
        => RunPowerShell(script, timeout, null);

    private static (int Exit, string StdOut, string StdErr) RunPowerShell(
        string script, TimeSpan timeout, IReadOnlyDictionary<string, string>? env)
    {
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(exe)) exe = "powershell.exe";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
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

    private static string? LastJsonLine(string stdout)
    {
        foreach (var line in stdout.Split('\n').Reverse())
        {
            var t = line.Trim();
            if (t.StartsWith('{') && t.EndsWith('}')) return t;
        }
        return null;
    }

    private static string EscapePs(string s) => s.Replace("'", "''", StringComparison.Ordinal);

    private static AvStatus Empty(string error) => new(
        false, "", "", false, false, false, false, false, false, false, false, false, false,
        "", 0, null, null, null, "", [], 0,
        0, 0, "off", "off", false, false, false, false, false, 0, error);

    private static bool Bool(JsonElement r, string n) =>
        r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement r, string n) =>
        r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static string Str(JsonElement r, string n) =>
        r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTimeOffset? Date(JsonElement r, string n) =>
        DateTimeOffset.TryParse(Str(r, n), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static IEnumerable<string> Strings(JsonElement r, string n)
    {
        if (!r.TryGetProperty(n, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.String)
        {
            if (v.GetString() is { Length: > 0 } s) yield return s;
            yield break;
        }
        if (v.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in v.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
                yield return s;
    }

    private static string Trim(string s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max];
    }
}
