using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Protected uninstall pipeline (CLI-driven):
/// <list type="number">
/// <item><b>Approve</b> — verify code with server (instant; fail on 401).</item>
/// <item><b>Stop</b> — stop collectors / kill takeover; disable auto-start watchdog bait.</item>
/// <item><b>Sync</b> — drain unsynced rows (bounded).</item>
/// <item><b>Status</b> — <c>self-remove</c>.</item>
/// <item><b>Wipe</b> — local account + machine id.</item>
/// <item><b>Teardown</b> — <c>teardown.ok</c>, pipe unlock, USB restore, delete DLP service, verify.</item>
/// </list>
/// Full success requires independently verified local postconditions — not merely
/// that <c>teardown.ok</c> exists.
/// </summary>
public sealed class ProtectedUninstallService
{
    private const int HttpVerifyTimeoutSec = 8;
    /// <summary>How long the CLI waits for the running tracker to start reporting phases.</summary>
    public static readonly TimeSpan HandoffGrace = TimeSpan.FromSeconds(5);
    /// <summary>Max wait for the full pipeline after handoff is alive.</summary>
    public static readonly TimeSpan PipelineBudget = TimeSpan.FromSeconds(90);

    private readonly AccountRemovalService _accountRemoval;
    private readonly AutoStartService? _autoStart;

    public ProtectedUninstallService(
        AccountRemovalService accountRemoval,
        AutoStartService? autoStart = null)
    {
        _accountRemoval = accountRemoval;
        _autoStart = autoStart;
    }

    /// <summary>
    /// Uninstall gate file. Windows: ProgramData (Inno checks). Linux: LocalAppData
    /// (user-writable) plus optional <c>/var/lib/&lt;package&gt;/dlp/teardown.ok</c> for
    /// root <c>prerm</c> (see <see cref="LinuxVarLibTeardownOkPath"/>).
    /// </summary>
    public static string TeardownOkPath =>
        Path.Combine(DlpStateDirectory, "teardown.ok");

    public static string ApprovedMarkerPath =>
        Path.Combine(DlpStateDirectory, "removal.approved");

    /// <summary>Machine-wide Linux gate for dpkg prerm (when directory is writable).</summary>
    public static string LinuxVarLibTeardownOkPath =>
        Path.Combine("/var/lib", AppInfo.PackageName, "dlp", "teardown.ok");

