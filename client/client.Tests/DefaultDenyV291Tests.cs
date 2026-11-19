using System.Net;
using AlphaAITracker.Service;
using Xunit;

namespace client.Tests;

public class DefaultDenyV291Tests
{
    [Fact]
    public void ActiveStore_DisabledProfile_Fails()
    {
        var verdict = FirewallActiveStoreProof.Evaluate(
            querySucceeded: true,
            active: Profiles(domainEnabled: false),
            persistent: Profiles(domainEnabled: true),
            quic: GoodQuic(),
            outboundAllows: []);
        Assert.False(verdict.Ok);
        Assert.StartsWith(FirewallActiveStoreProof.DisabledDetail, verdict.Detail);
    }

    [Fact]
    public void ActiveStore_AllowWhilePersistentBlock_Disagreed()
    {
        var active = new[]
        {
            new FirewallActiveStoreProof.ProfileRow("Domain", true, false),
            new FirewallActiveStoreProof.ProfileRow("Private", true, true),
            new FirewallActiveStoreProof.ProfileRow("Public", true, true),
        };
        var persistent = Profiles(domainEnabled: true);
        var verdict = FirewallActiveStoreProof.Evaluate(true, active, persistent, GoodQuic(), []);
        Assert.False(verdict.Ok);
        Assert.Equal(FirewallActiveStoreProof.DisagreeDetail, verdict.Detail);
    }

    [Fact]
    public void ActiveStore_QueryThrow_FailsClosed()
    {
        var verdict = FirewallActiveStoreProof.Parse("not-json");
        Assert.False(verdict.Ok);
        Assert.StartsWith(FirewallActiveStoreProof.QueryFailedDetail, verdict.Detail);
    }

    [Fact]
    public void ActiveStore_ExtractJson_UsesMarker()
    {
        var stdout = "noise\r\nALPHA_ACTIVE_STORE_JSON:{\"active\":[]}\r\n";
        Assert.Equal("{\"active\":[]}", FirewallActiveStoreProof.ExtractJson(stdout));
        Assert.Null(FirewallActiveStoreProof.ExtractJson("no marker here"));
    }

    [Fact]
    public void ActiveStore_JsonDisabledProfile_Fails()
    {
        const string json = """
            {"active":[
              {"Name":"Domain","Enabled":"False","DefaultOutboundAction":"Block"},
              {"Name":"Private","Enabled":"True","DefaultOutboundAction":"Block"},
              {"Name":"Public","Enabled":"True","DefaultOutboundAction":"Block"}
            ],"persistent":[],"quic":{"present":true,"enabled":"True","action":"Block","direction":"Outbound","protocol":"UDP","remotePort":"443"},"allows":[]}
            """;
        var verdict = FirewallActiveStoreProof.Parse(json);
        Assert.False(verdict.Ok);
        Assert.Contains(FirewallActiveStoreProof.DisabledDetail, verdict.Detail);
    }

    [Fact]
    public void Quic_ProductUdp443Allow_FailsEvenWhenDefaultBlockIsOn()
    {
        var allows = new[]
        {
            new FirewallActiveStoreProof.AllowRow("AlphaAI-DLP-LOCK-ALLOW-DEST-UDP", "UDP", "1-65535"),
        };
        var verdict = FirewallActiveStoreProof.Evaluate(true, Profiles(true), Profiles(true), GoodQuic(), allows);
        Assert.False(verdict.Ok);
        Assert.StartsWith(FirewallActiveStoreProof.Udp443AllowDetail, verdict.Detail);
    }

    [Fact]
    public void Quic_StoreAppUdpAnyAllow_IsIgnored()
    {
        var allows = new[]
        {
            new FirewallActiveStoreProof.AllowRow("Start", "Any", "Any"),
            new FirewallActiveStoreProof.AllowRow("Windows Search", "UDP", "Any"),
        };
        var verdict = FirewallActiveStoreProof.Evaluate(true, Profiles(true), Profiles(true), GoodQuic(), allows);
        Assert.True(verdict.Ok);
        Assert.False(FirewallActiveStoreProof.IsProductUdp443PunchThrough("Start", "Any", "Any"));
        Assert.True(FirewallActiveStoreProof.IsProductUdp443PunchThrough(
            "AlphaAI-DLP-LOCK-ALLOW-DEST-UDP", "UDP", "1-65535"));
    }

