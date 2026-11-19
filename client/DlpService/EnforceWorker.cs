using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Fast-path enforcer.
/// P1: USBSTOR Start=4 when any usb_* block rule is published.
/// P2: hosts DNS sink for web_navigate / shadowDeny domains (schedule-aware).
/// </summary>
public sealed class EnforceWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly RemovableDriveMonitor _drives;
    private readonly WebBlockState _webBlock;
    private readonly ILogger<EnforceWorker> _logger;
    private long _lastVersion = -1;
    private bool? _lastUsbBlock;
    private string _lastWebFingerprint = "\0";
    private DateTimeOffset _lastAccessDeniedLog = DateTimeOffset.MinValue;
    private bool _accessDeniedAlertQueued;
    private bool _webAccessDeniedAlertQueued;
    private string? _lastLockdownState;
    private bool _lockdownOverflowAudited;
    private DateTimeOffset _lastLockdownWarn = DateTimeOffset.MinValue;
    private string? _lastAppLockdownState;
    private DateTimeOffset _lastAppLockdownWarn = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAppSweepDue = DateTimeOffset.MinValue;
    private string _lastAppSweepAllowFp = "\0";
    private string? _lastDeleteBlockState;
    private string? _lastFileTransferState;
    private string? _lastStartupState;
    private DateTimeOffset _lastFileTransferTamperAlert = DateTimeOffset.MinValue;
    private static readonly TimeSpan FileTransferTamperAlertEvery = TimeSpan.FromMinutes(10);
    private string _lastPortFingerprint = "\0";
    private bool _portApplyStable;

    public EnforceWorker(
        DlpConfig cfg,
        PolicyStore store,
        RemovableDriveMonitor drives,
        WebBlockState webBlock,
        ILogger<EnforceWorker> logger)
    {
        _cfg = cfg;
        _store = store;
        _drives = drives;
        _webBlock = webBlock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.Enabled)
        {
            _logger.LogInformation("EnforceWorker parked (ALPHA_DLP_ENABLED=false)");
            if (!ProductionFailClosed.AllowFailOpen("dlp_disabled", out var parkDetail))
            {
                _logger.LogWarning(
                    "Production profile blocked park Clear ({Detail}) — keeping deny rules while disabled",
                    parkDetail);
                await _store.AuditAsync("park_clear_blocked", parkDetail, stoppingToken);
                return;
            }
            // Lab / recovery: clear leftovers left by an earlier enabled run.
            try
            {
                var parkClear = await Task.Run(LockdownNetworkEnforcer.Clear, stoppingToken);
                if (parkClear.Changed || parkClear.Detail.StartsWith("lock_clear_failed", StringComparison.Ordinal))
                    _logger.LogWarning("Parked: cleared lockdown filters ({Detail})", parkClear.Detail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lockdown clear on park failed");
            }
            try
            {
                var appClear = await Task.Run(AppAllowEnforcer.Clear, stoppingToken);
                if (appClear.Changed || !appClear.Applied)
                    _logger.LogWarning("Parked: cleared AppLocker ({Detail})", appClear.Detail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AppLocker clear on park failed");
            }
            try
            {
                var delClear = await Task.Run(DeleteBlockAclEnforcer.Clear, stoppingToken);
                if (delClear.Changed)
                    _logger.LogWarning("Parked: cleared delete-block ACLs ({Detail})", delClear.Detail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Delete-block ACL clear on park failed");
            }
            try
            {
                var ftClear = await Task.Run(FileTransferEnforcer.Clear, stoppingToken);
                if (ftClear.Changed)
                    _logger.LogWarning("Parked: cleared file-transfer browser policies ({Detail})", ftClear.Detail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "File-transfer clear on park failed");
            }
            try
            {
                var suClear = await Task.Run(StartupAppEnforcer.Clear, stoppingToken);
                if (suClear.Changed)
                    _logger.LogWarning("Parked: restored startup apps ({Detail})", suClear.Detail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Startup-app restore on park failed");
            }
            try
            {
                var portClear = await Task.Run(PortDeviceEnforcer.Clear, stoppingToken);
                if (portClear.Changed)
                    _logger.LogWarning("Parked: cleared port control ({Detail})", portClear.Detail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Port control clear on park failed");
            }
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        _logger.LogInformation(
            "EnforceWorker running (usbBlock={Usb}, webBlock={Web}, networkLockdown={Net}, appBlock={App}, deleteBlock={Del}, portBlock={Port}, fileTransferBlock={Ft}, startupBlock={Su})",
            _cfg.UsbBlock, _cfg.WebBlock, _cfg.NetworkLockdown, _cfg.AppBlock, _cfg.DeleteBlock, _cfg.PortBlock,
            _cfg.FileTransferBlock, _cfg.StartupBlock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Pick up service.env changes written by PolicyPullWorker after a new policy.
                _cfg.RefreshLocalSwitches();
                await ApplyAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EnforceWorker tick failed");
                EnforceStatus.Shared.NoteTickFailed(ex.Message);
            }

            try
            {
                // Idle enforce cadence (independent of policy pull). Policy publish wakes immediately.
                var tickSec = Math.Clamp(_cfg.EnforceTickSec, 5, 120);
                await DlpSignals.WaitEnforceAsync(TimeSpan.FromSeconds(tickSec), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Immediate full enforce pass (USB / network lockdown / AppLocker / web).
    /// Used after tracker login before the one-shot reboot.
    /// </summary>
    public Task ForceApplyOnceAsync(CancellationToken ct) => ApplyAsync(ct);

    private static void Report(string id, string code, string detail) =>
        EnforceStatus.Shared.SetLayer(id, code, detail ?? "");

    private async Task ApplyAsync(CancellationToken ct)
    {
        var (version, hash, bundleJson) = await _store.GetAppliedAsync(ct);
        _cfg.RefreshCredentials();
        // Fail-closed: local blocker switches arm the policy screen and Apply even
        // before DeviceAuth. Missing token must never Clear lockdown / AppLocker.
        var armed = _cfg.NetworkLockdown || _cfg.AppBlock ||
                    (!string.IsNullOrWhiteSpace(_cfg.ServerUrl) &&
                     !string.IsNullOrWhiteSpace(_cfg.DeviceToken));
        EnforceStatus.Shared.BeginApply(armed);
        if (armed)
            EnforceStatus.Shared.MarkVerifying();
        try
        {
            await ApplyCoreAsync(version, hash, bundleJson, ct);
        }
        finally
        {
            EnforceStatus.Shared.FinishApply();
            if (version > 0 && EnforceStatus.Shared.IsEnforcing)
            {
                _ = PolicyAckClient.TryAckAsync(_cfg, version, hash, "verified", "enforcing", ct);
            }
            else if (version > 0 && armed)
            {
                // Gate is stuck — tell the server which layer failed + the fix hint so the
                // dashboard shows why, instead of leaving verified_at blank with no reason.
                var (failCode, solution) = EnforceStatus.Shared.FailureSnapshot();
                var detail = string.IsNullOrWhiteSpace(failCode)
                    ? "verifying"
                    : (string.IsNullOrWhiteSpace(solution) ? failCode : failCode + ": " + solution);
                if (detail.Length > 480)
                    detail = detail[..480];
                _ = PolicyAckClient.TryAckAsync(_cfg, version, hash, "failed", detail, ct);
            }
        }
    }

    private async Task ApplyCoreAsync(long version, string hash, string bundleJson, CancellationToken ct)
    {
        var bundle = PolicyBundleParser.Parse(bundleJson);
        var now = DateTimeOffset.UtcNow;
        var wantUsbBlock = _cfg.UsbBlock && PolicyBundleParser.WantsUsbStorageBlock(bundle);
        var wantDomains = _cfg.WebBlock
            ? PolicyBundleParser.ActiveWebBlockDomains(bundle, now)
            : Array.Empty<string>();
        var webFp = PolicyBundleParser.WebBlockFingerprint(wantDomains);

        if (version != _lastVersion)
        {
            _lastVersion = version;
            // New published policy must always re-sync hosts (clear OR re-block).
            // Without this, empty→same-domain fingerprints could skip Apply after a clear.
            _lastWebFingerprint = "\0";
            _accessDeniedAlertQueued = false;
            _webAccessDeniedAlertQueued = false;
            await _store.AuditAsync("enforce_reload", $"version={version} hash={hash}", ct);
            _logger.LogInformation("EnforceWorker loaded policy version {Version}", version);
            if (wantUsbBlock)
                _logger.LogInformation("Policy requests USB mass-storage BLOCK");
            if (wantDomains.Count > 0)
                _logger.LogInformation("Policy requests WEB domain BLOCK ({Count} hosts)", wantDomains.Count);
            else if (version == 0)
                _logger.LogInformation(
                    "No allow-list publish yet (version 0). After login, web is default-deny (bootstrap only) until Allowlists → Publish.");
        }

        _drives.SetUsbBlocked(wantUsbBlock);

        await ApplyUsbAsync(bundle, version, wantUsbBlock, ct);
        // Lockdown (allow-world) first; explicit web-block domains below still apply inside it.
        await ApplyLockdownAsync(bundle, ct);
        // Phase 2 optional WFP app-aware (parked unless ALPHA_DLP_WFP_APP_AWARE=true + driver).
        var wfp = WfpAppAwareEnforcer.Apply();
        if (wfp.Detail.Contains("wfp_driver", StringComparison.OrdinalIgnoreCase)
            || WfpAppAwareEnforcer.Enabled)
        {
            await _store.AuditAsync("wfp_status", wfp.Detail, ct);
        }
        await ApplyAppLockdownAsync(bundle, ct);
        await ApplyDeleteBlockAsync(bundle, ct);
        await ApplyFileTransferAsync(bundle, version, ct);
        await ApplyStartupAppsAsync(bundle, ct);
        await ApplyWebAsync(bundle, version, wantDomains, webFp, ct);
        await ApplyPortControlAsync(bundle, version, ct);
    }

    /// <summary>
    /// Hardware/port control: disable blocked device classes (Device Installation
    /// Restrictions) + instances (Disable-PnpDevice). Independent of network lockdown;
    /// gated by the local ALPHA_DLP_PORT_BLOCK switch. Clears when off / empty.
    /// Fingerprint advances only when the live OS state matches the desired set
    /// (missing devices stay Pending and are retried every tick).
    /// </summary>
    private async Task ApplyPortControlAsync(PolicyBundle bundle, long version, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var instances = _cfg.PortBlock ? PolicyBundleParser.BlockedInstances(bundle) : Array.Empty<string>();
        var classes = _cfg.PortBlock ? PolicyBundleParser.BlockedClasses(bundle) : Array.Empty<string>();
        var allows = _cfg.PortBlock ? PolicyBundleParser.AllowedInstances(bundle) : Array.Empty<string>();
        var fp = (_cfg.PortBlock ? "on" : "off")
                 + "|i=" + string.Join(",", instances)
                 + "|c=" + string.Join(",", classes)
                 + "|a=" + string.Join(",", allows);

        if (!_cfg.PortBlock || (instances.Count == 0 && classes.Count == 0))
        {
            // Only clear once per empty desired set (fingerprint short-circuit for clear path).
            if (fp == _lastPortFingerprint)
            {
                Report("ports", "off", _cfg.PortBlock ? "no_port_rules" : "disabled_local");
                return;
            }
            var clear = await Task.Run(PortDeviceEnforcer.Clear, ct);
            if (clear.Changed || clear.Detail.StartsWith("error", StringComparison.Ordinal))
            {
                await _store.AuditAsync("port_control_clear",
                    $"switch={(_cfg.PortBlock ? "on" : "off")} detail={clear.Detail}", ct);
                _logger.LogInformation("Port control CLEARED (detail={Detail})", clear.Detail);
            }
            if (clear.Applied)
            {
                _lastPortFingerprint = fp;
                _portApplyStable = true;
                Report("ports", "off", clear.Detail ?? "");
            }
            else
            {
                Report("ports", "failed", clear.Detail ?? "port_clear_failed");
            }
            return;
        }

        // Skip full Apply when policy fingerprint is unchanged, last Apply was stable, and
        // PortGuard has not reported a reblock. Guard still catches re-enables between ticks.
        var needsReconcile = PortDeviceEnforcer.ConsumeNeedsFullReconcile();
        if (fp == _lastPortFingerprint && _portApplyStable && !needsReconcile)
        {
            Report("ports", "ok", "stable");
            return;
        }

        var result = await Task.Run(() => PortDeviceEnforcer.Apply(instances, classes, allows), ct);
        await _store.AuditAsync("port_control_apply",
            $"instances={instances.Count} classes={classes.Count} allows={allows.Count} " +
            $"applied={result.Applied} pending={result.Pending} changed={result.Changed} detail={result.Detail}", ct);

        if (result.Detail == "access_denied_need_admin")
        {
            _logger.LogWarning("Port control needs Administrator / LocalSystem — not applied");
            _lastPortFingerprint = "\0"; // force retry awareness
            _portApplyStable = false;
            Report("ports", "failed", result.Detail);
            return;
        }
        if (!result.Applied)
        {
            _lastPortFingerprint = "\0";
            _portApplyStable = false;
            if (result.Pending)
            {
                _logger.LogDebug(
                    "Port control PENDING (waiting for device plug-in): {Detail}", result.Detail);
                Report("ports", "pending", result.Detail ?? "");
            }
            else
            {
                _logger.LogWarning("Port control not applied: {Detail}", result.Detail);
                Report("ports", "failed", result.Detail ?? "");
            }
            return;
        }

        if (result.Changed || fp != _lastPortFingerprint)
        {
            _logger.LogInformation(
                "Port control ENFORCED (instances={Inst}, classes={Cls}, changed={Changed}, detail={Detail})",
                instances.Count, classes.Count, result.Changed, result.Detail);
        }

        if (result.Changed && fp != _lastPortFingerprint)
        {
            await _store.EnqueueAlertAsync(
                Guid.NewGuid().ToString(), null, version, "device_control", "high", "blocked",
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["instances"] = string.Join(",", instances),
                    ["classes"] = string.Join(",", classes),
                    ["detail"] = result.Detail,
                }),
                ct);
        }

        _lastPortFingerprint = fp;
        // Stable only when nothing pending — Guard covers re-enable fights until next wake.
        _portApplyStable = !result.Pending;
        Report("ports", "ok", result.Detail ?? "");
    }

    /// <summary>
    /// File Transfer: Chromium HKLM DownloadRestrictions / AllowFileSelectionDialogs from
    /// published DLP rules (triggers <c>file_download</c> / <c>file_upload</c>, action block /
    /// alert_block). Independent of network lockdown; honours the
    /// machine pause and the local ALPHA_DLP_FILE_TRANSFER_BLOCK switch. Runs every tick:
    /// a write while the desired state is unchanged means the policy was removed/changed
    /// on the PC → restored + rate-limited <c>file_transfer_tamper</c> alert.
    /// </summary>
    private async Task ApplyFileTransferAsync(PolicyBundle bundle, long version, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        _cfg.RefreshCredentials();
        var pausedUntil = await _store.GetLockdownPausedUntilAsync(ct);
        var paused = pausedUntil is { } p && p > DateTimeOffset.UtcNow;
        // Fail-closed: apply whenever the local switch is on and not paused — no DeviceAuth gate.
        var active = _cfg.FileTransferBlock && !paused;
        var downloadRule = PolicyBundleParser.BlockingRule(bundle, "file_download");
        var uploadRule = PolicyBundleParser.BlockingRule(bundle, "file_upload");
        var wantDownload = active && downloadRule is not null;
        var wantUpload = active && uploadRule is not null;
        var state = !_cfg.FileTransferBlock ? "disabled_local"
            : paused ? "paused"
            : $"dl={(wantDownload ? 1 : 0)},ul={(wantUpload ? 1 : 0)}";

        var result = await Task.Run(() => FileTransferEnforcer.Apply(wantDownload, wantUpload), ct);
        if (!result.Applied)
        {
            await _store.AuditAsync("file_transfer_apply_failed", result.Detail, ct);
            _logger.LogWarning("File-transfer policy apply failed: {Detail}", result.Detail);
            Report("filetransfer", "failed", result.Detail ?? "");
            return;
        }

        var sameAsLast = _lastFileTransferState == state;
        if (result.Changed)
        {
            var written = string.Join(", ", result.Written.Take(20));
            await _store.AuditAsync(
                (wantDownload || wantUpload) ? "file_transfer_apply" : "file_transfer_clear",
                $"state={state} {result.Detail} values=[{written}]", ct);
            _logger.LogInformation(
                "File-transfer browser policies {Action} (state={State}, {Detail})",
                (wantDownload || wantUpload) ? "ENFORCED" : "CLEARED", state, result.Detail);

            // Desired state unchanged but we had to write = someone removed/changed the policy.
            var now = DateTimeOffset.UtcNow;
            if (sameAsLast && (wantDownload || wantUpload) &&
                now - _lastFileTransferTamperAlert >= FileTransferTamperAlertEvery)
            {
                _lastFileTransferTamperAlert = now;
                var evidence = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["detail"] = "Browser download/upload block was removed or changed on this PC and restored automatically",
                    ["changes"] = written,
                });
                var rule = downloadRule ?? uploadRule;
                await _store.EnqueueAlertAsync(Guid.NewGuid().ToString(), rule?.Id, version,
                    "file_transfer_tamper", rule?.Severity ?? "high", "blocked", now, evidence, ct);
            }
        }
        _lastFileTransferState = state;
        Report("filetransfer", "ok", state);
    }

    /// <summary>
    /// Startup apps: every Run-key / Startup-folder entry is OFF unless it is this product,
    /// a Windows component, or published on <c>allow.apps</c> (/allowlists/apps). Independent
    /// of network lockdown; honours machine pause and ALPHA_DLP_STARTUP_BLOCK. Runs every
    /// tick so a re-enabled entry is switched off again (audited as tamper).
    /// </summary>
    private async Task ApplyStartupAppsAsync(PolicyBundle bundle, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var pausedUntil = await _store.GetLockdownPausedUntilAsync(ct);
        var paused = pausedUntil is { } p && p > DateTimeOffset.UtcNow;
        var active = _cfg.StartupBlock && !paused;
        var state = !_cfg.StartupBlock ? "disabled_local" : paused ? "paused" : "on";

        var result = active
            ? await Task.Run(() => StartupAppEnforcer.Apply(bundle.AllowApps), ct)
            : await Task.Run(StartupAppEnforcer.Clear, ct);

        if (result.Changed || (!result.Applied && _lastStartupState != "failed"))
        {
            var written = string.Join(", ", result.Written.Take(20));
            await _store.AuditAsync(
                !result.Applied ? "startup_apps_apply_failed"
                : active ? "startup_apps_apply" : "startup_apps_clear",
                $"state={state} {result.Detail} values=[{written}]", ct);
            _logger.LogInformation(
                "Startup apps {Action} (state={State}, {Detail})",
                active ? "ENFORCED" : "RESTORED", state, result.Detail);
            if (result.Tampered > 0)
                await _store.AuditAsync("startup_apps_tamper",
                    $"re-enabled={result.Tampered} values=[{written}]", ct);
        }
        _lastStartupState = result.Applied ? state : "failed";
        // Not a gate layer: a startup entry we cannot write never holds the policy screen.
        Report("startup", result.Applied ? (active ? "ok" : "off") : "failed", result.Detail ?? "");
    }

    /// <summary>
    /// NTFS Deny Delete on Fixed/Removable roots when lockdown is ON (create/edit stay allowed).
    /// Does not wait for DeviceAuth — fail-closed with network/apps.
    /// </summary>
    private async Task ApplyDeleteBlockAsync(PolicyBundle bundle, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        _cfg.RefreshCredentials();
        var pausedUntil = await _store.GetLockdownPausedUntilAsync(ct);
        var paused = pausedUntil is { } p && p > DateTimeOffset.UtcNow;
        var want = _cfg.DeleteBlock && !paused && bundle.LockdownEnabled;

        if (!want)
        {
            var clear = await Task.Run(DeleteBlockAclEnforcer.Clear, ct);
            var state = !_cfg.DeleteBlock ? "disabled_local" : paused ? "paused" : "lockdown_off";
            if (clear.Changed || _lastDeleteBlockState is not (null or "off" or "paused" or "disabled_local" or "lockdown_off"))
            {
                await _store.AuditAsync("delete_block_clear", $"state={state} detail={clear.Detail}", ct);
                _logger.LogInformation("Delete-block ACLs CLEARED (state={State}, detail={Detail})", state, clear.Detail);
            }
            if (clear.Applied)
                _lastDeleteBlockState = state;
            Report("delete", state is "disabled_local" or "lockdown_off" ? "off" : "failed", state);
            return;
        }

        var result = await Task.Run(
            () => DeleteBlockAclEnforcer.Apply(TimeSpan.FromSeconds(_cfg.DeleteRecheckSec)), ct);
        if (result.Detail.StartsWith("skipped", StringComparison.Ordinal))
        {
            Report("delete", "ok", result.Detail);
            return;
        }
        if (result.Applied)
        {
            if (result.Changed || _lastDeleteBlockState != "on")
            {
                await _store.AuditAsync("delete_block_apply", result.Detail, ct);
                _logger.LogInformation(
                    "Delete-block ACLs ENFORCED ({Detail}) — Authenticated Users cannot delete on Fixed/Removable volumes",
                    result.Detail);
            }
            _lastDeleteBlockState = "on";
            Report("delete", "ok", result.Detail);
        }
        else
        {
            await _store.AuditAsync("delete_block_apply_failed", result.Detail, ct);
            _logger.LogWarning("Delete-block ACL apply failed: {Detail}", result.Detail);
            Report("delete", "failed", result.Detail);
        }
    }

    /// <summary>
    /// AppLocker EXE default-deny when lockdown is ON (same switch as network lockdown).
    /// Pause / lockdown:false / local switch off clears. Missing DeviceAuth does
    /// <b>not</b> clear — bootstrap A + published allow.apps stay fail-closed.
    /// </summary>
    private async Task ApplyAppLockdownAsync(PolicyBundle bundle, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        _cfg.RefreshCredentials();
        var pausedUntil = await _store.GetLockdownPausedUntilAsync(ct);
        var paused = pausedUntil is { } p && p > DateTimeOffset.UtcNow;
        var want = _cfg.AppBlock && !paused && bundle.LockdownEnabled;

        string? blockedOpen = null;
        if (!want)
        {
            var openReason = !_cfg.AppBlock ? "disabled_local"
                : paused ? "paused"
                : "lockdown_off";
            if (!ProductionFailClosed.AllowFailOpen(openReason, out var openDetail))
            {
                await _store.AuditAsync("app_lockdown_clear_blocked", openDetail, ct);
                _logger.LogWarning(
                    "Production profile blocked AppLocker Clear ({Detail}) — keeping enforce",
                    openDetail);
                blockedOpen = openReason;
                // Fall through and re-apply bootstrap A + published apps.
            }
            else
            {
                if (openDetail.StartsWith("recovery:", StringComparison.Ordinal))
                    await _store.AuditAsync("recovery_bypass", openDetail, ct);
                var clear = await Task.Run(AppAllowEnforcer.Clear, ct);
                if (clear.Changed || _lastAppLockdownState is not (null or "off" or "paused" or "disabled_local" or "lockdown_off"))
                {
                    await _store.AuditAsync("app_lockdown_clear", $"state={openReason} detail={clear.Detail}", ct);
                    _logger.LogInformation("AppLocker CLEARED (state={State}, detail={Detail})", openReason, clear.Detail);
                }
                if (clear.Applied)
                    _lastAppLockdownState = openReason;
                EnforceStatus.Shared.SetWatch(false, Array.Empty<AllowAppEntry>());
                Report("applocker", openReason is "disabled_local" or "lockdown_off" ? "off" : "failed", openReason);
                return;
            }
        }

        var apps = bundle.AllowApps;
        var (version, hash, _) = await _store.GetAppliedAsync(ct);
        ManagedLauncherCatalog.Publish(apps, version, hash);

        // Signed WDAC base (when CIP present) + AppLocker interim. WDAC never clears
        // AppLocker — AppLocker stays the per-user / unsigned-PC layer.
        var wdac = await Task.Run(() => WdacEnforcer.Apply(apps), ct);
        if (wdac.Changed || !string.IsNullOrEmpty(wdac.Detail))
        {
            await _store.AuditAsync(
                wdac.Applied ? "wdac_apply" : "wdac_status",
                wdac.Detail,
                ct);
            if (wdac.Applied)
                _logger.LogInformation("WDAC ENFORCED ({Detail})", wdac.Detail);
            else if (DateTimeOffset.UtcNow - _lastAppLockdownWarn > TimeSpan.FromMinutes(10))
                _logger.LogInformation("WDAC status: {Detail}", wdac.Detail);
        }

        var result = await Task.Run(() => AppAllowEnforcer.Apply(apps), ct);
        var canaryThrew = result.Detail.Contains("canary_probe_failed", StringComparison.OrdinalIgnoreCase);
        var canaryDenied = result.Applied
                           && !canaryThrew
                           && result.Detail.Contains("verify=enforcing", StringComparison.OrdinalIgnoreCase);
        var facts = AppControlCapability.Probe();
        var secureBoot = WdacEnforcer.TryReadSecureBoot();
        var decision = AppControlCapability.Decide(
            ProductionFailClosed.Enabled,
            WdacEnforcer.IsWindowsProSku(),
            WdacEnforcer.HasOrgCip,
            secureBoot,
            WdacEnforcer.OrgCipFingerprintMatches(WdacEnforcer.OrgCipFingerprint()),
            canaryDenied,
            canaryThrew,
            AppControlCapability.Assess(facts),
            facts.Channel,
            WdacEnforcer.BlockRulesDetail.Contains("verified", StringComparison.Ordinal),
            ProductionFailClosed.AcceptAppLockerInterim);
        var appDetail = decision.AppLockerDetail + ";" + result.Detail + ";" + wdac.Detail;
        var wdacDetail = decision.WdacDetail + ";" + wdac.Detail;
        if (result.Detail != "stale_generation")
        {
            if (blockedOpen is not null)
                Report("applocker", "recovery_required", "recovery_required;" + blockedOpen + ";" + appDetail);
            else
                Report("applocker", decision.AppLockerCode, appDetail);

            if (blockedOpen is not null && decision.WdacCode is not "recovery_required")
                Report("wdac", "recovery_required", "recovery_required;" + blockedOpen + ";" + wdacDetail);
            else
                Report("wdac", decision.WdacCode, wdacDetail);

            // Watch clears only when the canary or the org CIP (with Secure Boot) proved
            // a native block. A SAC/p7b heuristic never reaches Decide.
            EnforceStatus.Shared.SetWatch(decision.ArmWatch, apps);
            if (decision.OverallEnforcing && blockedOpen is null)
            {
                if (result.Changed || wdac.Changed || _lastAppLockdownState != "on")
                {
                    await _store.AuditAsync(
                        "app_lockdown_apply",
                        $"apps={apps.Count} detail={appDetail}",
                        ct);
                    _logger.LogInformation(
                        "App control ENFORCED (allowApps={Count}, detail={Detail}) — denied launches show native Windows UI",
                        apps.Count, appDetail);
                }
                _lastAppLockdownState = "on";
            }
            else
            {
                if (DateTimeOffset.UtcNow - _lastAppLockdownWarn > TimeSpan.FromMinutes(2))
                {
                    _lastAppLockdownWarn = DateTimeOffset.UtcNow;
                    _logger.LogWarning("App control not enforcing: {Detail}", appDetail + ";" + wdacDetail);
                    await _store.AuditAsync("app_lockdown_apply_failed", appDetail + ";" + wdacDetail, ct);
                }
                _lastAppLockdownState = blockedOpen is not null || decision.WdacCode == "recovery_required"
                    ? "recovery_required"
                    : "failed";
                _nextAppSweepDue = DateTimeOffset.MinValue;
            }
        }

        // AppLocker does not kill already-running apps. Adaptive sweep: within 5s after a kill
        // or AppLocker change; otherwise every ALPHA_DLP_APP_SWEEP_SEC while lockdown is ON.
        var allowFp = AppProcessGuard.AllowAppsFingerprint(apps);
        var now = DateTimeOffset.UtcNow;
        var policyChanged = allowFp != _lastAppSweepAllowFp || result.Changed;
        if (policyChanged)
            _nextAppSweepDue = DateTimeOffset.MinValue;
        if (now < _nextAppSweepDue && !policyChanged)
            return;

        var sweep = await Task.Run(() => AppProcessGuard.Sweep(apps), ct);
        _lastAppSweepAllowFp = allowFp;
        if (sweep.Killed > 0)
        {
            await _store.AuditAsync("app_process_kill", sweep.Detail, ct);
            _logger.LogWarning("App allowlist killed {Count} process(es): {Detail}", sweep.Killed, sweep.Detail);
            _nextAppSweepDue = now.AddSeconds(5);
        }
        else
        {
            _nextAppSweepDue = now.AddSeconds(Math.Max(5, _cfg.AppSweepSec));
        }
    }

    /// <summary>
    /// Default-deny web (80/443 + DNS 53/853). ON whenever the local switch and
    /// published lockdown are on — DeviceAuth is <b>not</b> required. Missing token
    /// must never Clear the firewall (that left every site open). Publish only expands
    /// <c>allow.hosts</c>. Bootstrap carve-out uses <c>ALPHA_SERVER_URL</c> when present.
    /// </summary>
    private async Task ApplyLockdownAsync(PolicyBundle bundle, CancellationToken ct)
    {
        _cfg.RefreshCredentials();
        var pausedUntil = await _store.GetLockdownPausedUntilAsync(ct);
        var paused = pausedUntil is { } p && p > DateTimeOffset.UtcNow;
        var want = _cfg.NetworkLockdown && !paused && bundle.LockdownEnabled;

        string? blockedOpen = null;
        if (!want)
        {
            var openReason = !_cfg.NetworkLockdown ? "disabled_local"
                : paused ? "paused"
                : "lockdown_off";
            if (!ProductionFailClosed.AllowFailOpen(openReason, out var openDetail))
            {
                await _store.AuditAsync("lockdown_clear_blocked", openDetail, ct);
                _logger.LogWarning(
                    "Production profile blocked network Clear ({Detail}) — keeping deny rules",
                    openDetail);
                blockedOpen = openReason;
                // Fall through: re-apply deny with empty allow expansion rather than Clear.
            }
            else
            {
                if (openDetail.StartsWith("recovery:", StringComparison.Ordinal))
                    await _store.AuditAsync("recovery_bypass", openDetail, ct);
                var clear = await Task.Run(LockdownNetworkEnforcer.Clear, ct);
                if (clear.Changed || _lastLockdownState is not (null or "off" or "paused" or "disabled_local" or "lockdown_off"))
                {
                    await _store.AuditAsync("lockdown_clear", $"state={openReason} detail={clear.Detail}", ct);
                    _logger.LogInformation("Lockdown filters CLEARED (state={State}, detail={Detail})", openReason, clear.Detail);
                }
                if (!clear.Detail.StartsWith("lock_clear_failed", StringComparison.Ordinal))
                    _lastLockdownState = openReason;
                _lockdownOverflowAudited = false;
                var code = openReason is "disabled_local" or "lockdown_off" ? "off" : "failed";
                Report("network", code, openReason);
                Report("dns", code, openReason);
                return;
            }
        }

        var hosts = bundle.PlatformAllowHosts.Concat(bundle.AllowHosts).ToList();
        var serverUrl = _cfg.ServerUrl;
        var result = await Task.Run(() => LockdownNetworkEnforcer.Apply(hosts, serverUrl), ct);

        if (result.Overflow && !_lockdownOverflowAudited)
        {
            _lockdownOverflowAudited = true;
            await _store.AuditAsync(
                "lockdown_ip_overflow",
                $"cap={LockdownNetworkEnforcer.MaxAllowIps} hosts={hosts.Count}",
                ct);
            _logger.LogWarning(
                "Lockdown allow set exceeded {Cap} IPs — extra IPs dropped (bootstrap kept first)",
                LockdownNetworkEnforcer.MaxAllowIps);
        }
        else if (!result.Overflow)
        {
            _lockdownOverflowAudited = false;
        }

        if (result.Detail == "stale_generation")
            return;

        // Ready only when ActiveStore, QUIC, and the management TCP probe all passed.
        // Rules sitting on disk are not enough: a failed egress probe stays Failed.
        // bootstrap_unresolved is still Applied when the last saved management IP connected.
        var networkReady = result.Applied;
        var hd = result.HostsDetail ?? "";
        var hostsReady = networkReady &&
                         (string.IsNullOrEmpty(hd) ||
                          hd.StartsWith("hosts_allow", StringComparison.OrdinalIgnoreCase));

        if (networkReady)
        {
            if (blockedOpen is not null)
            {
                Report("network", "recovery_required", "recovery_required;" + blockedOpen + ";" + result.Detail);
                Report("dns", "recovery_required", "recovery_required;" + blockedOpen);
            }
            else
            {
                Report("network", "ok", result.Detail);
                Report("dns", hostsReady ? "ok" : "failed",
                    string.IsNullOrEmpty(result.HostsDetail) ? result.Detail : result.HostsDetail);
            }
            if (result.Changed || _lastLockdownState != "on")
            {
                await _store.AuditAsync(
                    "lockdown_apply",
                    $"hosts={hosts.Count} ips={result.AllowIps} detail={result.Detail}",
                    ct);
                _logger.LogInformation(
                    "Lockdown ENFORCED (hosts={Hosts}, allowIps={Ips}, detail={Detail})",
                    hosts.Count, result.AllowIps, result.Detail);
            }
            _lastLockdownState = "on";
            return;
        }

        Report("network", "failed", result.Detail);
        Report("dns", "failed", string.IsNullOrEmpty(result.HostsDetail) ? result.Detail : result.HostsDetail);

        // Not applied (bootstrap unresolved / access denied / netsh error): rules from a
        // cached server IP may still be up. Retry next tick; log at most every 2 min.
        if (result.Detail != "stale_generation" &&
            DateTimeOffset.UtcNow - _lastLockdownWarn > TimeSpan.FromMinutes(2))
        {
            _lastLockdownWarn = DateTimeOffset.UtcNow;
            _logger.LogWarning("Lockdown not applied: {Detail}", result.Detail);
            await _store.AuditAsync("lockdown_apply_failed", result.Detail, ct);
        }
    }

    private async Task ApplyUsbAsync(
        PolicyBundle bundle, long version, bool wantUsbBlock, CancellationToken ct)
    {
        // Do not touch the registry just to "confirm allow" on cold start with no block rules.
        // Exception: ALPHA_DLP_USB_BLOCK=false must lift a block an earlier run left behind,
        // so the first tick with the local switch off always writes "allow" once.
        if (!wantUsbBlock && _lastUsbBlock != true && (_cfg.UsbBlock || _lastUsbBlock == false))
        {
            _lastUsbBlock = false;
            Report("usb", "off", _cfg.UsbBlock ? "no_usb_rule" : "disabled_local");
            return;
        }

        if (_lastUsbBlock == wantUsbBlock)
        {
            Report("usb", wantUsbBlock ? "ok" : "off", wantUsbBlock ? "usbstor_block" : "usbstor_allow");
            return;
        }

        var result = UsbStorEnforcer.Apply(wantUsbBlock);
        await _store.AuditAsync(
            "usbstor_apply",
            $"block={wantUsbBlock} changed={result.Changed} detail={result.Detail} prev={result.PreviousStart}",
            ct);

        if (result.Detail == "access_denied_need_admin")
        {
            if (DateTimeOffset.UtcNow - _lastAccessDeniedLog > TimeSpan.FromMinutes(2))
            {
                _lastAccessDeniedLog = DateTimeOffset.UtcNow;
                _logger.LogWarning(
                    "USBSTOR enforce needs Administrator / LocalSystem. " +
                    $"Right-click {ServiceIdentity.ServiceName}.exe → Run as administrator, or install as a Windows Service.");
            }

            Report("usb", "failed", result.Detail ?? "access_denied_need_admin");
            if (!_accessDeniedAlertQueued && wantUsbBlock)
            {
                _accessDeniedAlertQueued = true;
                var rule = PolicyBundleParser.FirstUsbBlockRule(bundle);
                await _store.EnqueueAlertAsync(
                    Guid.NewGuid().ToString(),
                    string.IsNullOrEmpty(rule?.Id) ? null : rule.Id,
                    version,
                    "usb_storage",
                    rule?.Severity ?? "critical",
                    "blocked_failed",
                    DateTimeOffset.UtcNow,
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["reason"] = result.Detail ?? "",
                        ["desiredBlock"] = wantUsbBlock ? "true" : "false",
                    }),
                    ct);
            }
            return;
        }

        _lastUsbBlock = wantUsbBlock;
        _accessDeniedAlertQueued = false;
        Report("usb", "ok", result.Detail ?? "");
        _logger.LogInformation(
            "USBSTOR {State} (changed={Changed}, detail={Detail})",
            wantUsbBlock ? "BLOCK" : "ALLOW", result.Changed, result.Detail);

        if (result.Changed && wantUsbBlock)
        {
            var rule = PolicyBundleParser.FirstUsbBlockRule(bundle);
            await _store.EnqueueAlertAsync(
                Guid.NewGuid().ToString(),
                string.IsNullOrEmpty(rule?.Id) ? null : rule.Id,
                version,
                "usb_storage",
                rule?.Severity ?? "critical",
                "blocked",
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["action"] = "usbstor_disabled",
                    ["previousStart"] = result.PreviousStart?.ToString() ?? "",
                    ["desiredStart"] = result.DesiredStart.ToString(),
                }),
                ct);
        }
    }

    private async Task ApplyWebAsync(
        PolicyBundle bundle,
        long version,
        IReadOnlyList<string> wantDomains,
        string webFp,
        CancellationToken ct)
    {
        _cfg.RefreshCredentials();
        var pausedUntil = await _store.GetLockdownPausedUntilAsync(ct);
        var paused = pausedUntil is { } p && p > DateTimeOffset.UtcNow;
        // DeviceAuth is not required — fail-closed with network lockdown.
        var lockdownOn = _cfg.NetworkLockdown && !paused && bundle.LockdownEnabled;
        if (!lockdownOn && !ProductionFailClosed.AllowFailOpen(
                !_cfg.NetworkLockdown ? "disabled_local" : paused ? "paused" : "lockdown_off",
                out _))
        {
            lockdownOn = true;
        }
        var lockdownAllow = lockdownOn
            ? BuildLockdownBrowserAllowHosts(bundle, wantDomains, _cfg.ServerUrl)
            : null;
        var allowFp = lockdownAllow is null
            ? "allow=-"
            : "allow=" + string.Join(",", lockdownAllow);
        var combinedFp = webFp + "|" + allowFp;

        // Skip hosts write only on true cold start (no published policy yet) AND no
        // leftover OS artifacts. If URLBlocklist/hosts/FW still present, fall through
        // to Apply([]) so a wiped dlp.db / version-0 boot still self-heals.
        if (wantDomains.Count == 0 && !lockdownOn && _lastWebFingerprint == "\0" && version <= 0
            && _cfg.WebBlock && !DomainBlockEnforcer.HasLeftoverOsArtifacts())
        {
            _lastWebFingerprint = "";
            _webBlock.Set(version, Array.Empty<string>(), DomainBlockEnforcer.CurrentGeneration);
            Report("web", "ok", "no_domains");
            return;
        }

        var drift = !DomainBlockEnforcer.OsStateMatchesDesired(wantDomains, lockdownAllow);
        if (combinedFp == _lastWebFingerprint && !drift)
        {
            // Domain set + OS match — still refresh CDN IPs when cache TTL expires.
            if (wantDomains.Count > 0)
            {
                var refresh = DomainBlockEnforcer.RefreshStaleFirewallIps(wantDomains);
                if (refresh.Changed)
                {
                    _logger.LogInformation(
                        "Web firewall IP refresh ({Detail})", refresh.Detail);
                    await _store.AuditAsync("web_fw_ip_refresh", refresh.Detail, ct);
                }
            }
            _webBlock.Set(version, wantDomains, DomainBlockEnforcer.CurrentGeneration);
            Report("web", "ok", wantDomains.Count == 0 ? "clear" : "block");
            return;
        }

        if (combinedFp == _lastWebFingerprint && drift)
        {
            _logger.LogWarning(
                "Web block OS drift vs policy (count={Count}) — reconciling hosts/FW/PIN/URLBlocklist",
                wantDomains.Count);
            await _store.AuditAsync(
                "web_domain_reconcile",
                $"count={wantDomains.Count} fp_unchanged=1",
                ct);
        }

        var result = DomainBlockEnforcer.Apply(wantDomains, lockdownAllow);
        var auditAction = wantDomains.Count == 0 ? "web_domain_clear" : "web_domain_apply";
        await _store.AuditAsync(
            auditAction,
            $"count={wantDomains.Count} changed={result.Changed} detail={result.Detail}",
            ct);

        if (result.Detail == "access_denied_need_admin"
            || result.Detail.StartsWith("browser_error", StringComparison.Ordinal)
            || result.Detail.StartsWith("pin_clear_failed", StringComparison.Ordinal)
            || result.Detail.StartsWith("fw_clear_incomplete", StringComparison.Ordinal)
            || result.Detail.StartsWith("fw_error", StringComparison.Ordinal))
        {
            if (DateTimeOffset.UtcNow - _lastAccessDeniedLog > TimeSpan.FromMinutes(2))
            {
                _lastAccessDeniedLog = DateTimeOffset.UtcNow;
                _logger.LogWarning(
                    "Web domain enforce needs Administrator / LocalSystem or clear incomplete ({Path}). detail={Detail}",
                    DomainBlockEnforcer.HostsPath, result.Detail);
            }

            if (!_webAccessDeniedAlertQueued && wantDomains.Count > 0
                && result.Detail == "access_denied_need_admin")
            {
                _webAccessDeniedAlertQueued = true;
                var rule = PolicyBundleParser.FirstWebBlockRule(bundle, DateTimeOffset.UtcNow);
                await _store.EnqueueAlertAsync(
                    Guid.NewGuid().ToString(),
                    string.IsNullOrEmpty(rule?.Id) ? null : rule.Id,
                    version,
                    "web_navigate",
                    rule?.Severity ?? "high",
                    "blocked_failed",
                    DateTimeOffset.UtcNow,
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["reason"] = result.Detail ?? "",
                        ["domains"] = string.Join(",", wantDomains),
                    }),
                    ct);
            }
            // Do NOT advance fingerprint — retry next tick (PIN leftovers / URLBlocklist).
            Report("web", "failed", result.Detail ?? "");
            return;
        }

        // Refuse fingerprint advance until OS matches desired (empty or shrunk set).
        if (!DomainBlockEnforcer.OsStateMatchesDesired(wantDomains, lockdownAllow))
        {
            _logger.LogWarning(
                "Web domain apply incomplete ({Detail}) — OS still drifts; will retry",
                result.Detail);
            await _store.AuditAsync(
                wantDomains.Count == 0 ? "web_domain_clear_incomplete" : "web_domain_reconcile_incomplete",
                result.Detail ?? "",
                ct);
            Report("web", "failed", result.Detail ?? "incomplete");
            return;
        }

        _lastWebFingerprint = combinedFp;
        _webAccessDeniedAlertQueued = false;
        _webBlock.Set(version, wantDomains, DomainBlockEnforcer.CurrentGeneration);
        Report("web", "ok", result.Detail ?? "");
        _logger.LogInformation(
            "Web domains {State} (count={Count}, lockdownAllow={Allow}, changed={Changed}, gen={Gen}, detail={Detail})",
            wantDomains.Count > 0 ? "BLOCK" : (lockdownOn ? "LOCKDOWN" : "CLEAR"),
            wantDomains.Count,
            lockdownAllow?.Count ?? 0,
            result.Changed,
            DomainBlockEnforcer.CurrentGeneration,
            result.Detail);

        if (result.Changed && wantDomains.Count > 0)
        {
            var rule = PolicyBundleParser.FirstWebBlockRule(bundle, DateTimeOffset.UtcNow);
            await _store.EnqueueAlertAsync(
                Guid.NewGuid().ToString(),
                string.IsNullOrEmpty(rule?.Id) ? null : rule.Id,
                version,
                "web_navigate",
                rule?.Severity ?? "high",
                "blocked",
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["action"] = "hosts_and_firewall",
                    ["domains"] = string.Join(",", wantDomains),
                    ["detail"] = result.Detail ?? "",
                    ["count"] = wantDomains.Count.ToString(),
                }),
                ct);
        }
    }

    /// <summary>
    /// Fingerprint helpers for lockdown allow set (platformAllow + allow.hosts + API host,
    /// minus explicit web-block domains). Enforcement is network lockdown, not browser HKLM.
    /// </summary>
    private static IReadOnlyList<string> BuildLockdownBrowserAllowHosts(
        PolicyBundle bundle,
        IReadOnlyList<string> blockDomains,
        string? serverUrl)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in blockDomains)
        {
            foreach (var n in PolicyBundleParser.NormalizeDomain(d))
                blocked.Add(n);
        }

        var allow = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var n in PolicyBundleParser.NormalizeDomain(raw))
            {
                if (!blocked.Contains(n))
                    allow.Add(n);
            }
        }

        foreach (var h in bundle.PlatformAllowHosts) Add(h);
        foreach (var h in bundle.AllowHosts) Add(h);

        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            var s = serverUrl.Trim();
            if (!s.Contains("://", StringComparison.Ordinal))
                s = "https://" + s;
            if (Uri.TryCreate(s, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
                Add(u.Host.Trim('[', ']'));
        }

        return allow.ToList();
    }
}
