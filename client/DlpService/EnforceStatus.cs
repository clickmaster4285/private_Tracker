using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Last enforce result per layer. The tracker policy screen polls this over the pipe.
/// A layer is ready only when its code is <c>ok</c> or <c>off</c> (not requested).
/// Network lockdown and AppLocker never use <c>off</c> for a switch that is supposed to be on.
/// </summary>
internal sealed class EnforceStatus
{
    public static EnforceStatus Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, Layer> _layers = new(StringComparer.OrdinalIgnoreCase);
    private string _phase = "waiting";
    /// <summary>True when local blockers or DeviceAuth mean Apply must run (not idle-wait).</summary>
    private bool _armed;
    private bool _rebootArmed;
    private bool _rebootQueued;
    private string? _rebootError;
    private string _solution = "";
    private string _failureCode = "";
    private AppBlockNotice? _lastBlock;
    private bool _watchArmed;
    private IReadOnlyList<AllowAppEntry> _watchApps = Array.Empty<AllowAppEntry>();

    private sealed record Layer(string Code, string Detail);

    public sealed record AppBlockNotice(string Id, string Name, string Path, DateTimeOffset At);

    public bool IsEnforcing
    {
        get
        {
            lock (_gate)
                return _phase is "enforcing" or "reboot_queued";
        }
    }

    public void BeginApply(bool armed)
    {
        lock (_gate)
        {
            _armed = armed;
            if (_rebootQueued)
                return;
            _phase = armed ? "applying" : "waiting";
        }
    }

    /// <summary>
    /// ActiveStore, egress, and canary/CIP checks are in flight. Not success.
    /// </summary>
    public void MarkVerifying()
    {
        lock (_gate)
        {
            if (_rebootQueued || !_armed)
                return;
            _phase = "verifying";
        }
    }

    public void SetLayer(string id, string code, string detail)
    {
        lock (_gate)
            _layers[id] = new Layer(code, detail ?? "");
    }

    public void SetWatch(bool armed, IReadOnlyList<AllowAppEntry> apps)
    {
        lock (_gate)
        {
            _watchArmed = armed;
            _watchApps = apps ?? Array.Empty<AllowAppEntry>();
        }
    }

    public (bool Armed, IReadOnlyList<AllowAppEntry> Apps) WatchSnapshot()
    {
        lock (_gate)
            return (_watchArmed, _watchApps);
    }

