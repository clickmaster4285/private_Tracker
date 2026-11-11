using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using client.Core;
using Microsoft.Win32;

namespace client.Services;

/// <summary>
/// Privileged Windows teardown: restore USBSTOR Start=3, clear AlphaAI-DLP hosts
/// sink + firewall rules (COM first — <c>netsh</c> is AppLocker-blocked for users),
/// clear leftover AppLocker Exe Enforce (SrpV2 + NotConfigured), stop/delete the DLP
/// service. Prefer SYSTEM via Task Scheduler COM (same pattern as Inno). Every external
/// process is hard-killed on timeout so the CLI never hangs indefinitely. Results are
/// verified, not assumed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsTeardownCleanup
{
    public const int UsbStorStartManual = 3;
    private const int ProcessTimeoutMs = 15_000;
    private const int SystemTaskSettleMs = 4_000;

    // Must match DomainBlockEnforcer markers (DlpService).
    public const string HostsBeginMarker = "# AlphaAI-DLP-BEGIN";
    public const string HostsEndMarker = "# AlphaAI-DLP-END";
    public const string HostsMarkerPrefix = "# AlphaAI-DLP";
    public const string FirewallRulePrefix = "AlphaAI-DLP-";

    internal const string EmptyAppLockerPolicyXml =
        "<AppLockerPolicy Version=\"1\">" +
        "<RuleCollection Type=\"Exe\" EnforcementMode=\"NotConfigured\" />" +
        "<RuleCollection Type=\"Msi\" EnforcementMode=\"NotConfigured\" />" +
        "<RuleCollection Type=\"Script\" EnforcementMode=\"NotConfigured\" />" +
        "<RuleCollection Type=\"Appx\" EnforcementMode=\"NotConfigured\" />" +
        "<RuleCollection Type=\"Dll\" EnforcementMode=\"NotConfigured\" />" +
        "</AppLockerPolicy>";

    public sealed record TeardownCleanupResult(
        bool UsbRestored,
        bool ServiceGone,
        bool HostsCleared,
        bool AttemptedElevatedCleanup,
        bool IsProcessElevated,
        string Summary,
        IReadOnlyList<string> Steps);

    /// <summary>
    /// Restore USB + clear web domain sink + remove DLP service registration. Idempotent.
    /// </summary>
    public static TeardownCleanupResult RestoreUsbAndRemoveDlpService()
    {
        if (!OperatingSystem.IsWindows())
            return new TeardownCleanupResult(true, true, true, false, false, "not_windows", Array.Empty<string>());

        var steps = new List<string>();
        var svc = AppInfo.WindowsServiceName;
        var exe = AppInfo.WindowsServiceName + ".exe";
        var elevated = IsElevated();
        steps.Add($"context elevated={elevated}");

        var beforeState = QueryServiceState(svc);
        var beforeUsb = ReadUsbStorStart();
        steps.Add(
            $"before service={beforeState} usbstor={beforeUsb?.ToString() ?? "?"} " +
            $"hostsDirty={!IsWebDomainBlockCleared()}");

        // Clear hosts/FW/AppLocker BEFORE killing the service — pipe AuthorizeTeardown
        // Clear() is fire-and-forget and often write_timeouts under load.
        TryClearWebDomainBlockDirect(steps);
        TryClearAppLockerDirect(steps);
        TryClearBrowserPoliciesDirect(steps);

        // Prefer in-process SCM / Process APIs — AppLocker blocks sc.exe / taskkill.exe
        // for interactive users under app allowlist lockdown.
        TryScmCleanupNative(steps, svc, exe);
        TryRestoreUsbStorDirect(steps);
        TryClearWebDomainBlockDirect(steps);
        TryClearAppLockerDirect(steps);

        var afterDirectGone = !ServiceExists(svc);
        var afterDirectUsb = ReadUsbStorStart() == UsbStorStartManual;
        var afterDirectHosts = IsWebDomainBlockCleared();
        var afterDirectAppLocker = !LooksLikeAppLockerExeEnforced();
        if (afterDirectGone && afterDirectUsb && afterDirectHosts && afterDirectAppLocker)
        {
            steps.Add("verified after direct cleanup");
            return new TeardownCleanupResult(true, true, true, false, elevated,
                "verified_direct", steps);
        }

        // SYSTEM one-shot (Task Scheduler COM first — no schtasks.exe; then schtasks fallback).
        // Required when interactive powershell/netsh are AppLocker-denied (SYSTEM-only bootstrap).
        var systemOk = TrySystemCleanupTask(steps, svc, exe);
        Thread.Sleep(SystemTaskSettleMs);

        // Re-try after SYSTEM task may have opened/stopped the service.
        TryScmCleanupNative(steps, svc, exe, labelPrefix: "retry-");
        TryRestoreUsbStorDirect(steps);
        TryClearWebDomainBlockDirect(steps);
        TryClearAppLockerDirect(steps);
        TryClearBrowserPoliciesDirect(steps);

        var usbOk = ReadUsbStorStart() == UsbStorStartManual;
        var gone = !ServiceExists(svc);
        var hostsOk = IsWebDomainBlockCleared();
        var appLockerOk = !LooksLikeAppLockerExeEnforced();
        var afterState = QueryServiceState(svc);
        steps.Add(
            $"after service={afterState} usbstor={ReadUsbStorStart()?.ToString() ?? "?"} " +
            $"hostsCleared={hostsOk} appLockerCleared={appLockerOk} systemTask={systemOk}");

        var summary = (usbOk, gone, hostsOk, appLockerOk) switch
        {
            (true, true, true, true) => "verified",
            (true, true, true, false) => "service_gone_applocker_still_enforced",
            (true, true, false, _) => "service_gone_hosts_still_dirty",
            (false, true, _, _) => "service_gone_usbstor_not_manual",
            (true, false, _, _) => "usbstor_ok_service_still_registered",
            _ => "cleanup_incomplete",
        };

        return new TeardownCleanupResult(usbOk, gone, hostsOk, systemOk, elevated, summary, steps);
    }

    /// <summary>
    /// True when local AppLocker Exe EnforcementMode is Enabled (1) under SrpV2.
    /// </summary>
    public static bool LooksLikeAppLockerExeEnforced()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\SrpV2\Exe");
            if (key is null) return false;
            var mode = key.GetValue("EnforcementMode");
            return mode switch
            {
                int i => i == 1,
                string s => s == "1",
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    public static bool ServiceExists(string? serviceName = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var name = serviceName ?? AppInfo.WindowsServiceName;
        return QueryServiceState(name) is not ("absent" or "query_failed");
    }

    public static string QueryServiceState(string serviceName)
    {
        try
        {
            // Native OpenService — does not spawn sc.exe (AppLocker-safe).
            var native = WindowsScmNative.QueryServiceState(serviceName);
            if (!native.StartsWith("query_failed", StringComparison.Ordinal))
                return native;

            // Legacy fallback when native OpenSCManager itself fails.
            var (exit, stdout, stderr) = RunCapture("sc.exe", $"query \"{serviceName}\"", ProcessTimeoutMs);
            var text = stdout + "\n" + stderr;
            if (exit == 1060 || text.Contains("1060", StringComparison.Ordinal) ||
                text.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return "absent";
            if (text.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return "RUNNING";
            if (text.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return "STOPPED";
            if (text.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase)) return "STOP_PENDING";
            if (exit != 0) return $"query_failed:{exit};native={native}";
            return "present";
        }
        catch (Exception ex)
        {
            return "query_failed:" + ex.Message;
        }
    }

    public static int? ReadUsbStorStart()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\USBSTOR", writable: false);
            var v = key?.GetValue("Start");
            return v is int i ? i : v is long l ? (int)l : null;
        }
        catch
        {
            return null;
        }
    }

    public static string HostsPath
    {
        get
        {
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return Path.Combine(windir, "System32", "drivers", "etc", "hosts");
        }
    }

    /// <summary>True when no AlphaAI-DLP hosts markers and no owned firewall rules remain.</summary>
    public static bool IsWebDomainBlockCleared()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            if (File.Exists(HostsPath))
            {
                var text = File.ReadAllText(HostsPath);
                if (text.Contains(HostsMarkerPrefix, StringComparison.OrdinalIgnoreCase) ||
                    text.Contains(HostsBeginMarker, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        catch
        {
            // Unreadable hosts → treat as not cleared so SYSTEM path retries.
            return false;
        }

        return ListOwnedFirewallRules().Count == 0;
    }

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void TryScmCleanupNative(
        List<string> steps, string svc, string exe, string labelPrefix = "")
    {
        var fail = WindowsScmNative.TryClearFailureActions(svc);
        steps.Add($"{labelPrefix}scm-failure-clear ok={fail.Ok} {fail.Detail}");

        var stop = WindowsScmNative.TryStop(svc);
        steps.Add($"{labelPrefix}scm-stop ok={stop.Ok} {stop.Detail}");

        var del = WindowsScmNative.TryDelete(svc);
        steps.Add($"{labelPrefix}scm-delete ok={del.Ok} {del.Detail}");

        var kill = WindowsScmNative.TryKillByImageName(exe);
        steps.Add($"{labelPrefix}proc-kill killed={kill.Killed} {kill.Detail}");

        // If native delete failed with access denied (locked DACL) and sc.exe is still
        // allowed on this machine, try the legacy shell path once.
        if (!del.Ok && WindowsScmNative.IsAccessDeniedDetail(del.Detail))
        {
            RunLogged(steps, $"{labelPrefix}sc-stop-fallback", "sc.exe", $"stop \"{svc}\"", ProcessTimeoutMs);
            RunLogged(steps, $"{labelPrefix}sc-delete-fallback", "sc.exe", $"delete \"{svc}\"", ProcessTimeoutMs);
        }
    }

    private static void TryRestoreUsbStorDirect(List<string> steps)
    {
        // Registry API — no reg.exe (AppLocker-safe).
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\USBSTOR", writable: true);
            if (key is null)
            {
                steps.Add("reg-usbstor native missing_key");
            }
            else
            {
                key.SetValue("Start", UsbStorStartManual, RegistryValueKind.DWord);
                steps.Add("reg-usbstor native ok");
            }
        }
        catch (Exception ex)
        {
            steps.Add("reg-usbstor native error=" + ex.Message);
            RunLogged(steps, "reg-usbstor", "reg.exe",
                "add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\USBSTOR\" /v Start /t REG_DWORD /d 3 /f",
                ProcessTimeoutMs);
        }

        // Prefer native ChangeServiceConfig if we ever need it; sc config is best-effort.
        RunLogged(steps, "sc-usbstor-demand", "sc.exe",
            "config USBSTOR start= demand", ProcessTimeoutMs);
    }

    private static void TryClearWebDomainBlockDirect(List<string> steps)
    {
        try
        {
            var hostsDetail = ClearHostsFile();
            steps.Add("hosts-clear " + hostsDetail);
        }
        catch (Exception ex)
        {
            steps.Add("hosts-clear error=" + ex.Message);
        }

        try
        {
            var removed = ClearOwnedFirewallRulesCom();
            if (removed < 0)
            {
                removed = ClearOwnedFirewallRulesNetsh();
                steps.Add($"fw-clear netsh-fallback removed={removed}");
            }
            else
            {
                steps.Add($"fw-clear com removed={removed}");
            }
        }
        catch (Exception ex)
        {
            steps.Add("fw-clear error=" + ex.Message);
            try
            {
                var removed = ClearOwnedFirewallRulesNetsh();
                steps.Add($"fw-clear netsh-fallback removed={removed}");
            }
            catch (Exception ex2)
            {
                steps.Add("fw-clear netsh error=" + ex2.Message);
            }
        }

        // ipconfig.exe is often AppLocker-denied for interactive users — try cmdlet via PS.
        RunLogged(steps, "flushdns", "ipconfig.exe", "/flushdns", ProcessTimeoutMs);
        RunLogged(steps, "flushdns-ps", "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"Clear-DnsClientCache\"",
            ProcessTimeoutMs);
    }

    private static void TryClearAppLockerDirect(List<string> steps)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(
                @"SOFTWARE\Policies\Microsoft\Windows\SrpV2",
                throwOnMissingSubKey: false);
            steps.Add("applocker-srpv2-deleted");
        }
        catch (Exception ex)
        {
            steps.Add("applocker-srpv2 error=" + ex.Message);
        }

        try
        {
            var dir = TeardownScriptDir;
            var xmlPath = Path.Combine(dir, "applocker-clear.xml");
            File.WriteAllText(xmlPath, EmptyAppLockerPolicyXml, Encoding.Unicode);
            var quoted = xmlPath.Replace("'", "''");
            var exit = RunLogged(steps, "applocker-set", "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command " +
                $"\"Set-AppLockerPolicy -XmlPolicy '{quoted}'; " +
                "Set-Service -Name AppIDSvc -StartupType Automatic -ErrorAction SilentlyContinue; " +
                "Restart-Service -Name AppIDSvc -Force -ErrorAction SilentlyContinue\"",
                ProcessTimeoutMs);
            steps.Add(exit == 0
                ? "applocker-set ok"
                : "applocker-set failed (SYSTEM task may retry)");
        }
        catch (Exception ex)
        {
            steps.Add("applocker-set error=" + ex.Message);
        }
    }

    private static void TryClearBrowserPoliciesDirect(List<string> steps)
    {
        foreach (var root in new[]
                 {
                     @"SOFTWARE\Policies\Google\Chrome",
                     @"SOFTWARE\Policies\Microsoft\Edge",
                 })
        {
            foreach (var sub in new[] { "URLBlocklist", "URLAllowlist" })
            {
                try
                {
                    Registry.LocalMachine.DeleteSubKeyTree(root + "\\" + sub, throwOnMissingSubKey: false);
                    steps.Add($"browser-policy removed {root}\\{sub}");
                }
                catch (Exception ex)
                {
                    steps.Add($"browser-policy {sub} error=" + ex.Message);
                }
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root, writable: true);
                if (key is null) continue;
                foreach (var v in new[] { "DnsOverHttpsMode", "QuicAllowed" })
                {
                    try
                    {
                        if (key.GetValue(v) is not null)
                        {
                            key.DeleteValue(v, throwOnMissingValue: false);
                            steps.Add($"browser-policy value removed {root}\\{v}");
                        }
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }
    }

    private static string ClearHostsFile()
    {
        if (!File.Exists(HostsPath))
            return "hosts_absent";

        string original;
        try
        {
            original = File.ReadAllText(HostsPath, Encoding.UTF8);
        }
        catch (UnauthorizedAccessException)
        {
            return "access_denied_need_admin";
        }

        if (!original.Contains(HostsMarkerPrefix, StringComparison.OrdinalIgnoreCase) &&
            !original.Contains(HostsBeginMarker, StringComparison.OrdinalIgnoreCase))
            return "already_clean";

        var next = StripOwnedHostsBlock(original).TrimEnd() + "\r\n";
        try
        {
            var tmp = HostsPath + ".alpha-dlp-teardown.tmp";
            File.WriteAllText(tmp, next, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Copy(tmp, HostsPath, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
            return "hosts_cleared";
        }
        catch (UnauthorizedAccessException)
        {
            return "access_denied_need_admin";
        }
    }

    internal static string StripOwnedHostsBlock(string content)
    {
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        var inBlock = false;
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Equals(HostsBeginMarker, StringComparison.OrdinalIgnoreCase))
            {
                inBlock = true;
                continue;
            }
            if (t.Equals(HostsEndMarker, StringComparison.OrdinalIgnoreCase))
            {
                inBlock = false;
                continue;
            }
            if (inBlock)
                continue;
            if (t.Contains(HostsMarkerPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(line);
        }
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1]))
            kept.RemoveAt(kept.Count - 1);
        return string.Join("\r\n", kept);
    }

    /// <summary>COM firewall wipe — works when <c>netsh.exe</c> is AppLocker-denied.</summary>
    /// <returns>Count removed, or -1 if COM unavailable.</returns>
    private static int ClearOwnedFirewallRulesCom()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        if (type is null) return -1;
        object? fw;
        try { fw = Activator.CreateInstance(type); }
        catch { return -1; }
        if (fw is null) return -1;

        object? rulesObj;
        try
        {
            rulesObj = type.InvokeMember(
                "Rules",
                System.Reflection.BindingFlags.GetProperty,
                binder: null,
                target: fw,
                args: null);
        }
        catch { return -1; }
        if (rulesObj is null) return -1;

        var names = new List<string>();
        try
        {
            foreach (var rule in (System.Collections.IEnumerable)rulesObj)
            {
                if (rule is null) continue;
                var nameObj = rule.GetType().InvokeMember(
                    "Name",
                    System.Reflection.BindingFlags.GetProperty,
                    binder: null,
                    target: rule,
                    args: null);
                var name = nameObj as string;
                if (!string.IsNullOrEmpty(name) &&
                    name.StartsWith(FirewallRulePrefix, StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }
        catch
        {
            return -1;
        }

        var removed = 0;
        var rulesType = rulesObj.GetType();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                rulesType.InvokeMember(
                    "Remove",
                    System.Reflection.BindingFlags.InvokeMethod,
                    binder: null,
                    target: rulesObj,
                    args: [name]);
                removed++;
            }
            catch { /* ignore single rule */ }
        }

        return removed;
    }

    private static int ClearOwnedFirewallRulesNetsh()
    {
        var names = ListOwnedFirewallRulesNetsh();
        var removed = 0;
        foreach (var name in names)
        {
            var (exit, _, _) = RunCapture(
                "netsh.exe",
                $"advfirewall firewall delete rule name=\"{name.Replace("\"", "'")}\"",
                ProcessTimeoutMs);
            if (exit == 0) removed++;
        }
        return removed;
    }

    private static List<string> ListOwnedFirewallRules()
    {
        var viaCom = ListOwnedFirewallRulesCom();
        if (viaCom is not null) return viaCom;
        return ListOwnedFirewallRulesNetsh();
    }

    private static List<string>? ListOwnedFirewallRulesCom()
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return null;
            var fw = Activator.CreateInstance(type);
            if (fw is null) return null;
            var rulesObj = type.InvokeMember(
                "Rules",
                System.Reflection.BindingFlags.GetProperty,
                binder: null,
                target: fw,
                args: null);
            if (rulesObj is null) return null;

            var list = new List<string>();
            foreach (var rule in (System.Collections.IEnumerable)rulesObj)
            {
                if (rule is null) continue;
                var nameObj = rule.GetType().InvokeMember(
                    "Name",
                    System.Reflection.BindingFlags.GetProperty,
                    binder: null,
                    target: rule,
                    args: null);
                var name = nameObj as string;
                if (!string.IsNullOrEmpty(name) &&
                    name.StartsWith(FirewallRulePrefix, StringComparison.OrdinalIgnoreCase))
                    list.Add(name);
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ListOwnedFirewallRulesNetsh()
    {
        var list = new List<string>();
        try
        {
            var (_, stdout, stderr) = RunCapture(
                "netsh.exe", "advfirewall firewall show rule name=all", ProcessTimeoutMs);
            var text = stdout + "\n" + stderr;
            foreach (Match m in Regex.Matches(text, @"^Rule Name:\s*(.+)\s*$", RegexOptions.Multiline))
            {
                var name = m.Groups[1].Value.Trim();
                if (name.StartsWith(FirewallRulePrefix, StringComparison.OrdinalIgnoreCase))
                    list.Add(name);
            }
        }
        catch
        {
            /* ignore — verification treats uncleared as dirty when hosts dirty */
        }
        return list;
    }

    /// <summary>
    /// User-writable staging dir for teardown scripts. ProgramData\dlp is often
    /// ACL-locked (or holds SYSTEM-owned leftovers) so unelevated WriteAllText fails.
    /// </summary>
    public static string TeardownScriptDir
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "AlphaAITracker-teardown");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// When the unelevated CLI cannot clear hosts / stop the DLP service, relaunch
    /// this same binary elevated (UAC) to run cleanup only. Requires <c>teardown.ok</c>.
    /// </summary>
    public static TeardownCleanupResult TryElevateCleanupViaUac(List<string>? steps = null)
    {
        steps ??= new List<string>();
        if (!OperatingSystem.IsWindows())
            return new TeardownCleanupResult(true, true, true, false, false, "not_windows", steps);

        if (IsElevated())
            return RestoreUsbAndRemoveDlpService();

        var gate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppInfo.DataFolderName, "dlp", "teardown.ok");
        if (!File.Exists(gate))
        {
            steps.Add("elevate skipped: teardown.ok missing");
            return new TeardownCleanupResult(false, false, false, false, false, "no_gate", steps);
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            steps.Add("elevate skipped: ProcessPath missing");
            return new TeardownCleanupResult(false, false, false, false, false, "no_exe", steps);
        }

        steps.Add("requesting UAC elevation for hosts/USB/DLP cleanup");
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--teardown-cleanup-elevated --no-pause",
                UseShellExecute = true,
                Verb = "runas",
            });
            if (p is null)
            {
                steps.Add("elevate Process.Start returned null");
                return new TeardownCleanupResult(false, false, false, false, false, "elevate_null", steps);
            }

            if (!p.WaitForExit(120_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                steps.Add("elevate child timed out after 120s");
            }
            else
            {
                steps.Add($"elevate child exit={p.ExitCode}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user dismissed UAC
            steps.Add("elevate cancelled by user (UAC)");
            return new TeardownCleanupResult(
                ReadUsbStorStart() == UsbStorStartManual,
                !ServiceExists(),
                IsWebDomainBlockCleared(),
                false,
                false,
                "uac_cancelled",
                steps);
        }
        catch (Exception ex)
        {
            steps.Add("elevate error: " + ex.Message);
        }

        var usbOk = ReadUsbStorStart() == UsbStorStartManual;
        var gone = !ServiceExists();
        var hostsOk = IsWebDomainBlockCleared();
        var appLockerOk = !LooksLikeAppLockerExeEnforced();
        var summary = (usbOk, gone, hostsOk, appLockerOk) switch
        {
            (true, true, true, true) => "verified_after_elevate",
            (true, true, true, false) => "elevate_applocker_still_enforced",
            _ => "elevate_incomplete",
        };
        steps.Add(
            $"after elevate usbstor={ReadUsbStorStart()?.ToString() ?? "?"} gone={gone} " +
            $"hostsCleared={hostsOk} appLockerCleared={appLockerOk}");
        return new TeardownCleanupResult(usbOk, gone, hostsOk, true, false, summary, steps);
    }

    private static bool TrySystemCleanupTask(List<string> steps, string svc, string exe)
    {
        try
        {
            // Never write under ProgramData\dlp — unelevated users often get Access Denied
            // (or cannot overwrite SYSTEM-owned leftovers from a prior attempt).
            var dir = TeardownScriptDir;

            var ps1 = Path.Combine(dir, "teardown-clear-hosts.ps1");
            File.WriteAllText(ps1, BuildHostsClearPowerShell(), Encoding.UTF8);

            var appLockerPs1 = Path.Combine(dir, "teardown-clear-applocker.ps1");
            File.WriteAllText(appLockerPs1, BuildAppLockerClearPowerShell(dir), Encoding.UTF8);

            var script = Path.Combine(dir, "teardown-cleanup.cmd");
            // Keep script short and non-interactive; no pauses.
            // Clear hosts/FW + AppLocker first (SYSTEM can run powershell under lockdown),
            // then USB + service stop/delete.
            File.WriteAllText(script,
                "@echo off\r\n" +
                $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" >nul 2>&1\r\n" +
                $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{appLockerPs1}\" >nul 2>&1\r\n" +
                "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\USBSTOR\" /v Start /t REG_DWORD /d 3 /f >nul 2>&1\r\n" +
                "sc config USBSTOR start= demand >nul 2>&1\r\n" +
                $"sc failure \"{svc}\" reset= 0 actions= // >nul 2>&1\r\n" +
                $"sc stop \"{svc}\" >nul 2>&1\r\n" +
                $"sc delete \"{svc}\" >nul 2>&1\r\n" +
                $"taskkill /F /IM \"{exe}\" >nul 2>&1\r\n");

            var task = AppInfo.WindowsServiceMaintTask + "Teardown";
            // Prefer Task Scheduler COM (no schtasks.exe — AppLocker cannot block COM).
            var com = WindowsScmNative.TryRunSystemCmdViaTaskScheduler(task, $"\"{script}\"");
            steps.Add($"tasksched-com ok={com.Ok} {com.Detail}");
            if (com.Ok)
            {
                Thread.Sleep(SystemTaskSettleMs);
                WindowsScmNative.TryDeleteScheduledTask(task);
                return true;
            }

            // Legacy schtasks.exe fallback (blocked under AppLocker lockdown for users).
            var tr = $"cmd.exe /c \\\"{script}\\\"";
            var create = RunLogged(steps, "schtasks-create", "schtasks.exe",
                $"/Create /TN \"{task}\" /TR \"{tr}\" /SC ONCE /ST 00:00 /RU SYSTEM /F",
                ProcessTimeoutMs);
            if (create != 0)
            {
                steps.Add($"schtasks-create failed exit={create} (need elevation for /RU SYSTEM)");
                return false;
            }

            var run = RunLogged(steps, "schtasks-run", "schtasks.exe",
                $"/Run /TN \"{task}\"", ProcessTimeoutMs);
            Thread.Sleep(SystemTaskSettleMs);
            RunLogged(steps, "schtasks-delete", "schtasks.exe",
                $"/Delete /TN \"{task}\" /F", ProcessTimeoutMs);

            steps.Add($"schtasks-run exit={run}");
            return run == 0;
        }
        catch (Exception ex)
        {
            steps.Add("schtasks exception: " + ex.Message);
            return false;
        }
    }

    /// <summary>SYSTEM PowerShell: wipe SrpV2 + Set-AppLockerPolicy NotConfigured.</summary>
    internal static string BuildAppLockerClearPowerShell(string workDir)
    {
        var xmlPath = Path.Combine(workDir, "applocker-clear-system.xml").Replace("'", "''");
        return
            "$ErrorActionPreference='SilentlyContinue'\r\n" +
            "try { Remove-Item -LiteralPath 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\SrpV2' -Recurse -Force } catch {}\r\n" +
            $"$xmlPath = '{xmlPath}'\r\n" +
            "$xml = @'\r\n" +
            EmptyAppLockerPolicyXml + "\r\n" +
            "'@\r\n" +
            "[System.IO.File]::WriteAllText($xmlPath, $xml, [System.Text.Encoding]::Unicode)\r\n" +
            "Set-AppLockerPolicy -XmlPolicy $xmlPath\r\n" +
            "Set-Service -Name AppIDSvc -StartupType Automatic -ErrorAction SilentlyContinue\r\n" +
            "Restart-Service -Name AppIDSvc -Force -ErrorAction SilentlyContinue\r\n";
    }

    /// <summary>PowerShell used by SYSTEM / Inno to strip AlphaAI-DLP hosts + FW rules.</summary>
    internal static string BuildHostsClearPowerShell() =>
        "$ErrorActionPreference='SilentlyContinue'\r\n" +
        "$p=Join-Path $env:SystemRoot 'System32\\drivers\\etc\\hosts'\r\n" +
        "if (Test-Path $p) {\r\n" +
        "  $c=Get-Content -LiteralPath $p -Raw\r\n" +
        "  if ($null -ne $c -and $c -match 'AlphaAI-DLP') {\r\n" +
        "    $lines=$c -replace \"`r`n\",\"`n\" -replace \"`r\",\"`n\" -split \"`n\"\r\n" +
        "    $kept=New-Object System.Collections.Generic.List[string]\r\n" +
        "    $in=$false\r\n" +
        "    foreach ($line in $lines) {\r\n" +
        "      $t=$line.Trim()\r\n" +
        "      if ($t -eq '# AlphaAI-DLP-BEGIN') { $in=$true; continue }\r\n" +
        "      if ($t -eq '# AlphaAI-DLP-END') { $in=$false; continue }\r\n" +
        "      if ($in) { continue }\r\n" +
        "      if ($t -like '*# AlphaAI-DLP*') { continue }\r\n" +
        "      [void]$kept.Add($line)\r\n" +
        "    }\r\n" +
        "    while ($kept.Count -gt 0 -and [string]::IsNullOrWhiteSpace($kept[$kept.Count-1])) { $kept.RemoveAt($kept.Count-1) }\r\n" +
        "    $out=($kept -join \"`r`n\")\r\n" +
        "    if ($out.Length -gt 0) { $out += \"`r`n\" }\r\n" +
        "    [System.IO.File]::WriteAllText($p, $out, (New-Object System.Text.UTF8Encoding $false))\r\n" +
        "  }\r\n" +
        "}\r\n" +
        "$out = netsh advfirewall firewall show rule name=all 2>$null\r\n" +
        "foreach ($m in [regex]::Matches([string]$out, '(?m)^Rule Name:\\s*(.+?)\\s*$')) {\r\n" +
        "  $n=$m.Groups[1].Value.Trim()\r\n" +
        "  if ($n.StartsWith('AlphaAI-DLP-')) { netsh advfirewall firewall delete rule name=\"$n\" | Out-Null }\r\n" +
        "}\r\n" +
        "ipconfig /flushdns | Out-Null\r\n" +
        "# Remove local HTTPS block-page CA from Root store\r\n" +
        "$store=New-Object System.Security.Cryptography.X509Certificates.X509Store('Root','LocalMachine')\r\n" +
        "$store.Open('ReadWrite')\r\n" +
        "$store.Certificates | Where-Object { $_.Subject -like '*Alpha AI Tracker DLP Block CA*' } | ForEach-Object { $store.Remove($_); }\r\n" +
        "$store.Close()\r\n" +
        "Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $env:ProgramData 'AlphaAITracker\\dlp\\block-page-ca.pfx')\r\n";

    private static int RunLogged(List<string> steps, string op, string fileName, string args, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (exit, stdout, stderr) = RunCapture(fileName, args, timeoutMs);
            var detail = (stdout + " " + stderr).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (detail.Length > 160) detail = detail[..160] + "…";
            steps.Add($"{op} exit={exit} {sw.ElapsedMilliseconds}ms{(detail.Length > 0 ? " " + detail : "")}");
            return exit;
        }
        catch (Exception ex)
        {
            steps.Add($"{op} error={ex.Message} {sw.ElapsedMilliseconds}ms");
            return -1;
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCapture(
        string fileName, string args, int timeoutMs)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        p.Start();
        // Avoid schtasks hanging on a password prompt when stdin is redirected.
        try { p.StandardInput.Close(); } catch { /* ignore */ }

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { p.WaitForExit(3_000); } catch { /* ignore */ }
            return (-1, "", $"timeout_killed_after_{timeoutMs}ms");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return (p.ExitCode, stdout ?? "", stderr ?? "");
    }
}
