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
/// sink + firewall rules, stop/delete the DLP service. Prefer SYSTEM via schtasks
/// (same pattern as Inno). Every external process is hard-killed on timeout so the
/// CLI never hangs indefinitely. Results are verified, not assumed.
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

        // Clear hosts/FW BEFORE killing the service — the service's own Clear() is
        // fire-and-forget and often loses the race to sc stop / taskkill.
        TryClearWebDomainBlockDirect(steps);

        // Clear SCM failure actions so stop/kill is not undone by restart/5s.
        RunLogged(steps, "sc-failure-clear", "sc.exe",
            $"failure {svc} reset= 0 actions= //", ProcessTimeoutMs);

        // Direct attempts (work after ApplyTeardownGate opened the DACL, if elevated).
        RunLogged(steps, "sc-stop-direct", "sc.exe", $"stop \"{svc}\"", ProcessTimeoutMs);
        RunLogged(steps, "sc-delete-direct", "sc.exe", $"delete \"{svc}\"", ProcessTimeoutMs);
        RunLogged(steps, "taskkill-direct", "taskkill.exe", $"/F /IM \"{exe}\"", ProcessTimeoutMs);
        TryRestoreUsbStorDirect(steps);
        TryClearWebDomainBlockDirect(steps);

        var afterDirectGone = !ServiceExists(svc);
        var afterDirectUsb = ReadUsbStorStart() == UsbStorStartManual;
        var afterDirectHosts = IsWebDomainBlockCleared();
        if (afterDirectGone && afterDirectUsb && afterDirectHosts)
        {
            steps.Add("verified after direct cleanup");
            return new TeardownCleanupResult(true, true, true, false, elevated,
                "verified_direct", steps);
        }

        // SYSTEM one-shot (requires ability to create schtasks — usually admin).
        var systemOk = TrySystemCleanupTask(steps, svc, exe);
        Thread.Sleep(SystemTaskSettleMs);

        // Re-try direct after SYSTEM task may have opened/stopped the service.
        RunLogged(steps, "sc-stop-retry", "sc.exe", $"stop \"{svc}\"", ProcessTimeoutMs);
        RunLogged(steps, "sc-delete-retry", "sc.exe", $"delete \"{svc}\"", ProcessTimeoutMs);
        RunLogged(steps, "taskkill-retry", "taskkill.exe", $"/F /IM \"{exe}\"", ProcessTimeoutMs);
        TryRestoreUsbStorDirect(steps);
        TryClearWebDomainBlockDirect(steps);

        var usbOk = ReadUsbStorStart() == UsbStorStartManual;
        var gone = !ServiceExists(svc);
        var hostsOk = IsWebDomainBlockCleared();
        var afterState = QueryServiceState(svc);
        steps.Add(
            $"after service={afterState} usbstor={ReadUsbStorStart()?.ToString() ?? "?"} " +
            $"hostsCleared={hostsOk} systemTask={systemOk}");

        var summary = (usbOk, gone, hostsOk) switch
        {
            (true, true, true) => "verified",
            (true, true, false) => "service_gone_hosts_still_dirty",
            (false, true, _) => "service_gone_usbstor_not_manual",
            (true, false, _) => "usbstor_ok_service_still_registered",
            _ => "cleanup_incomplete",
        };

        return new TeardownCleanupResult(usbOk, gone, hostsOk, systemOk, elevated, summary, steps);
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
            var (exit, stdout, stderr) = RunCapture("sc.exe", $"query \"{serviceName}\"", ProcessTimeoutMs);
            var text = (stdout + "\n" + stderr);
            if (exit == 1060 || text.Contains("1060", StringComparison.Ordinal) ||
                text.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return "absent";
            if (text.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return "RUNNING";
            if (text.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return "STOPPED";
            if (text.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase)) return "STOP_PENDING";
            if (exit != 0) return $"query_failed:{exit}";
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

    private static void TryRestoreUsbStorDirect(List<string> steps)
    {
        RunLogged(steps, "reg-usbstor", "reg.exe",
            "add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\USBSTOR\" /v Start /t REG_DWORD /d 3 /f",
            ProcessTimeoutMs);
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
            var removed = ClearOwnedFirewallRules();
            steps.Add($"fw-clear removed={removed}");
        }
        catch (Exception ex)
        {
            steps.Add("fw-clear error=" + ex.Message);
        }

        RunLogged(steps, "flushdns", "ipconfig.exe", "/flushdns", ProcessTimeoutMs);
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

    private static int ClearOwnedFirewallRules()
    {
        var names = ListOwnedFirewallRules();
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
            /* ignore — verification treats uncleared as dirty */
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
        var summary = (usbOk, gone, hostsOk) switch
        {
            (true, true, true) => "verified_after_elevate",
            _ => "elevate_incomplete",
        };
        steps.Add($"after elevate usbstor={ReadUsbStorStart()?.ToString() ?? "?"} gone={gone} hostsCleared={hostsOk}");
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

            var script = Path.Combine(dir, "teardown-cleanup.cmd");
            // Keep script short and non-interactive; no pauses.
            // Clear hosts/FW first, then USB + service stop/delete.
            File.WriteAllText(script,
                "@echo off\r\n" +
                $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" >nul 2>&1\r\n" +
                "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\USBSTOR\" /v Start /t REG_DWORD /d 3 /f >nul 2>&1\r\n" +
                "sc config USBSTOR start= demand >nul 2>&1\r\n" +
                $"sc failure \"{svc}\" reset= 0 actions= // >nul 2>&1\r\n" +
                $"sc stop \"{svc}\" >nul 2>&1\r\n" +
                $"sc delete \"{svc}\" >nul 2>&1\r\n" +
                $"taskkill /F /IM \"{exe}\" >nul 2>&1\r\n");

            var task = AppInfo.WindowsServiceMaintTask + "Teardown";
            // Match Inno: single-quoted /TR payload that runs cmd /c <script>.
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