    public void NoteAppBlock(string name, string path)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastBlock is { } prev
                && string.Equals(prev.Path, path, StringComparison.OrdinalIgnoreCase)
                && now - prev.At < TimeSpan.FromSeconds(5))
                return;
            _lastBlock = new AppBlockNotice(Guid.NewGuid().ToString("N"), name, path, now);
        }
    }

    /// <summary>Called when credentials appear mid-run and a reboot should follow a good apply.</summary>
    public void SetRebootArmed(bool armed)
    {
        lock (_gate)
        {
            if (_rebootQueued)
            {
                _rebootArmed = true;
                return;
            }
            _rebootArmed = armed;
        }
    }

    public void MarkRebootQueued(string detail)
    {
        lock (_gate)
        {
            _rebootQueued = true;
            _rebootArmed = true;
            _rebootError = null;
            _phase = "reboot_queued";
            _solution = "";
            _failureCode = "";
            _layers["reboot"] = new Layer("ok", detail ?? "");
        }
    }

    public void MarkRebootFailed(string detail)
    {
        lock (_gate)
        {
            _rebootError = string.IsNullOrWhiteSpace(detail) ? "reboot_failed" : detail;
            _solution =
                "Windows did not accept the restart (" + _rebootError +
                "). Choose Retry. The block rules stay on.";
            _failureCode = _rebootError;
        }
    }

    public void NoteTickFailed(string detail)
    {
        lock (_gate)
        {
            if (_rebootQueued)
                return;
            _phase = "failed";
            _failureCode = "tick_failed";
            _solution = SolutionFor("enforce", "failed", detail);
        }
    }

    /// <summary>
    /// The Windows service is up and the pipe is open, but dlp.db did not open.
    /// The policy screen must say that, not "service is not running."
    /// </summary>
    public void NoteStoreFailed(string detail)
    {
        lock (_gate)
        {
            if (_rebootQueued)
                return;
            var tail = detail ?? "";
            if (tail.Length > 180)
                tail = tail[..180];
            _phase = "failed";
            _failureCode = "store_init";
            _solution = ServiceIdentity.ServiceName
                + " is running, but its policy database did not open ("
                + tail
                + "). Restart the service, then choose Retry.";
        }
    }

    public void FinishApply()
    {
        lock (_gate)
        {
            if (_rebootQueued)
            {
                _phase = "reboot_queued";
                _solution = "";
                _failureCode = "";
                return;
            }
            if (!_armed)
            {
                _phase = "waiting";
                _solution = "";
                _failureCode = "";
                return;
            }

            var recovery = FirstCode("recovery_required");
            if (recovery is not null)
            {
                _phase = "recovery_required";
                _failureCode = "recovery_required";
                _solution = SolutionFor(recovery.Value.Id, "recovery_required", recovery.Value.Detail);
                return;
            }

            var fail = FirstFailure();
            if (fail is not null)
            {
                _phase = "failed";
                _failureCode = fail.Value.Code;
                _solution = fail.Value.Solution;
                return;
            }

            _phase = "enforcing";
            _failureCode = "";
            if (string.IsNullOrEmpty(_rebootError))
                _solution = "";
        }
    }

    public string ToJson()
    {
        lock (_gate)
        {
            var layers = _layers
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new { id = kv.Key, code = kv.Value.Code, detail = kv.Value.Detail })
                .ToArray();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                phase = _phase,
                rebootArmed = _rebootArmed,
                rebootError = _rebootError,
                solution = _solution,
                failureCode = _failureCode,
                layers,
                lastAppBlock = _lastBlock is null
                    ? null
                    : new { id = _lastBlock.Id, name = _lastBlock.Name, path = _lastBlock.Path },
            });
        }
    }

    private (string Id, string Detail)? FirstCode(string code)
    {
        foreach (var kv in _layers)
        {
            if (string.Equals(kv.Value.Code, code, StringComparison.OrdinalIgnoreCase))
                return (kv.Key, kv.Value.Detail);
        }
        return null;
    }

    private (string Code, string Solution)? FirstFailure()
    {
        // AV is never gated here — ALPHA_AV_ENFORCE is independent of the policy screen.
        string[] order = ["network", "dns", "applocker", "wdac", "usb", "web", "filetransfer", "delete", "ports"];
        foreach (var id in order)
        {
            if (!_layers.TryGetValue(id, out var layer))
            {
                // wdac is optional on Enterprise interim; missing until first app tick is ok.
                if (id == "wdac")
                    continue;
                return ("missing_" + id, SolutionFor(id, "missing", "not_applied"));
            }
            if (layer.Code is "ok" or "off")
                continue;
            if (id == "ports" && layer.Code == "pending")
                continue;
            // failureCode names the layer (network / dns / applocker / …), not the raw detail.
            return (id, SolutionFor(id, layer.Code, layer.Detail));
        }
        return null;
    }

    internal static string SolutionFor(string layer, string code, string detail)
    {
        var d = detail ?? "";
        if (d.Contains("bootstrap_unresolved", StringComparison.OrdinalIgnoreCase))
            return "Network (DNS): the tracker server name did not resolve. Check the server address and DNS, then choose Retry. Deny rules stay on using the last saved server IPs.";
        if (d.Contains("hosts_allow", StringComparison.OrdinalIgnoreCase)
            && (layer == "dns" || layer == "network"))
            return "Hosts pin: the allowlist hosts file block could not be written (" + Truncate(d, 80) +
                   "). Choose Retry. If it stays, run the Windows setup repair and restart " +
                   ServiceIdentity.ServiceName + ".";
        if (d.Contains("no_reliable_app_control", StringComparison.OrdinalIgnoreCase))
            return "No reliable application control on this PC. AppLocker enforcement needs Windows 10 version 2004 or later with KB 5024351 (or a documented build baseline) or Windows 11 23H2+, and this PC has no signed WDAC policy. This is not an exec-time block.";
        if (d.Contains("applocker_gp_unsupported_on_pro", StringComparison.OrdinalIgnoreCase))
            return "Group Policy cannot deploy AppLocker on Windows Pro. Use MDM, a script, or local policy. This PC is not Enforcing.";
        if (d.Contains("channel_unknown", StringComparison.OrdinalIgnoreCase))
            return "AppLocker management channel could not be read (Group Policy, MDM, or local). This screen stays until that probe succeeds. This is not Enforcing.";
        if (d.Contains("applocker_interim", StringComparison.OrdinalIgnoreCase) || code == "interim")
            return "AppLocker is enforcing as an interim control. Production on Windows Pro still needs a signed WDAC policy and Secure Boot before this screen can close. This is not full exec-time deny.";
        if (d.Contains("wdac_secure_boot_off", StringComparison.OrdinalIgnoreCase)
            || (code == "recovery_required" && d.Contains("secure_boot", StringComparison.OrdinalIgnoreCase)))
            return "Signed WDAC tamper resistance is not active because Secure Boot is off or could not be read. Enable Secure Boot, then choose Retry. Deny rules stay on. This is not Enforcing.";
        if (code == "recovery_required" || d.Contains("recovery_required", StringComparison.OrdinalIgnoreCase))
            return "Production profile will not turn blocking off without a valid single-use recovery authorization. Deny rules stay on. This is not Enforcing.";
        if (d.Contains("applocker_unavailable", StringComparison.OrdinalIgnoreCase))
            return "Apps: AppLocker could not be configured on this build. Enforcement needs Windows 10 version 2004 or later with KB 5024351, or Windows 11. Group Policy deployment is unsupported on Pro.";
        if (d.Contains("set_policy_failed", StringComparison.OrdinalIgnoreCase)
            || d.Contains("still_notconfigured", StringComparison.OrdinalIgnoreCase))
        {
            return "Apps (AppLocker): Set-AppLockerPolicy did not leave Exe/Appx Enforcing (still NotConfigured). " +
                   "Choose Retry. If it stays, reboot once. Domain AppLocker GPO can also block local apply. " +
                   "Detail: " + Truncate(d, 100);
        }
        if (d.Contains("registry_write_failed", StringComparison.OrdinalIgnoreCase))
            return "Apps (AppLocker): could not write Exe EnforcementMode=1 in the registry. " +
                   ServiceIdentity.ServiceName + " must run as LocalSystem. Choose Retry.";
        if (d.Contains("NOT_ENFORCING", StringComparison.OrdinalIgnoreCase)
            || d.Contains("verify_failed", StringComparison.OrdinalIgnoreCase)
            || d.Contains("canary_allowed", StringComparison.OrdinalIgnoreCase)
            || d.Contains("canary_probe_failed", StringComparison.OrdinalIgnoreCase)
            || d.Contains("taskmgr_allowed", StringComparison.OrdinalIgnoreCase))
        {
            // Prefer policy-object failures (mode=/canary) over registry= — details often
            // contain both "registry=-1" and "mode=NotConfigured".
            string why;
            if (d.Contains("no_exe_collection", StringComparison.OrdinalIgnoreCase))
                why = "No Exe AppLocker policy is loaded.";
            else if (d.Contains("canary_allowed", StringComparison.OrdinalIgnoreCase))
                why = "Default-deny is not active (deny canary is still Allowed).";
            else if (d.Contains("canary_probe_failed", StringComparison.OrdinalIgnoreCase))
                why = "Deny canary probe failed (Test-AppLockerPolicy threw) — fail-closed until it returns Denied.";
            else if (d.Contains("taskmgr_allowed", StringComparison.OrdinalIgnoreCase))
                why = "Default-deny probe failed on an older build — Retry after updating the tracker.";
            else if (d.Contains("mode=", StringComparison.OrdinalIgnoreCase)
                     && !d.Contains("registry_mode=", StringComparison.OrdinalIgnoreCase))
                why = "The Exe policy is still NotConfigured / not in Enforcing mode after apply.";
            else if (d.Contains("verify_failed", StringComparison.OrdinalIgnoreCase))
                why = "AppLocker PowerShell probe failed (Get-AppLockerPolicy / Test-AppLockerPolicy). Retry; reboot once if it stays.";
            else if (d.Contains("registry_mode=", StringComparison.OrdinalIgnoreCase))
                why = "AppLocker Exe is Enabled but registry EnforcementMode heal is still pending.";
            else
                why = "AppLocker verify did not pass.";
            return "Apps (AppLocker): not enforcing. " + why +
                   " Choose Retry. If it stays, reboot this PC once. On Windows Pro, Group Policy deployment is unsupported; MDM, script, or local policy can enforce when KB 5024351 (or a later documented build) is present. A signed WDAC policy is required for production.";
        }
        if (d.Contains("active_store_query_failed", StringComparison.OrdinalIgnoreCase))
            return "Network: firewall ActiveStore proof failed (" + Truncate(d, 120) +
                   "). Start Windows Firewall (MpsSvc), ensure Domain/Private/Public are Enabled and Block, then Retry. Restart " +
                   ServiceIdentity.ServiceName + " if it stays.";
        if (d.Contains("active_store_profile_disabled", StringComparison.OrdinalIgnoreCase))
            return "Network: a firewall profile is disabled in ActiveStore. Profiles must be Enabled and Block. Choose Retry (the service turns them on).";
        if (d.Contains("quic_rule_missing", StringComparison.OrdinalIgnoreCase))
            return "Network: the Block QUIC rule (outbound UDP 443) is missing from ActiveStore. Choose Retry so the service recreates AlphaAI-DLP-LOCK-BLOCK-QUIC.";
        if (d.Contains("udp_443_allow_present", StringComparison.OrdinalIgnoreCase))
            return "Network: a product firewall allow re-opens UDP 443 (" + Truncate(d, 100) +
                   "). Remove leftover AlphaAI-DLP-LOCK DEST-UDP allows, then Retry.";
        if (d.Contains("active store disagreed", StringComparison.OrdinalIgnoreCase))
            return "Network: local policy says Block but ActiveStore still Allows outbound. Fix GPO/MDM firewall override, then Retry.";
        if (d.Contains("egress_probe", StringComparison.OrdinalIgnoreCase))
            return "Network: behavioral egress check failed (" + Truncate(d, 100) +
                   "). Public HTTPS should be blocked and the management server reachable. Choose Retry.";
        if (d.Contains("wdac_cip_required", StringComparison.OrdinalIgnoreCase))
            return "Apps (WDAC): Windows Pro requires a signed org CIP under %ProgramData%\\" +
                   ServiceIdentity.DataFolderName + "\\dlp\\wdac\\. Place base-policy.cip, enable Secure Boot, then Retry.";
        if (d.Contains("access_denied", StringComparison.OrdinalIgnoreCase))
            return ServiceIdentity.ServiceName + " is not running as LocalSystem. Start the service, then choose Retry.";
        if (d.Contains("disabled_local", StringComparison.OrdinalIgnoreCase) && layer == "network")
            return "Network: lockdown is turned off in service.env (ALPHA_DLP_NETWORK_LOCKDOWN). Set it to true and restart AlphaAITracker.Service.";
        if (d.Contains("disabled_local", StringComparison.OrdinalIgnoreCase) && layer == "applocker")
            return "Apps: blocking is turned off in service.env (ALPHA_DLP_APP_BLOCK). Set it to true and restart AlphaAITracker.Service.";
        if (d.Contains("paused", StringComparison.OrdinalIgnoreCase))
            return "Blocking is paused. It stays off until the pause ends. Choose Retry after the pause ends.";
        if (d.Contains("lockdown_off", StringComparison.OrdinalIgnoreCase))
            return "Network: lockdown is turned off in the published policy. An administrator must turn it on under Allowlists, then choose Retry.";
        if (d.Contains("lock_error", StringComparison.OrdinalIgnoreCase)
            || d.Contains("netsh", StringComparison.OrdinalIgnoreCase)
            || d.Contains("firewall", StringComparison.OrdinalIgnoreCase))
            return "Network: Windows Firewall did not accept the block rules (80/443 + DNS 53/853). Start the Windows Firewall service (MpsSvc), then choose Retry.";
        if (layer == "pipe" || d.Contains("dlp_disabled", StringComparison.OrdinalIgnoreCase))
            return "The security service is not running. Start " + ServiceIdentity.ServiceName + ", then choose Retry.";
        if (code == "missing")
            return "Security policy has not finished applying (" + layer + "). Choose Retry.";
        var label = layer switch
        {
            "network" => "Network",
            "dns" => "DNS / hosts pin",
            "applocker" => "Apps (AppLocker)",
            "usb" => "USB",
            "web" => "Web block",
            "filetransfer" => "File transfer",
            "delete" => "Delete block",
            "ports" => "Ports",
            _ => layer,
        };
        var tail = string.IsNullOrWhiteSpace(d) ? code : d;
        return label + " did not apply (" + Truncate(tail, 140) + "). Choose Retry. If it keeps failing, restart " +
               ServiceIdentity.ServiceName + ".";
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s ?? "";
        return s[..max];
    }
}