    [Fact]
    public void Quic_MissingRule_Fails()
    {
        var verdict = FirewallActiveStoreProof.Evaluate(
            true, Profiles(true), Profiles(true),
            new FirewallActiveStoreProof.QuicRow(false, false, "", "", "", ""),
            []);
        Assert.False(verdict.Ok);
        Assert.Equal(FirewallActiveStoreProof.QuicMissingDetail, verdict.Detail);
    }

    [Fact]
    public void DestRules_DoNotOpenAllUdpOrPortRange()
    {
        var rules = LockdownNetworkEnforcer.BuildDesiredAllowRules(
            [IPAddress.Parse("192.168.88.35")],
            8000,
            [IPAddress.Parse("203.0.113.10")],
            [IPAddress.Parse("203.0.113.11")],
            [IPAddress.Parse("203.0.113.12")]);
        Assert.DoesNotContain(rules.Keys, k => k.Contains("DEST-UDP", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rules.Keys, k => k.StartsWith(LockdownNetworkEnforcer.AllowMgmtTcpBase + "-8000", StringComparison.OrdinalIgnoreCase));
        foreach (var name in rules.Keys)
        {
            var spec = LockdownNetworkEnforcer.SpecForAllow(name);
            Assert.NotEqual("1-65535", spec.Port);
            Assert.False(FirewallActiveStoreProof.AllowCoversUdp443(spec.Proto, spec.Port));
        }
        var mgmt = LockdownNetworkEnforcer.SpecForAllow(
            rules.Keys.First(k => k.StartsWith(LockdownNetworkEnforcer.AllowMgmtTcpBase, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(("tcp", "8000", ""), mgmt);
        Assert.Equal(LockdownNetworkEnforcer.QuicBlockRuleName, "AlphaAI-DLP-LOCK-BLOCK-QUIC");
        Assert.Equal("Block QUIC", LockdownNetworkEnforcer.QuicBlockDisplayName);
    }

    [Fact]
    public void ManagementPort_FollowsSchemeAndExplicitPort()
    {
        Assert.Equal(443, LockdownNetworkEnforcer.ManagementTcpPort("https://192.168.88.35"));
        Assert.Equal(80, LockdownNetworkEnforcer.ManagementTcpPort("http://192.168.88.35"));
        Assert.Equal(8000, LockdownNetworkEnforcer.ManagementTcpPort("https://192.168.88.35:8000"));
        Assert.Equal(443, LockdownNetworkEnforcer.ManagementTcpPort("192.168.88.35"));
    }

    [Fact]
    public void ManagementLastIp_RequiresSuccessfulTcp()
    {
        var ok = LockdownNetworkEnforcer.EvaluateBehavioralEgress(
            publicConnected: false, managementConnected: true, usedLastSavedIp: true);
        Assert.True(ok.Ok);
        Assert.Contains("last_ip", ok.Detail);

        var offlineLast = LockdownNetworkEnforcer.EvaluateBehavioralEgress(
            publicConnected: false, managementConnected: false, usedLastSavedIp: true);
        Assert.True(offlineLast.Ok);
        Assert.Equal("egress_probe_ok;mgmt=offline_last_ip", offlineLast.Detail);
    }

    [Fact]
    public void ManagementUnreachable_IsOfflineFirstPass()
    {
        var offline = LockdownNetworkEnforcer.EvaluateBehavioralEgress(
            publicConnected: false, managementConnected: false, usedLastSavedIp: false);
        Assert.True(offline.Ok);
        Assert.Equal("egress_probe_ok;mgmt=offline", offline.Detail);

        // Public reachable still fails even when offline from management.
        var leak = LockdownNetworkEnforcer.EvaluateBehavioralEgress(true, false, false);
        Assert.False(leak.Ok);
        Assert.Equal("egress_probe_public_reachable", leak.Detail);
    }

    [Fact]
    public void ForeignDisabled_ParsesMarkersAndNeverProductRules()
    {
        var names = LockdownNetworkEnforcer.ParseForeignDisabled(
            "noise\r\nALPHA_FOREIGN_DISABLED:{ABC-123}\nALPHA_FOREIGN_DISABLED:SPOOLSV-Out\n" +
            "ALPHA_FOREIGN_DISABLED:AlphaAI-DLP-LOCK-ALLOW-DHCP\nALPHA_FOREIGN_DISABLED:   \n");
        Assert.Equal(new[] { "{ABC-123}", "SPOOLSV-Out" }, names);
        Assert.Empty(LockdownNetworkEnforcer.ParseForeignDisabled(null));
    }

    [Fact]
    public void PublicIpProbe_IsNotSuccess()
    {
        var bad = LockdownNetworkEnforcer.EvaluateBehavioralEgress(true, true, false);
        Assert.False(bad.Ok);
        Assert.Equal("egress_probe_public_reachable", bad.Detail);
    }

    [Fact]
    public void Capability_UnknownKb_DoesNotClaimInterim()
    {
        var facts = new AppControlFacts(22621, null, false, null, AppLockerChannel.Local);
        var assessment = AppControlCapability.Assess(facts);
        Assert.False(assessment.CanClaimInterim);
        Assert.Equal("kb_unknown", assessment.Reason);
    }

    [Fact]
    public void Capability_HigherKbName_IsNotSupersedingEvidence()
    {
        var facts = new AppControlFacts(22621, 100, true, ["KB5039999"], AppLockerChannel.Local);
        var assessment = AppControlCapability.Assess(facts);
        Assert.False(assessment.CanClaimInterim);
        Assert.Equal("kb_absent", assessment.Reason);
    }

    [Fact]
    public void Capability_ExactKbOrUbrFloor_Claims()
    {
        var byKb = AppControlCapability.Assess(
            new AppControlFacts(19041, null, true, ["KB5018482"], AppLockerChannel.Local));
        Assert.True(byKb.CanClaimInterim);
        Assert.Equal("kb_exact", byKb.Reason);

        var byUbr = AppControlCapability.Assess(
            new AppControlFacts(22621, 608, false, null, AppLockerChannel.Local));
        Assert.True(byUbr.CanClaimInterim);
        Assert.Equal("ubr_baseline", byUbr.Reason);

        var old = AppControlCapability.Assess(
            new AppControlFacts(17763, 5000, true, ["KB5024351"], AppLockerChannel.Local));
        Assert.False(old.CanClaimInterim);
        Assert.Equal("build_too_old", old.Reason);
    }

    [Fact]
    public void GpOnPro_CanaryNotDenied_FailsClosed()
    {
        var decision = AppControlCapability.Decide(
            production: true,
            isPro: true,
            hasOrgCip: false,
            secureBoot: true,
            cipFingerprintMatch: false,
            canaryDenied: false,
            canaryThrew: false,
            assessment: new AppControlAssessment(true, "kb_exact"),
            channel: AppLockerChannel.Gp,
            blockRulesAttested: false);
        Assert.False(decision.OverallEnforcing);
        Assert.Contains("applocker_gp_unsupported_on_pro", decision.AppLockerDetail);
    }

    [Fact]
    public void ProWithoutCip_DoesNotReportOverallEnforcing()
    {
        var decision = AppControlCapability.Decide(
            production: true,
            isPro: true,
            hasOrgCip: false,
            secureBoot: true,
            cipFingerprintMatch: false,
            canaryDenied: true,
            canaryThrew: false,
            assessment: new AppControlAssessment(true, "kb_exact"),
            channel: AppLockerChannel.Local,
            blockRulesAttested: false);
        Assert.False(decision.OverallEnforcing);
        Assert.Equal("interim", decision.AppLockerCode);
        Assert.Contains("wdac_cip_required_pro", decision.WdacDetail);
        Assert.Contains("wdac_block_rules=unverified", decision.WdacDetail);
    }

    [Fact]
    public void ProWithoutCip_AcceptAppLockerInterim_ClosesGate()
    {
        // Windows Pro, production, no signed WDAC, AppLocker genuinely enforcing (canary
        // denied). With ALPHA_DLP_APPLOCKER_INTERIM_OK the gate must close: AppLocker is the
        // accepted enforcement level and WDAC drops to best-effort (off), never a hard fail.
        var decision = AppControlCapability.Decide(
            production: true,
            isPro: true,
            hasOrgCip: false,
            secureBoot: false,
            cipFingerprintMatch: false,
            canaryDenied: true,
            canaryThrew: false,
            assessment: new AppControlAssessment(true, "kb_exact"),
            channel: AppLockerChannel.Local,
            blockRulesAttested: false,
            acceptAppLockerInterim: true);
        Assert.True(decision.OverallEnforcing);
        Assert.Equal("ok", decision.AppLockerCode);
        Assert.Equal("off", decision.WdacCode);
        Assert.Contains("wdac_best_effort_applocker_accepted", decision.WdacDetail);
    }

    [Fact]
    public void AcceptAppLockerInterim_WithoutEnforcingAppLocker_StillFails()
    {
        // The accept flag must NOT close the gate when AppLocker is not actually enforcing
        // (canary not denied) — it only accepts a genuine block, never a missing one.
        var decision = AppControlCapability.Decide(
            production: true,
            isPro: true,
            hasOrgCip: false,
            secureBoot: false,
            cipFingerprintMatch: false,
            canaryDenied: false,
            canaryThrew: false,
            assessment: new AppControlAssessment(true, "kb_exact"),
            channel: AppLockerChannel.Local,
            blockRulesAttested: false,
            acceptAppLockerInterim: true);
        Assert.False(decision.OverallEnforcing);
    }

    [Fact]
    public void SecureBoot_OffOrThrow_IsNotTamperResistant()
    {
        Assert.False(WdacEnforcer.IsOrgCipEnforcing("abcdefabcdef", null));
        Assert.False(WdacEnforcer.IsOrgCipEnforcing("abcdefabcdef", false));

        var decision = AppControlCapability.Decide(
            production: true,
            isPro: true,
            hasOrgCip: true,
            secureBoot: null,
            cipFingerprintMatch: true,
            canaryDenied: true,
            canaryThrew: false,
            assessment: new AppControlAssessment(true, "win11_inbox"),
            channel: AppLockerChannel.Mdm,
            blockRulesAttested: true);
        Assert.False(decision.OverallEnforcing);
        Assert.Equal("recovery_required", decision.WdacCode);
        Assert.Contains("wdac_secure_boot_off", decision.WdacDetail);
        Assert.Contains("wdac_block_rules=verified", decision.WdacDetail);
    }

    [Fact]
    public void CanaryThrow_IsNotEnforcingUnlessCipAndSecureBoot()
    {
        var blocked = AppControlCapability.Decide(
            true, true, false, true, false,
            canaryDenied: false, canaryThrew: true,
            new AppControlAssessment(true, "kb_exact"),
            AppLockerChannel.Local, false);
        Assert.False(blocked.OverallEnforcing);
        Assert.Contains("canary_probe_failed", blocked.AppLockerDetail);

        var viaCip = AppControlCapability.Decide(
            true, true, true, true, true,
            canaryDenied: false, canaryThrew: true,
            new AppControlAssessment(true, "kb_exact"),
            AppLockerChannel.Local, false);
        Assert.True(viaCip.OverallEnforcing);
        Assert.Contains("canary_probe_failed", viaCip.AppLockerDetail);
    }

    [Fact]
    public void GatePhases_VerifyingAndRecovery_AreNotEnforcing()
    {
        var status = new EnforceStatus();
        status.BeginApply(true);
        status.MarkVerifying();
        Assert.Contains("\"phase\":\"verifying\"", status.ToJson());
        Assert.False(status.IsEnforcing);

        status.SetLayer("wdac", "recovery_required", "wdac_secure_boot_off");
        status.FinishApply();
        var json = status.ToJson();
        Assert.Contains("\"phase\":\"recovery_required\"", json);
        Assert.False(status.IsEnforcing);
    }

    private static FirewallActiveStoreProof.ProfileRow[] Profiles(bool domainEnabled) =>
    [
        new FirewallActiveStoreProof.ProfileRow("Domain", domainEnabled, true),
        new FirewallActiveStoreProof.ProfileRow("Private", true, true),
        new FirewallActiveStoreProof.ProfileRow("Public", true, true),
    ];

    private static FirewallActiveStoreProof.QuicRow GoodQuic() =>
        new(true, true, "Block", "Outbound", "UDP", "443");
}
