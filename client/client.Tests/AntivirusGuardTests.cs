using AlphaAITracker.Service;
using Xunit;

namespace client.Tests;

/// <summary>
/// Antivirus guard regression tests: the Defender detect → heal → detect loop, cooldown /
/// backoff, exclusion audit (no duplicate adds, no broad / Temp exclusions) and user-profile
/// discovery. Pure logic only, except the read-only WMI integration test at the bottom.
/// </summary>
public class AntivirusGuardTests
{
    private const string Install = @"C:\Program Files\Alpha AI Tracker";
    private const string Data = @"C:\ProgramData\AlphaAITracker";
    private const string UserData = @"C:\Users\team\AppData\Local\AlphaAITracker";
    private const string ServiceExe = @"C:\Program Files\Alpha AI Tracker\Service\AlphaAITracker.Service.exe";

    private static readonly ProductAvExclusions.Set Product = new(
        [Install, Data, UserData],
        [ServiceExe, "AlphaAITracker.Service.exe", "client.exe"],
        [ServiceExe]);

    private static DefenderWmi.Prefs Healthy(
        IReadOnlyList<string>? paths = null,
        IReadOnlyList<string>? procs = null,
        IReadOnlyList<string>? asr = null) => new()
    {
        MAPSReporting = 2,
        CloudBlockLevel = 2,
        CloudExtendedTimeout = 50,
        PUAProtection = 1,
        EnableNetworkProtection = 1,
        ExclusionPath = paths ?? [Install, Data, UserData],
        ExclusionProcess = procs ?? [ServiceExe, "AlphaAITracker.Service.exe", "client.exe"],
        AsrOnlyExclusions = asr ?? [Install, Data, UserData],
    };

    // ── Detection loop (items 2–4) ─────────────────────────────────────────

    [Fact]
    public void CommandLineDetection_ByProductProcess_IsProductCommand_NotHeal()
    {
        // Exactly what the lab PC logged 332×/day: the guard's own PowerShell flagged as
        // Trojan:Win32/PowExcEnv with the service as the launching process.
        var data = new Dictionary<string, string>
        {
            ["Path"] = @"CmdLine:_C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -EncodedCommand JABF",
            ["Process Name"] = ServiceExe,
            ["Threat Name"] = "Trojan:Win32/PowExcEnv.ZB!MTB",
        };
        Assert.Equal(DefenderPlan.SelfEventKind.ProductCommand, DefenderPlan.ClassifySelfEvent(data, Product));
    }

    [Fact]
    public void QuarantinedProductFile_IsProductFile()
    {
        var data = new Dictionary<string, string>
        {
            ["Path"] = @"file:_C:\Program Files\Alpha AI Tracker\onnxruntime.dll",
            ["Process Name"] = "Unknown",
        };
        Assert.Equal(DefenderPlan.SelfEventKind.ProductFile, DefenderPlan.ClassifySelfEvent(data, Product));
    }

    [Fact]
    public void UnrelatedMalware_IsNotSelfEvent()
    {
        var data = new Dictionary<string, string>
        {
            ["Path"] = @"file:_C:\Users\team\Downloads\evil.exe",
            ["Process Name"] = @"C:\Windows\explorer.exe",
        };
        Assert.Equal(DefenderPlan.SelfEventKind.None, DefenderPlan.ClassifySelfEvent(data, Product));
    }

