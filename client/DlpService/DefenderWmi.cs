using System.Management;
using System.Runtime.Versioning;

namespace AlphaAITracker.Service;

/// <summary>
/// Native Microsoft Defender management through its own WMI provider
/// (<c>root\Microsoft\Windows\Defender</c>). The Defender PowerShell cmdlets
/// (<c>Get/Set/Add/Remove-MpPreference</c>, <c>Get-MpComputerStatus</c>, <c>Start-MpScan</c>,
/// <c>Update-MpSignature</c>, <c>Remove-MpThreat</c>) are thin CDXML wrappers over exactly
/// these classes, so behaviour is identical — but no <c>powershell.exe -EncodedCommand</c>
/// process is started. Defender classifies an encoded PowerShell that edits its own
/// exclusions as <c>Trojan:Win32/PowExcEnv</c>, which caused a detect → heal → detect loop.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DefenderWmi
{
    private const string DefenderNs = @"\\.\root\Microsoft\Windows\Defender";

    /// <summary>Snapshot of the MSFT_MpPreference fields the guard evaluates.</summary>
    public sealed record Prefs
    {
        public bool DisableRealtimeMonitoring { get; init; }
        public bool DisableBehaviorMonitoring { get; init; }
        public bool DisableIOAVProtection { get; init; }
        public bool DisableScriptScanning { get; init; }
        public bool DisableBlockAtFirstSeen { get; init; }
        public bool DisableArchiveScanning { get; init; }
        public bool DisableRemovableDriveScanning { get; init; }
        public bool DisableEmailScanning { get; init; }
        public bool DisableCatchupQuickScan { get; init; }
        public bool DisableCatchupFullScan { get; init; }
        public bool CheckForSignaturesBeforeRunningScan { get; init; }
        public int MAPSReporting { get; init; }
        public int SubmitSamplesConsent { get; init; }
        public int CloudBlockLevel { get; init; }
        public long CloudExtendedTimeout { get; init; }
        public int PUAProtection { get; init; }
        public int EnableNetworkProtection { get; init; }
        public int EnableControlledFolderAccess { get; init; }
        public int LowThreatDefaultAction { get; init; }
        public int ModerateThreatDefaultAction { get; init; }
        public int HighThreatDefaultAction { get; init; }
        public int SevereThreatDefaultAction { get; init; }
        public int ScanAvgCPULoadFactor { get; init; }
        public long QuarantinePurgeItemsAfterDelay { get; init; }
        public IReadOnlyList<long> ThreatIdDefaultActionIds { get; init; } = [];
        public IReadOnlyList<string> ExclusionPath { get; init; } = [];
        public IReadOnlyList<string> ExclusionProcess { get; init; } = [];
        public IReadOnlyList<string> ExclusionExtension { get; init; } = [];
        public IReadOnlyList<string> AsrOnlyExclusions { get; init; } = [];
        public IReadOnlyList<string> AsrRuleIds { get; init; } = [];
        public IReadOnlyList<int> AsrRuleActions { get; init; } = [];
        public IReadOnlyList<string> CfaAllowedApplications { get; init; } = [];
    }

    private static ManagementScope Scope()
    {
        var scope = new ManagementScope(DefenderNs);
        scope.Connect();
        return scope;
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    public static Prefs ReadPrefs()
    {
        using var mo = FirstInstance("MSFT_MpPreference")
                       ?? throw new InvalidOperationException("MSFT_MpPreference unavailable");
        return new Prefs
        {
            DisableRealtimeMonitoring = B(mo, "DisableRealtimeMonitoring"),
            DisableBehaviorMonitoring = B(mo, "DisableBehaviorMonitoring"),
            DisableIOAVProtection = B(mo, "DisableIOAVProtection"),
            DisableScriptScanning = B(mo, "DisableScriptScanning"),
            DisableBlockAtFirstSeen = B(mo, "DisableBlockAtFirstSeen"),
            DisableArchiveScanning = B(mo, "DisableArchiveScanning"),
            DisableRemovableDriveScanning = B(mo, "DisableRemovableDriveScanning"),
            DisableEmailScanning = B(mo, "DisableEmailScanning"),
            DisableCatchupQuickScan = B(mo, "DisableCatchupQuickScan"),
            DisableCatchupFullScan = B(mo, "DisableCatchupFullScan"),
            CheckForSignaturesBeforeRunningScan = B(mo, "CheckForSignaturesBeforeRunningScan"),
            MAPSReporting = I(mo, "MAPSReporting"),
            SubmitSamplesConsent = I(mo, "SubmitSamplesConsent"),
            CloudBlockLevel = I(mo, "CloudBlockLevel"),
            CloudExtendedTimeout = L(mo, "CloudExtendedTimeout"),
            PUAProtection = I(mo, "PUAProtection"),
            EnableNetworkProtection = I(mo, "EnableNetworkProtection"),
            EnableControlledFolderAccess = I(mo, "EnableControlledFolderAccess"),
            LowThreatDefaultAction = I(mo, "LowThreatDefaultAction"),
            ModerateThreatDefaultAction = I(mo, "ModerateThreatDefaultAction"),
            HighThreatDefaultAction = I(mo, "HighThreatDefaultAction"),
            SevereThreatDefaultAction = I(mo, "SevereThreatDefaultAction"),
            ScanAvgCPULoadFactor = I(mo, "ScanAvgCPULoadFactor"),
            QuarantinePurgeItemsAfterDelay = L(mo, "QuarantinePurgeItemsAfterDelay"),
            ThreatIdDefaultActionIds = Longs(mo, "ThreatIDDefaultAction_Ids"),
            ExclusionPath = Strs(mo, "ExclusionPath"),
            ExclusionProcess = Strs(mo, "ExclusionProcess"),
            ExclusionExtension = Strs(mo, "ExclusionExtension"),
            AsrOnlyExclusions = Strs(mo, "AttackSurfaceReductionOnlyExclusions"),
            AsrRuleIds = Strs(mo, "AttackSurfaceReductionRules_Ids"),
            AsrRuleActions = Longs(mo, "AttackSurfaceReductionRules_Actions").Select(v => (int)v).ToList(),
            CfaAllowedApplications = Strs(mo, "ControlledFolderAccessAllowedApplications"),
        };
    }

    /// <summary>MSFT_MpComputerStatus as a property bag (null when Defender is not present).</summary>
    public static ManagementBaseObject? ReadComputerStatus() => FirstInstance("MSFT_MpComputerStatus");

    public static int CountThreats()
    {
        using var searcher = new ManagementObjectSearcher(Scope(), new ObjectQuery("SELECT ThreatID FROM MSFT_MpThreat"));
        using var results = searcher.Get();
        return results.Count;
    }

    /// <summary>Resources (e.g. <c>file:_C:\…</c>) of every recorded threat detection.</summary>
    public static IReadOnlyList<string> ThreatDetectionResources()
    {
        var list = new List<string>();
        using var searcher = new ManagementObjectSearcher(Scope(),
            new ObjectQuery("SELECT Resources FROM MSFT_MpThreatDetection"));
        using var results = searcher.Get();
        foreach (ManagementBaseObject mo in results)
        {
            using (mo)
                list.AddRange(Strs(mo, "Resources"));
        }
        return list;
    }

    /// <summary>Value of a property in another WMI namespace (Device Guard / Security Center).</summary>
    public static List<ManagementBaseObject> Query(string ns, string wql)
    {
        var scope = new ManagementScope(ns);
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        return results.Cast<ManagementBaseObject>().ToList();
    }

    // ── Writes (static methods on MSFT_MpPreference) ────────────────────────

    /// <summary>Set-MpPreference with one named parameter.</summary>
    public static void Set(string name, object value) => Invoke("MSFT_MpPreference", "Set", (name, value));

    /// <summary>Add-MpPreference with one named parameter (array values).</summary>
    public static void Add(string name, object value) => Invoke("MSFT_MpPreference", "Add", (name, value));

    /// <summary>Remove-MpPreference with one named parameter (array values).</summary>
    public static void Remove(string name, object value) => Invoke("MSFT_MpPreference", "Remove", (name, value));

    /// <summary>Add-MpPreference with ASR rule id + action pairs.</summary>
    public static void AddAsrRule(string id, byte action) =>
        Invoke("MSFT_MpPreference", "Add",
            ("AttackSurfaceReductionRules_Ids", new[] { id }),
            ("AttackSurfaceReductionRules_Actions", new[] { action }));

    /// <summary>Start-MpScan: 1 Quick, 2 Full, 3 Custom (blocks until the scan finishes).</summary>
    public static void StartScan(byte scanType, string? scanPath, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(scanPath))
            Invoke("MSFT_MpScan", "Start", timeout, ("ScanType", scanType));
        else
            Invoke("MSFT_MpScan", "Start", timeout, ("ScanType", scanType), ("ScanPath", scanPath));
    }

    /// <summary>Update-MpSignature (default update source).</summary>
    public static void UpdateSignatures(TimeSpan timeout) => Invoke("MSFT_MpSignature", "Update", timeout);

    /// <summary>Remove-MpThreat (all active threats).</summary>
    public static void RemoveActiveThreats(TimeSpan timeout) => Invoke("MSFT_MpThreat", "Remove", timeout);

    public static int CountActiveThreats()
    {
        using var searcher = new ManagementObjectSearcher(Scope(),
            new ObjectQuery("SELECT ThreatID FROM MSFT_MpThreat WHERE IsActive = TRUE"));
        using var results = searcher.Get();
        return results.Count;
    }

    private static void Invoke(string cls, string method, params (string Name, object Value)[] args) =>
        Invoke(cls, method, TimeSpan.FromMinutes(2), args);

    private static void Invoke(string cls, string method, TimeSpan timeout, params (string Name, object Value)[] args)
    {
        using var mc = new ManagementClass(Scope(), new ManagementPath(cls), null);
        using var inParams = mc.GetMethodParameters(method);
        foreach (var (name, value) in args)
            inParams[name] = value;
        var opts = new InvokeMethodOptions(null, timeout);
        using var result = mc.InvokeMethod(method, inParams, opts);
        // Defender methods report failure through ManagementException (extended status)
        // or a non-zero ReturnValue.
        if (result?["ReturnValue"] is { } rv && Convert.ToInt64(rv) != 0)
            throw new InvalidOperationException($"{cls}.{method} returned 0x{Convert.ToInt64(rv):X}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ManagementBaseObject? FirstInstance(string cls)
    {
        using var searcher = new ManagementObjectSearcher(Scope(), new ObjectQuery("SELECT * FROM " + cls));
        using var results = searcher.Get();
        foreach (ManagementBaseObject mo in results)
            return mo;
        return null;
    }

    private static object? Raw(ManagementBaseObject mo, string name)
    {
        try { return mo[name]; }
        catch (ManagementException) { return null; } // property absent on older builds
    }

    internal static bool B(ManagementBaseObject mo, string name) => Raw(mo, name) is bool b && b;

    internal static int I(ManagementBaseObject mo, string name) =>
        Raw(mo, name) is { } v ? (int)Convert.ToInt64(v) : 0;

    internal static long L(ManagementBaseObject mo, string name) =>
        Raw(mo, name) is { } v ? Convert.ToInt64(v) : 0;

    internal static string S(ManagementBaseObject mo, string name) => Raw(mo, name)?.ToString() ?? "";

    internal static DateTimeOffset? D(ManagementBaseObject mo, string name)
    {
        if (Raw(mo, name) is not string dmtf || dmtf.Length == 0) return null;
        try
        {
            var dt = ManagementDateTimeConverter.ToDateTime(dmtf).ToUniversalTime();
            return dt.Year > 1601 ? new DateTimeOffset(dt, TimeSpan.Zero) : null;
        }
        catch { return null; }
    }

    internal static IReadOnlyList<string> Strs(ManagementBaseObject mo, string name) =>
        Raw(mo, name) is string[] arr
            ? arr.Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
            : [];

    internal static IReadOnlyList<long> Longs(ManagementBaseObject mo, string name)
    {
        if (Raw(mo, name) is not Array arr) return [];
        var list = new List<long>(arr.Length);
        foreach (var v in arr)
            if (v is not null) list.Add(Convert.ToInt64(v));
        return list;
    }
}
