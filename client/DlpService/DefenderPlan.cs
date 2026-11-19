namespace AlphaAITracker.Service;

/// <summary>
/// Pure decision logic for the antivirus guard: given a Defender preference snapshot,
/// decide which writes are needed. No Defender / WMI access here, so every rule is unit
/// testable. The rules are the same ones the former PowerShell <c>EnforceScript</c> /
/// <c>SelfProtectScript</c> applied; only drifted values produce an operation, and an
/// exclusion that is already present is never re-added.
/// </summary>
internal static class DefenderPlan
{
    public enum OpKind { Set, Add, Remove, AddAsrRule }

    /// <summary>One MSFT_MpPreference write. <see cref="Hardening"/> ops are never tamper.</summary>
    public sealed record Op(string Name, OpKind Kind, string Param, object Value, bool Hardening);

    /// <summary>Allowed exclusion set: product roots + IT extras, normalized, broad paths dropped.</summary>
    public static HashSet<string> BuildAllowed(ProductAvExclusions.Set product, IEnumerable<string> itExtras)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in ProductAvExclusions.FlattenAllowlist(product))
            AddAllowed(allowed, x);
        foreach (var x in itExtras)
        {
            if (string.IsNullOrWhiteSpace(x) || x.Contains('\n')) continue;
            // A broad / Temp exclusion from service.env is rejected, not honoured.
            if (ProductAvExclusions.IsBroadOrUnsafeExclusion(x)) continue;
            AddAllowed(allowed, x.Trim());
        }
        return allowed;
    }

    private static void AddAllowed(HashSet<string> set, string x)
    {
        var n = ProductAvExclusions.NormalizePath(x);
        if (n.Length > 0) set.Add(n);
    }

    public static bool IsAllowed(string? value, HashSet<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var n = ProductAvExclusions.NormalizePath(value);
        return n.Length == 0 || allowed.Contains(n);
    }

    private static bool LetItRun(int action) => action is 6 or 8 or 9; // Allow / UserDefined / NoAction

    /// <summary>Weakening settings + unauthorized exclusions → tamper operations.</summary>
    public static List<Op> TamperFixes(DefenderWmi.Prefs p, HashSet<string> allowed)
    {
        var ops = new List<Op>();
        void Fix(bool weak, string name, string param, object value)
        {
            if (weak) ops.Add(new Op(name, OpKind.Set, param, value, Hardening: false));
        }

        Fix(p.DisableRealtimeMonitoring, "realtime", "DisableRealtimeMonitoring", false);
        Fix(p.DisableBehaviorMonitoring, "behavior", "DisableBehaviorMonitoring", false);
        Fix(p.DisableIOAVProtection, "download_scan", "DisableIOAVProtection", false);
        Fix(p.DisableScriptScanning, "script_scan", "DisableScriptScanning", false);
        Fix(p.DisableBlockAtFirstSeen, "block_at_first_sight", "DisableBlockAtFirstSeen", false);
        Fix(p.DisableArchiveScanning, "archive_scan", "DisableArchiveScanning", false);
        Fix(p.DisableRemovableDriveScanning, "removable_scan", "DisableRemovableDriveScanning", false);
        Fix(p.MAPSReporting != 2, "cloud_protection", "MAPSReporting", (byte)2);            // Advanced
        Fix(p.SubmitSamplesConsent == 2, "sample_submission", "SubmitSamplesConsent", (byte)1); // SendSafeSamples
        Fix(p.CloudBlockLevel < 2, "cloud_block_level", "CloudBlockLevel", (byte)2);        // High
        Fix(p.CloudExtendedTimeout < 50, "cloud_timeout", "CloudExtendedTimeout", (uint)50);
        Fix(p.PUAProtection != 1, "pua", "PUAProtection", (byte)1);
        Fix(p.EnableNetworkProtection != 1, "network_protection", "EnableNetworkProtection", (byte)1);
        Fix(LetItRun(p.LowThreatDefaultAction), "threat_action_low", "LowThreatDefaultAction", (byte)2);          // Quarantine
        Fix(LetItRun(p.ModerateThreatDefaultAction), "threat_action_moderate", "ModerateThreatDefaultAction", (byte)2);
        Fix(LetItRun(p.HighThreatDefaultAction), "threat_action_high", "HighThreatDefaultAction", (byte)3);       // Remove
        Fix(LetItRun(p.SevereThreatDefaultAction), "threat_action_severe", "SevereThreatDefaultAction", (byte)3);

        if (p.ThreatIdDefaultActionIds.Count > 0)
            ops.Add(new Op("threat_overrides", OpKind.Remove, "ThreatIDDefaultAction_Ids",
                p.ThreatIdDefaultActionIds.ToArray(), Hardening: false));

        foreach (var x in p.ExclusionPath.Where(x => !IsAllowed(x, allowed)))
            ops.Add(new Op("exclusion_path:" + x, OpKind.Remove, "ExclusionPath", new[] { x }, false));
        foreach (var x in p.ExclusionProcess.Where(x => !IsAllowed(x, allowed)))
            ops.Add(new Op("exclusion_process:" + x, OpKind.Remove, "ExclusionProcess", new[] { x }, false));
        foreach (var x in p.ExclusionExtension.Where(x => !IsAllowed(x, allowed)))
            ops.Add(new Op("exclusion_extension:" + x, OpKind.Remove, "ExclusionExtension", new[] { x }, false));
        // ASR-only exclusions were never audited; a Temp folder there turns off ASR for the
        // most common malware drop zone. Same allowlist as the other exclusion types.
        foreach (var x in p.AsrOnlyExclusions.Where(x => !IsAllowed(x, allowed)))
            ops.Add(new Op("asr_exclusion:" + x, OpKind.Remove, "AttackSurfaceReductionOnlyExclusions", new[] { x }, false));
        return ops;
    }

    /// <summary>Product self-exclusions that are missing (path, process, ASR-only).</summary>
    public static List<Op> MissingProductExclusions(DefenderWmi.Prefs p, ProductAvExclusions.Set product)
    {
        var ops = new List<Op>();
        var curPaths = NormSet(p.ExclusionPath);
        var curAsr = NormSet(p.AsrOnlyExclusions);
        foreach (var x in product.Paths)
        {
            var n = ProductAvExclusions.NormalizePath(x);
            if (n.Length == 0 || ProductAvExclusions.IsBroadOrUnsafeExclusion(n)) continue;
            if (!curPaths.Contains(n))
                ops.Add(new Op("product_exclusion_path:" + n, OpKind.Add, "ExclusionPath", new[] { n }, true));
            if (!curAsr.Contains(n))
                ops.Add(new Op("product_asr_exclusion:" + n, OpKind.Add, "AttackSurfaceReductionOnlyExclusions", new[] { n }, true));
        }
        foreach (var x in product.Processes)
        {
            var t = x.Trim();
            if (t.Length == 0) continue;
            var tn = ProductAvExclusions.NormalizePath(t);
            var hit = p.ExclusionProcess.Any(c =>
                c.Trim().Equals(t, StringComparison.OrdinalIgnoreCase) ||
                (tn.Length > 0 && ProductAvExclusions.NormalizePath(c).Equals(tn, StringComparison.OrdinalIgnoreCase)));
            if (!hit)
                ops.Add(new Op("product_exclusion_process:" + t, OpKind.Add, "ExclusionProcess", new[] { t }, true));
        }
        return ops;
    }

    /// <summary>Opt-in hardening (email scan, catch-up scans, CPU limit, ASR, CFA).</summary>
    public static List<Op> Hardening(
        DefenderWmi.Prefs p, DefenderGuard.HardeningOptions h, IReadOnlyList<string> asrRuleIds,
        IReadOnlyList<string> cfaApps)
    {
        var ops = new List<Op>();
        if (!h.Enabled) return ops;
        void H(bool need, string name, string param, object value)
        {
            if (need) ops.Add(new Op(name, OpKind.Set, param, value, Hardening: true));
        }

        H(p.DisableEmailScanning, "email_scan", "DisableEmailScanning", false);
        H(p.DisableCatchupQuickScan, "catchup_quick_scan", "DisableCatchupQuickScan", false);
        H(p.DisableCatchupFullScan, "catchup_full_scan", "DisableCatchupFullScan", false);
        H(!p.CheckForSignaturesBeforeRunningScan, "signature_before_scan", "CheckForSignaturesBeforeRunningScan", true);
        var cpu = Math.Clamp(h.ScanCpuLimit, 0, 100);
        H(cpu > 0 && p.ScanAvgCPULoadFactor != cpu, "scan_cpu_limit", "ScanAvgCPULoadFactor", (byte)cpu);
        var purge = Math.Max(0, h.QuarantinePurgeDays);
        H(purge > 0 && p.QuarantinePurgeItemsAfterDelay != purge, "quarantine_purge", "QuarantinePurgeItemsAfterDelay", (uint)purge);

        // ASR rules: bring each wanted rule to the wanted action (1 block / 2 audit).
        var want = (byte)(h.AsrAudit ? 2 : 1);
        var current = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < p.AsrRuleIds.Count; i++)
            current[p.AsrRuleIds[i]] = i < p.AsrRuleActions.Count ? p.AsrRuleActions[i] : 0;
        foreach (var id in asrRuleIds)
        {
            if (!current.TryGetValue(id, out var act) || act != want)
                ops.Add(new Op("asr:" + id[..Math.Min(8, id.Length)], OpKind.AddAsrRule, id, want, Hardening: true));
        }

        // Controlled Folder Access — only when opted in.
        if (h.ControlledFolderAccess)
        {
            var cfaWant = h.CfaAudit ? 2 : 1;
            H(p.EnableControlledFolderAccess != cfaWant,
                "controlled_folder_access:" + (h.CfaAudit ? "AuditMode" : "Enabled"),
                "EnableControlledFolderAccess", (byte)cfaWant);
            var cur = NormSet(p.CfaAllowedApplications);
            foreach (var app in cfaApps)
            {
                var n = ProductAvExclusions.NormalizePath(app);
                if (n.Length > 0 && !cur.Contains(n))
                    ops.Add(new Op("cfa_allow:" + n, OpKind.Add, "ControlledFolderAccessAllowedApplications", new[] { n }, true));
            }
        }
        return ops;
    }

    /// <summary>Weakenings Windows refused to fix (re-read after writes).</summary>
    public static List<string> RemainingDrift(DefenderWmi.Prefs p, HashSet<string> allowed)
    {
        var d = new List<string>();
        if (p.DisableRealtimeMonitoring) d.Add("realtime");
        if (p.DisableBehaviorMonitoring) d.Add("behavior");
        if (p.DisableIOAVProtection) d.Add("download_scan");
        if (p.DisableScriptScanning) d.Add("script_scan");
        if (p.DisableBlockAtFirstSeen) d.Add("block_at_first_sight");
        if (p.DisableArchiveScanning) d.Add("archive_scan");
        if (p.DisableRemovableDriveScanning) d.Add("removable_scan");
        if (p.MAPSReporting != 2) d.Add("cloud_protection");
        if (p.SubmitSamplesConsent == 2) d.Add("sample_submission");
        if (p.CloudBlockLevel < 2) d.Add("cloud_block_level");
        if (p.PUAProtection != 1) d.Add("pua");
        if (p.EnableNetworkProtection != 1) d.Add("network_protection");
        if (new[] { p.LowThreatDefaultAction, p.ModerateThreatDefaultAction, p.HighThreatDefaultAction, p.SevereThreatDefaultAction }
            .Any(LetItRun)) d.Add("threat_action");
        if (p.ThreatIdDefaultActionIds.Count > 0) d.Add("threat_overrides");
        if (p.ExclusionPath.Concat(p.ExclusionProcess).Concat(p.ExclusionExtension).Concat(p.AsrOnlyExclusions)
            .Any(x => !IsAllowed(x, allowed))) d.Add("exclusions");
        return d;
    }

    private static HashSet<string> NormSet(IEnumerable<string> values)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in values)
        {
            var n = ProductAvExclusions.NormalizePath(v);
            if (n.Length > 0) set.Add(n);
        }
        return set;
    }

    // ── Defender event classification (detection loop guard) ───────────────

    public enum SelfEventKind { None, ProductFile, ProductCommand }

    /// <summary>
    /// <see cref="SelfEventKind.ProductFile"/>: Defender acted on a product file (heal it).
    /// <see cref="SelfEventKind.ProductCommand"/>: Defender flagged a command line the product
    /// itself launched (resource <c>CmdLine:_…</c>, process = product). Re-running the guard
    /// would only launch the same command again, so it must NOT trigger self-protection.
    /// </summary>
    public static SelfEventKind ClassifySelfEvent(
        IReadOnlyDictionary<string, string> data, ProductAvExclusions.Set product)
    {
        var resources = data.TryGetValue("Path", out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        var fileResources = resources
            .Where(r => !r.StartsWith("CmdLine:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (fileResources.Any(r => ProductAvExclusions.IsProductPath(r, product)))
            return SelfEventKind.ProductFile;

        var processIsProduct = data.TryGetValue("Process Name", out var proc) &&
                               ProductAvExclusions.IsProductPath(proc, product);
        if (!processIsProduct)
            return SelfEventKind.None;

        // Command-line detection of something the product launched (no file of ours).
        if (resources.Length > 0 && fileResources.Count == 0)
            return SelfEventKind.ProductCommand;
        // ASR block / action against a file launched by a product process — heal as before.
        return SelfEventKind.ProductFile;
    }
}