    [Fact]
    public void Cooldown_StartsAt60Minutes_AndBacksOffExponentially_CappedAt24h()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), AvSelfProtection.CooldownFor(0));
        Assert.Equal(TimeSpan.FromMinutes(120), AvSelfProtection.CooldownFor(1));
        Assert.Equal(TimeSpan.FromMinutes(240), AvSelfProtection.CooldownFor(2));
        Assert.Equal(TimeSpan.FromHours(24), AvSelfProtection.CooldownFor(10));
        Assert.Equal(TimeSpan.FromHours(24), AvSelfProtection.CooldownFor(50));
    }

    [Fact]
    public void AutomaticTrigger_RespectsPersistedCooldown_PipeDoesNot()
    {
        var now = DateTimeOffset.UtcNow;
        var state = AvSelfProtection.NextState(null, ok: false, ["boom"], now);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.Equal(now + TimeSpan.FromMinutes(120), state.NextAllowedUtc);

        // Event / startup inside the window (e.g. right after a service restart) → skipped.
        Assert.Equal("cooldown", AvSelfProtection.SkipReason(
            AvSelfProtection.Trigger.Event, now.AddMinutes(5), DateTimeOffset.MinValue, state));
        Assert.Equal("cooldown", AvSelfProtection.SkipReason(
            AvSelfProtection.Trigger.Startup, now.AddMinutes(5), DateTimeOffset.MinValue, state));
        // After the window → runs.
        Assert.Null(AvSelfProtection.SkipReason(
            AvSelfProtection.Trigger.Event, now.AddMinutes(121), DateTimeOffset.MinValue, state));
        // Pipe repair ignores the cooldown, keeps the 30 s floor.
        Assert.Null(AvSelfProtection.SkipReason(
            AvSelfProtection.Trigger.Pipe, now.AddMinutes(5), DateTimeOffset.MinValue, state));
        Assert.Equal("min_interval", AvSelfProtection.SkipReason(
            AvSelfProtection.Trigger.Pipe, now.AddSeconds(10), now, state));
    }

    [Fact]
    public void SuccessfulRun_ResetsFailures()
    {
        var now = DateTimeOffset.UtcNow;
        var failed = AvSelfProtection.NextState(null, false, ["x"], now);
        var ok = AvSelfProtection.NextState(failed, true, [], now);
        Assert.Equal(0, ok.ConsecutiveFailures);
        Assert.Equal(now + TimeSpan.FromMinutes(60), ok.NextAllowedUtc);
    }

    // ── Exclusions (items 5, 7, 8) ─────────────────────────────────────────

    [Fact]
    public void ExistingProductExclusions_AreNotReAdded()
    {
        Assert.Empty(DefenderPlan.MissingProductExclusions(Healthy(), Product));
        var allowed = DefenderPlan.BuildAllowed(Product, []);
        Assert.Empty(DefenderPlan.TamperFixes(Healthy(), allowed));
    }

    [Fact]
    public void ExclusionMatch_IsCaseAndSlashInsensitive()
    {
        var prefs = Healthy(paths: [Install.ToUpperInvariant() + "\\", Data.ToLowerInvariant(), UserData]);
        Assert.DoesNotContain(DefenderPlan.MissingProductExclusions(prefs, Product),
            o => o.Param == "ExclusionPath");
    }

    [Fact]
    public void MissingProductExclusions_AreAddedOnce()
    {
        var ops = DefenderPlan.MissingProductExclusions(Healthy(paths: [Install], asr: [Install], procs: []), Product);
        Assert.Contains(ops, o => o.Param == "ExclusionPath" && ((string[])o.Value)[0] == Data);
        Assert.Contains(ops, o => o.Param == "AttackSurfaceReductionOnlyExclusions" && ((string[])o.Value)[0] == UserData);
        Assert.Equal(3, ops.Count(o => o.Param == "ExclusionProcess"));
        Assert.All(ops, o => Assert.True(o.Hardening)); // never reported as tamper
    }

    [Fact]
    public void UnauthorizedTempAsrExclusion_IsRemoved()
    {
        var prefs = Healthy(asr: [Install, Data, UserData,
            @"C:\Users\team\AppData\Local\Temp", @"C:\Users\team\Desktop\Alpha Customs\server\setup"]);
        var ops = DefenderPlan.TamperFixes(prefs, DefenderPlan.BuildAllowed(Product, []));
        Assert.Contains(ops, o => o.Kind == DefenderPlan.OpKind.Remove &&
                                  o.Param == "AttackSurfaceReductionOnlyExclusions" &&
                                  ((string[])o.Value)[0] == @"C:\Users\team\AppData\Local\Temp");
        Assert.Contains(ops, o => o.Name.Contains(@"server\setup"));
    }

    [Fact]
    public void BroadItExclusion_IsRejected_EvenWhenAllowlisted()
    {
        var allowed = DefenderPlan.BuildAllowed(Product, [@"C:\Users\team\AppData\Local\Temp", @"D:\Tools\Scanner"]);
        Assert.DoesNotContain(@"C:\Users\team\AppData\Local\Temp", allowed);
        Assert.Contains(@"D:\Tools\Scanner", allowed);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Windows", true)]
    [InlineData(@"C:\Windows\Temp", true)]
    [InlineData(@"C:\Users", true)]
    [InlineData(@"C:\Users\team", true)]
    [InlineData(@"C:\Users\team\AppData\Local", true)]
    [InlineData(@"C:\Users\team\AppData\Local\Temp\x", true)]
    [InlineData(@"C:\Users\team\AppData\Local\AlphaAITracker", false)]
    [InlineData(@"C:\Program Files\Alpha AI Tracker", false)]
    [InlineData("client.exe", false)]
    public void BroadOrUnsafeExclusion(string path, bool expected) =>
        Assert.Equal(expected, ProductAvExclusions.IsBroadOrUnsafeExclusion(path));

    [Fact]
    public void WeakSettings_ProduceTamperFixes_WithDefenderValues()
    {
        var prefs = new DefenderWmi.Prefs
        {
            DisableRealtimeMonitoring = true,
            MAPSReporting = 0,
            CloudBlockLevel = 0,
            CloudExtendedTimeout = 0,
            PUAProtection = 0,
            EnableNetworkProtection = 0,
            HighThreatDefaultAction = 6, // Allow
            ThreatIdDefaultActionIds = [2147943074],
            ExclusionPath = [Install, Data, UserData],
            ExclusionProcess = [ServiceExe, "AlphaAITracker.Service.exe", "client.exe"],
            AsrOnlyExclusions = [Install, Data, UserData],
        };
        var ops = DefenderPlan.TamperFixes(prefs, DefenderPlan.BuildAllowed(Product, []));
        Assert.Contains(ops, o => o.Name == "realtime" && (bool)o.Value == false);
        Assert.Contains(ops, o => o.Name == "cloud_protection" && (byte)o.Value == 2);
        Assert.Contains(ops, o => o.Name == "threat_action_high" && (byte)o.Value == 3);
        Assert.Contains(ops, o => o.Name == "threat_overrides" && o.Kind == DefenderPlan.OpKind.Remove);
        Assert.All(ops, o => Assert.False(o.Hardening));
        Assert.Contains("realtime", DefenderPlan.RemainingDrift(prefs, DefenderPlan.BuildAllowed(Product, [])));
    }

    [Fact]
    public void Hardening_Off_ProducesNoOps()
    {
        Assert.Empty(DefenderPlan.Hardening(Healthy(), DefenderGuard.HardeningOptions.Off, ["be9ba2d9-53ea-4cdc-84e5-9b1eeee46550"], []));
    }

    [Fact]
    public void Hardening_AsrRule_OnlyWhenActionDiffers()
    {
        const string id = "be9ba2d9-53ea-4cdc-84e5-9b1eeee46550";
        var opts = new DefenderGuard.HardeningOptions(true, false, false, false, 0, 0);
        var set = new DefenderWmi.Prefs { AsrRuleIds = [id], AsrRuleActions = [1], CheckForSignaturesBeforeRunningScan = true };
        Assert.DoesNotContain(DefenderPlan.Hardening(set, opts, [id], []), o => o.Kind == DefenderPlan.OpKind.AddAsrRule);
        var audit = set with { };
        var auditOpts = opts with { AsrAudit = true };
        Assert.Contains(DefenderPlan.Hardening(audit, auditOpts, [id], []), o => o.Kind == DefenderPlan.OpKind.AddAsrRule);
    }

    // ── Profile discovery (item 6) ─────────────────────────────────────────

    [Theory]
    [InlineData("S-1-5-21-1111-2222-3333-1001", true)]
    [InlineData("S-1-5-18", false)]
    [InlineData("S-1-5-19", false)]
    [InlineData("S-1-5-20", false)]
    [InlineData("S-1-5-21-1111-2222-3333-1001.bak", false)]
    public void OnlyUserSids_AreProfiles(string sid, bool expected) =>
        Assert.Equal(expected, ProductAvExclusions.IsUserSid(sid));

    [Fact]
    public void ProductExclusions_NeverContainWindowsDirOrBroadPaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var dir in ProductAvExclusions.EnumerateUserProfileDirs())
            Assert.False(ProductAvExclusions.IsUnderWindowsDir(dir), dir);
        var set = ProductAvExclusions.Build();
        Assert.DoesNotContain(set.Paths, p => p.Contains(@"\system32\config\", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(set.Paths, ProductAvExclusions.IsBroadOrUnsafeExclusion);
    }

    // ── Integration: real Defender WMI (read-only) ─────────────────────────

    [Fact]
    public void DefenderWmi_ReadsStatusAndPrefs_WithoutPowerShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        DefenderWmi.Prefs prefs;
        try { prefs = DefenderWmi.ReadPrefs(); }
        catch (Exception) { return; } // Defender absent / third-party AV owns the PC
        Assert.InRange(prefs.MAPSReporting, 0, 2);
        var status = DefenderGuard.ReadStatus();
        Assert.True(status.Available || status.ThirdPartyProducts.Count > 0 || status.Error.Length > 0);
        if (status.Available)
            Assert.False(string.IsNullOrEmpty(status.EngineVersion));
    }
}