    private static string DlpStateDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    AppInfo.DataFolderName, "dlp");
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInfo.DataFolderName, "dlp");
        }
    }

    /// <summary>Last structured report from <see cref="RunPipelineDetailedAsync"/>.</summary>
    public TeardownReport? LastReport { get; private set; }

    /// <summary>
    /// Full pipeline. Skip approve when <paramref name="codeAlreadyVerified"/>
    /// (CLI already consumed the one-time code).
    /// Returns true only for <see cref="TeardownReport.FullLocalSuccess"/>.
    /// </summary>
    public async Task<bool> RunPipelineAsync(
        string rawCode,
        string purpose = "both",
        CancellationToken ct = default,
        Action<string>? progress = null,
        bool codeAlreadyVerified = false)
    {
        var report = await RunPipelineDetailedAsync(
            rawCode, purpose, ct, progress, codeAlreadyVerified).ConfigureAwait(false);
        return report.FullLocalSuccess;
    }

    public async Task<TeardownReport> RunPipelineDetailedAsync(
        string rawCode,
        string purpose = "both",
        CancellationToken ct = default,
        Action<string>? progress = null,
        bool codeAlreadyVerified = false)
    {
        var code = NormalizeCode(rawCode);
        RemovalProgress.Clear();
        var steps = new List<string>();

        try
        {
            Log("Starting protected uninstall pipeline");

            if (!codeAlreadyVerified)
            {
                if (string.IsNullOrEmpty(code))
                {
                    Fail("empty removal code");
                    return Finish(new TeardownReport
                    {
                        FailedOperation = "approve",
                        Detail = "empty removal code",
                    });
                }

                Set(progress, "approve", "Approving with server");
                Log("Approving removal code with server");
                if (!await VerifyCodeOnlyAsync(code, purpose, ct).ConfigureAwait(false))
                {
                    return Finish(new TeardownReport
                    {
                        FailedOperation = "approve",
                        Detail = "removal code rejected or verify failed",
                    });
                }
                ClearApprovedMarker();
                Log("Approval completed");
            }
            else
            {
                Set(progress, "approve", "Approved (CLI)");
                ClearApprovedMarker();
                Log("Approval skipped (code already verified)");
            }

            // Prevent BackgroundGuard / Run-key relaunch during teardown.
            BackgroundGuardService.SuppressWatchdog = true;
            Set(progress, "stop", "Disabling auto-start");
            var autoClearedEarly = TryDisableAutoStart(steps);
            Log($"Auto-start disable early: {autoClearedEarly}");

            // Code already verified above — Fixed gate must not block protected-uninstall.
            var notified = await _accountRemoval.RunAsync(
                ct,
                phase =>
                {
                    var (p, label) = phase switch
                    {
                        "stop" => ("stop", "Stopping collectors"),
                        "sync" => ("sync", "Syncing unsynced data"),
                        "status" => ("status", "Updating server status"),
                        "wipe" => ("wipe", "Wiping local account"),
                        _ => ("sync", phase),
                    };
                    Set(progress, p, label);
                },
                fixedModeCodeVerified: true).ConfigureAwait(false);

            Log($"Account removal completed (serverNotified={notified})");
            if (!notified)
            {
                Console.Error.WriteLine(
                    "[uninstall] Server self-remove did not confirm — local wipe done; continuing DLP teardown.");
            }

            Set(progress, "teardown", "Unlocking DLP / removing service");
            var teardown = await FinalizeTeardownDetailedAsync(purpose, ct, progress, steps)
                .ConfigureAwait(false);

            var autoCleared = autoClearedEarly || TryDisableAutoStart(steps) ||
                              !(_autoStart?.IsAutoStartEnabled() ?? IsWindowsAutoStartPresent());

            var report = new TeardownReport
            {
                AccountRemoved = true,
                ServerNotified = notified,
                GateWritten = teardown.GateWritten,
                PipeUnlockOk = teardown.PipeUnlockOk,
                PipeTimedOut = teardown.PipeTimedOut,
                UsbRestored = teardown.UsbRestored,
                HostsCleared = teardown.HostsCleared,
                DlpServiceGone = teardown.DlpServiceGone,
                AutoStartCleared = autoCleared,
                CleanupPendingElevation = teardown.CleanupPendingElevation,
                FailedOperation = teardown.FailedOperation,
                Detail = teardown.Detail,
                Steps = steps,
            };

            if (report.FullLocalSuccess)
            {
                RemovalProgress.Write("done", "protected-uninstall successfully");
                Log("Full uninstall verified");
            }
            else
            {
                RemovalProgress.Write("failed", report.SummaryLine);
                Console.Error.WriteLine($"[uninstall] {report.SummaryLine}");
                foreach (var s in steps)
                    Console.Error.WriteLine($"[uninstall]   · {s}");
            }

            return Finish(report);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return Finish(new TeardownReport
            {
                FailedOperation = "pipeline",
                Detail = ex.Message,
                Steps = steps,
            });
        }
    }

    /// <summary>Approve only — consumes the one-time code. Instant fail on 401.</summary>
    public static async Task<bool> ApproveAsync(
        string rawCode,
        string purpose = "both",
        CancellationToken ct = default,
        Action<string>? progress = null)
    {
        var code = NormalizeCode(rawCode);
        if (string.IsNullOrEmpty(code))
        {
            Fail("empty removal code");
            return false;
        }

        RemovalProgress.Clear();
        Set(progress, "approve", "Approving with server");
        if (!await VerifyCodeOnlyAsync(code, purpose, ct).ConfigureAwait(false))
            return false;

        WriteApprovedMarker(purpose);
        RemovalProgress.Write("approve", "Approved");
        progress?.Invoke("Approved");
        return true;
    }

    public static async Task<bool> FinalizeTeardownAsync(
        string purpose = "both",
        CancellationToken ct = default)
    {
        var r = await FinalizeTeardownDetailedAsync(purpose, ct).ConfigureAwait(false);
        return r.GateWritten && r.UsbRestored && r.HostsCleared && r.DlpServiceGone;
    }

    public static async Task<TeardownSlice> FinalizeTeardownDetailedAsync(
        string purpose = "both",
        CancellationToken ct = default,
        Action<string>? progress = null,
        List<string>? steps = null)
    {
        steps ??= new List<string>();
        var sw = Stopwatch.StartNew();

        Log("Writing teardown.ok gate");
        Set(progress, "teardown", "Writing uninstall gate");
        if (!WriteTeardownOkLocal(purpose))
        {
            steps.Add("gate write failed");
            return new TeardownSlice
            {
                FailedOperation = "write_teardown_ok",
                Detail = "failed to write teardown.ok",
            };
        }
        steps.Add($"gate written ({sw.ElapsedMilliseconds}ms)");
        Log("Gate written");

        var pipeOk = false;
        var pipeTimedOut = false;
        if (OperatingSystem.IsWindows())
        {
            Log("Unlocking DLP teardown via named pipe");
            Set(progress, "teardown", "Unlocking DLP service");
            // Retry: a single write_timeout often means another pipe client held the
            // (old) single accept loop — second attempt usually succeeds after concurrent accept.
            for (var attempt = 1; attempt <= 3 && !pipeOk; attempt++)
            {
                var pipeSw = Stopwatch.StartNew();
                var pipeTask = DlpPipeClient.ApplyTeardownGateDetailedAsync(purpose, CancellationToken.None);
                var pipeFinished = await Task.WhenAny(
                        pipeTask,
                        Task.Delay(DlpPipeClient.ApplyTeardownOverallTimeoutMs + 1_000, CancellationToken.None))
                    .ConfigureAwait(false);
                if (pipeFinished == pipeTask)
                {
                    var pipe = await pipeTask.ConfigureAwait(false);
                    pipeOk = pipe.Ok;
                    pipeTimedOut = pipe.TimedOut;
                    steps.Add(
                        $"pipe unlock attempt={attempt} ok={pipeOk} timedOut={pipeTimedOut} " +
                        $"err={pipe.Error ?? "none"} {pipeSw.ElapsedMilliseconds}ms");
                    if (pipeOk)
                        Log("DLP unlock completed");
                    else
                        Log($"DLP unlock failed (attempt {attempt}): {pipe.Error ?? "not_ok"}");
                }
                else
                {
                    pipeTimedOut = true;
                    steps.Add($"pipe unlock attempt={attempt} abandoned after {pipeSw.ElapsedMilliseconds}ms");
                    Log($"DLP unlock timed out (attempt {attempt})");
                }

                if (!pipeOk && attempt < 3)
                    await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }

            if (!pipeOk)
                Log("DLP unlock failed after retries — continuing SYSTEM / elevated cleanup");

            // Brief settle only when unlock reported ok; never delay on timeout.
            // Service Clear() runs async after pipe reply — give it a moment, then
            // CLI clears hosts itself (does not rely on that race).
            if (pipeOk)
                await Task.Delay(1_200, CancellationToken.None).ConfigureAwait(false);

            Log("Stopping/removing DLP service + restoring USBSTOR + clearing hosts");
            Set(progress, "teardown", "Removing DLP service registration");
            var cleanupSw = Stopwatch.StartNew();
            var cleanup = WindowsTeardownCleanup.RestoreUsbAndRemoveDlpService();
            steps.AddRange(cleanup.Steps);
            steps.Add($"cleanup summary={cleanup.Summary} {cleanupSw.ElapsedMilliseconds}ms");

            // Unelevated CLI cannot edit hosts / AppLocker / sc stop LocalSystem — prompt UAC.
            var appLockerStillOn = WindowsTeardownCleanup.LooksLikeAppLockerExeEnforced();
            if ((!cleanup.ServiceGone || !cleanup.UsbRestored || !cleanup.HostsCleared ||
                 appLockerStillOn) &&
                !cleanup.IsProcessElevated)
            {
                Log("Cleanup incomplete without admin — requesting UAC elevation");
                Set(progress, "teardown", "Administrator approval required for hosts/DLP/AppLocker cleanup");
                var elevated = WindowsTeardownCleanup.TryElevateCleanupViaUac(steps);
                cleanup = elevated;
                steps.Add($"elevate summary={elevated.Summary}");
                appLockerStillOn = WindowsTeardownCleanup.LooksLikeAppLockerExeEnforced();
            }

            Log(
                $"USB restored={cleanup.UsbRestored}; hostsCleared={cleanup.HostsCleared}; " +
                $"DLP gone={cleanup.ServiceGone}; appLockerCleared={!appLockerStillOn}");

            var pendingElevation =
                (!cleanup.ServiceGone || !cleanup.HostsCleared || appLockerStillOn) &&
                !WindowsTeardownCleanup.IsElevated();
            string? failedOp = null;
            string? detail = null;
            if (!cleanup.ServiceGone)
            {
                failedOp = "remove_dlp_service";
                detail = cleanup.IsProcessElevated
                    ? "service still registered after elevated cleanup"
                    : "service still registered — re-run elevated or uninstall from Apps & features";
            }
            else if (!cleanup.UsbRestored)
            {
                failedOp = "restore_usbstor";
                detail = $"USBSTOR Start={WindowsTeardownCleanup.ReadUsbStorStart()?.ToString() ?? "?"}";
            }
            else if (!cleanup.HostsCleared)
            {
                failedOp = "clear_web_block";
                detail = "AlphaAI-DLP hosts/firewall entries still present";
            }
            else if (appLockerStillOn)
            {
                failedOp = "clear_applocker";
                detail =
                    "AppLocker Exe still Enabled — reboot may be required; see removeTracker.md";
            }

            Set(progress, "teardown", "Verifying remaining services");
            Log($"Verifying: service={WindowsTeardownCleanup.QueryServiceState(AppInfo.WindowsServiceName)} " +
                $"usbstor={WindowsTeardownCleanup.ReadUsbStorStart()?.ToString() ?? "?"} " +
                $"hostsCleared={cleanup.HostsCleared} appLockerCleared={!appLockerStillOn}");

            return new TeardownSlice
            {
                GateWritten = File.Exists(TeardownOkPath),
                PipeUnlockOk = pipeOk,
                PipeTimedOut = pipeTimedOut,
                UsbRestored = cleanup.UsbRestored,
                HostsCleared = cleanup.HostsCleared,
                DlpServiceGone = cleanup.ServiceGone,
                CleanupPendingElevation = pendingElevation,
                FailedOperation = failedOp,
                Detail = detail,
            };
        }

        // Linux/macOS: no DLP SCM — stop user unit + clear autostart (AutoStartService).
        if (OperatingSystem.IsLinux())
        {
            Set(progress, "teardown", "Stopping Linux background unit");
            try
            {
                var unit = LinuxUserSystemdUnit.ServiceFileName;
                _ = RunQuietSystemctl($"--user disable --now {unit}");
                if (File.Exists(LinuxUserSystemdUnit.ServicePath))
                    File.Delete(LinuxUserSystemdUnit.ServicePath);
                _ = RunQuietSystemctl("--user daemon-reload");
                steps.Add($"linux unit disabled/removed ({unit})");
            }
            catch (Exception ex)
            {
                steps.Add($"linux unit teardown warning: {ex.Message}");
            }
        }

        return new TeardownSlice
        {
            GateWritten = File.Exists(TeardownOkPath),
            UsbRestored = true,
            HostsCleared = true,
            DlpServiceGone = true,
        };
    }

    private static int RunQuietSystemctl(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return -1;
            proc.WaitForExit(8_000);
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    public sealed class TeardownSlice
    {
        public bool GateWritten { get; init; }
        public bool PipeUnlockOk { get; init; }
        public bool PipeTimedOut { get; init; }
        public bool UsbRestored { get; init; }
        public bool HostsCleared { get; init; }
        public bool DlpServiceGone { get; init; }
        public bool CleanupPendingElevation { get; init; }
        public string? FailedOperation { get; init; }
        public string? Detail { get; init; }
    }

    /// <summary>
    /// CLI when another tracker holds the mutex: approve HERE, then always kill
    /// the running instance and take over. Handoff-and-wait was unreliable
    /// (progress stalls → 90s timeout with USB never restored).
    /// </summary>
    public static async Task<HandoffResult> ApproveAndHandoffAsync(
        string rawCode,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!await ApproveAsync(NormalizeCode(rawCode), "both", ct, progress).ConfigureAwait(false))
            return HandoffResult.Failed;

        Set(progress, "stop", "Stopping running tracker");
        _ = SingleInstanceService.SignalExistingInstance(
            SingleInstanceService.CommandProtectedUninstallGo);

        await Task.Delay(800, ct).ConfigureAwait(false);

        Set(progress, "stop", "Taking over — sync and wipe in this process");
        KillOtherClientProcesses();
        return HandoffResult.TakeOver;
    }

    /// <summary>
    /// CLI when another tracker holds the mutex for <c>--remove-account</c>.
    /// Same reliability model as <see cref="ApproveAndHandoffAsync"/>: do not sit out a
    /// long pipeline wait on a stuck peer. Critical: never write <c>phase=stop</c> into
    /// <see cref="RemovalProgress"/> from the CLI — that falsely looks like the peer
    /// started work and used to strand the spinner on "Signaling running tracker…" for
    /// <see cref="PipelineBudget"/> without killing the still-running process (mutex stay
    /// held → "could not acquire tracker lock" after the wipe already succeeded).
    /// </summary>
    public static async Task<bool> WaitForRemoveAccountAsync(
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        RemovalProgress.Clear();
        // Spinner only — must NOT call Set() here (Set writes phase=stop to the shared file).
        progress?.Invoke("Signaling running tracker");

        var signaled = SingleInstanceService.SignalExistingInstance(
            SingleInstanceService.CommandRemoveAccount);
        if (!signaled)
        {
            progress?.Invoke("Could not reach running tracker — taking over locally");
            KillOtherClientProcesses();
            return false; // caller takes over
        }

        // Short cooperative window: if the peer finishes quickly, exit success without kill.
        var graceDeadline = DateTime.UtcNow + HandoffGrace;
        var sawPeerWork = false;
        string? lastLabel = null;
        while (DateTime.UtcNow < graceDeadline)
        {
            ct.ThrowIfCancellationRequested();
            if (RemovalProgress.TryRead(out var phase, out var label))
            {
                if (!string.IsNullOrEmpty(label) && label != lastLabel)
                {
                    lastLabel = label;
                    progress?.Invoke(label);
                }

                if (phase == "done") return true;
                if (phase == "failed") return false;
                // After Clear(), any phase is from the peer (CLI no longer seeds phase=stop).
                if (phase is "stop" or "sync" or "status" or "wipe")
                    sawPeerWork = true;
            }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        if (sawPeerWork)
        {
            // Peer is actively removing — give it a bounded finish window, then take over.
            var finished = await WaitUntilTerminalAsync(progress, lastLabel, ct).ConfigureAwait(false);
            if (finished)
                return true;

            progress?.Invoke("Tracker stuck — stopping it and taking over");
            KillOtherClientProcesses();
            // Clear the timeout "failed" marker so a cold-path wipe can report cleanly.
            RemovalProgress.Clear();
            return false;
        }

        progress?.Invoke("No response — taking over locally");
        KillOtherClientProcesses();
        return false;
    }

    public enum HandoffResult { Completed, Failed, TakeOver }

    public static void EnsureCliConsole() => CliConsole.Ensure();

    public static bool HasApprovedMarker() => File.Exists(ApprovedMarkerPath);

    /// <summary>
    /// cmd.exe treats <c>'code'</c> as literal quotes (unlike PowerShell). Strip a
    /// matching pair so the SHA-256 hash still matches the server challenge.
    /// </summary>
    public static string NormalizeCode(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length >= 2)
        {
            var a = s[0];
            var b = s[^1];
            if ((a == '\'' && b == '\'') || (a == '"' && b == '"'))
                s = s[1..^1].Trim();
        }
        return s;
    }

    public static void KillOtherClientProcesses()
    {
        var self = Environment.ProcessId;
        // Installed binary is client.exe; also match rebranded process names and any
        // process whose MainModule path sits under our install / data folders.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "client",
            "AlphaAI",
            AppInfo.ExecutableName,
            AppInfo.AppMutex,
        };

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (proc.Id == self) continue;
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                    }
                    catch { /* ignore */ }
                    finally { proc.Dispose(); }
                }
            }
            catch { /* ignore */ }
        }

        // Second pass: path-based (covers odd process names after rebrand).
        try
        {
            var markers = new[]
            {
                AppInfo.DataFolderName,
                "Alpha AI Tracker",
                AppInfo.AppMutex,
            };
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id == self) continue;
                    string? path = null;
                    try { path = proc.MainModule?.FileName; } catch { /* access denied */ }
                    if (string.IsNullOrEmpty(path)) continue;
                    var hit = false;
                    foreach (var m in markers)
                    {
                        if (!string.IsNullOrEmpty(m) &&
                            path.Contains(m, StringComparison.OrdinalIgnoreCase))
                        {
                            hit = true;
                            break;
                        }
                    }
                    if (!hit) continue;
                    // Only kill our own client binary, not the DLP service exe.
                    if (path.EndsWith("Service.exe", StringComparison.OrdinalIgnoreCase))
                        continue;
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
                catch { /* ignore */ }
                finally
                {
                    try { proc.Dispose(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }

        Thread.Sleep(600);
    }

    // ── internals ────────────────────────────────────────────────────────────

    private TeardownReport Finish(TeardownReport report)
    {
        LastReport = report;
        return report;
    }

    private bool TryDisableAutoStart(List<string> steps)
    {
        try
        {
            if (_autoStart is not null)
            {
                var ok = _autoStart.DisableAutoStart();
                var still = _autoStart.IsAutoStartEnabled();
                steps.Add($"auto-start disable via service ok={ok} stillPresent={still}");
                return !still;
            }

            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                key?.DeleteValue("AlphaAITracker", throwOnMissingValue: false);
                var gone = !IsWindowsAutoStartPresent();
                steps.Add($"auto-start Run key cleared={gone}");
                return gone;
            }
        }
        catch (Exception ex)
        {
            steps.Add("auto-start disable error: " + ex.Message);
        }
        return false;
    }

    private static bool IsWindowsAutoStartPresent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("AlphaAITracker") != null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitUntilTerminalAsync(
        Action<string>? progress, string? lastLabel, CancellationToken ct)
    {
        // Remove-account handoff: keep this shorter than the old 90s stall. Peer drain
        // is already capped (~45s); if it has not finished by then, kill + take over.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(50);
        lastLabel ??= "";
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (RemovalProgress.TryRead(out var phase, out var label))
            {
                if (!string.IsNullOrEmpty(label) && label != lastLabel)
                {
                    lastLabel = label;
                    progress?.Invoke(label);
                }

                if (phase == "done") return true;
                if (phase == "failed") return false;
            }

            if (File.Exists(TeardownOkPath) &&
                RemovalProgress.TryRead(out var p2, out _) &&
                p2 is "teardown" or "done")
                return true;

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        // Do not use Fail() — that stamps "protected-uninstall failed" on a remove-account path.
        RemovalProgress.Write("failed", "timed out waiting for remove-account pipeline");
        Console.Error.WriteLine("[uninstall] remove-account — timed out waiting for pipeline to finish");
        return false;
    }

    private static async Task<bool> VerifyCodeOnlyAsync(
        string code, string purpose, CancellationToken ct)
    {
        if (!TryReadDeviceAuth(out var serverUrl, out var deviceToken) ||
            string.IsNullOrEmpty(deviceToken))
        {
            if (!TryRecoverDeviceToken(out var recovered, out var employeeId, out var source, out var why))
            {
                Fail(why);
                return false;
            }

            var url = ConfiguredServerUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                Fail("no server URL in the loaded config (ALPHA_SERVER_URL) — cannot verify the removal code");
                return false;
            }

            // Recovery is not approval — removal-verify must still succeed.
            // On Windows, republish device.auth for the LocalSystem DLP service.
            // On Linux/macOS there is no ProgramData bridge (Publish is a no-op), so
            // use the recovered token + config server URL directly.
            deviceToken = recovered;
            if (OperatingSystem.IsWindows())
            {
                DlpDeviceCredentialBridge.Publish(recovered, employeeId, url);
                if (!TryReadDeviceAuth(out serverUrl, out deviceToken) || string.IsNullOrEmpty(deviceToken))
                {
                    Fail("found a device token in the tracker database but could not write device.auth");
                    return false;
                }

                Log($"repaired device.auth from {source} (token length {deviceToken!.Length}). Server approval is still required.");
                serverUrl = string.IsNullOrWhiteSpace(serverUrl) ? url : serverUrl;
            }
            else
            {
                Log($"using device token from {source} (token length {deviceToken!.Length}). Server approval is still required.");
                serverUrl = url;
            }
        }

        if (string.IsNullOrWhiteSpace(serverUrl))
            serverUrl = ConfiguredServerUrl();
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrEmpty(deviceToken))
        {
            Fail("device credential is incomplete — server URL or token is empty");
            return false;
        }

        return await VerifyHttpAsync(serverUrl, deviceToken!, code, purpose, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Server URL already loaded from <c>config.enc</c> into the process environment.
    /// Does not fall back to a baked-in host.
    /// </summary>
    internal static string ConfiguredServerUrl() =>
        (Environment.GetEnvironmentVariable("ALPHA_SERVER_URL") ?? "").Trim().TrimEnd('/');

    internal static bool TryRecoverDeviceToken(
        out string token, out string? employeeId, out string? sourcePath, out string failure)
    {
        token = "";
        employeeId = null;
        sourcePath = null;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = Environment.GetEnvironmentVariable("ALPHA_DB_PATH");
        var paths = TrackerDeviceTokenReader.CandidatePaths(local, profile, configured);
        if (!TrackerDeviceTokenReader.TryPickNewest(paths, out token, out employeeId, out sourcePath, out var diagnostic))
        {
            failure = "no device.auth and no device token in the tracker database — login the tracker once, then retry. "
                + diagnostic;
            return false;
        }

        failure = "";
        return true;
    }

    private static async Task<bool> VerifyHttpAsync(
        string serverUrl,
        string deviceToken,
        string code,
        string purpose,
        CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(HttpVerifyTimeoutSec) };
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(HttpVerifyTimeoutSec));

            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"{serverUrl.TrimEnd('/')}/api/v1/devices/removal-verify")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { code, purpose }),
                    Encoding.UTF8,
                    "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Device", deviceToken);
            ClientIdentityHeaders.Apply(req);

            using var resp = await http.SendAsync(req, linked.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Fail(
                    $"removal code rejected (HTTP {(int)resp.StatusCode}) — " +
                    "issue a FRESH code for THIS device on the web and retry. " +
                    $"Detail: {TrimBody(body)}");
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("valid", out var v) && v.GetBoolean())
                return true;

            Fail("removal code invalid, expired, or already used — issue a fresh code");
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Fail($"removal-verify timed out after {HttpVerifyTimeoutSec}s");
            return false;
        }
        catch (Exception ex)
        {
            Fail($"removal-verify failed: {ex.Message}");
            return false;
        }
    }

    private static string TrimBody(string body)
    {
        body = (body ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return body.Length <= 180 ? body : body[..180] + "…";
    }

    private static bool TryReadDeviceAuth(out string serverUrl, out string? token)
    {
        serverUrl = "";
        token = null;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppInfo.DataFolderName, "dlp", "device.auth");
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("serverUrl", out var su))
                serverUrl = (su.GetString() ?? "").TrimEnd('/');
            if (doc.RootElement.TryGetProperty("deviceToken", out var dt))
                token = dt.GetString();
            return !string.IsNullOrWhiteSpace(token);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteApprovedMarker(string purpose)
    {
        try
        {
            var dir = Path.GetDirectoryName(ApprovedMarkerPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(ApprovedMarkerPath,
                $"authorizedAt={DateTime.UtcNow:o}\npurpose={purpose}\n");
        }
        catch { /* best-effort */ }
    }

    private static void ClearApprovedMarker()
    {
        try
        {
            if (File.Exists(ApprovedMarkerPath)) File.Delete(ApprovedMarkerPath);
        }
        catch { /* ignore */ }
    }

    private static bool WriteTeardownOkLocal(string purpose)
    {
        try
        {
            var payload = $"authorizedAt={DateTime.UtcNow:o}\npurpose={purpose}\n";
            var dir = Path.GetDirectoryName(TeardownOkPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(TeardownOkPath, payload);

            // Best-effort machine-wide copy for dpkg prerm running as root.
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    var varDir = Path.GetDirectoryName(LinuxVarLibTeardownOkPath)!;
                    Directory.CreateDirectory(varDir);
                    File.WriteAllText(LinuxVarLibTeardownOkPath, payload);
                }
                catch
                {
                    /* postinst should seed /var/lib/<pkg>/dlp mode 1777; ignore if not yet */
                }
            }

            return File.Exists(TeardownOkPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[uninstall] Failed to write teardown.ok: {ex.Message}");
            return false;
        }
    }

    private static void Set(Action<string>? progress, string phase, string label)
    {
        RemovalProgress.Write(phase, label);
        progress?.Invoke(label);
    }

    private static void Fail(string reason)
    {
        RemovalProgress.Write("failed", reason);
        Console.Error.WriteLine($"[uninstall] protected-uninstall failed — {reason}");
    }

    private static void Log(string message) =>
        Console.WriteLine($"[uninstall] {message}");
}
