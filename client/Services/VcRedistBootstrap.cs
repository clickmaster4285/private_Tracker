using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace client.Services;

/// <summary>
/// Fail-closed Windows gate: before the tracker / soft-lock / live-stream load any
/// native CRT-dependent DLL, require the system Microsoft Visual C++ 2015–2022
/// redistributable (x64). If missing, stop startup and force-install from
/// <c>{app}\redist\vc_redist.x64.exe</c> (+ x86 when present).
/// </summary>
public static class VcRedistBootstrap
{
    private static int _done;

    /// <summary>
    /// Returns true when the process may continue. Returns false when the caller
    /// must exit (install failed / UAC denied / redistributable package missing).
    /// </summary>
    public static bool EnsureInstalledOrExit(bool allowUi)
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (Interlocked.Exchange(ref _done, 1) != 0) return true;

        if (IsVc14Installed(x64: true))
            return true;

        var baseDir = AppContext.BaseDirectory;
        var redistDir = Path.Combine(baseDir, "redist");
        var x64 = Path.Combine(redistDir, "vc_redist.x64.exe");
        var x86 = Path.Combine(redistDir, "vc_redist.x86.exe");

        if (!File.Exists(x64))
        {
            Fail(allowUi,
                "Microsoft Visual C++ 2015–2022 (x64) is not installed, and " +
                "vc_redist.x64.exe is missing next to the tracker (redist folder).\n\n" +
                "Reinstall Alpha AI Tracker Setup 2.4.6+.");
            return false;
        }

        if (allowUi)
        {
            MessageBoxW(IntPtr.Zero,
                "Microsoft Visual C++ is required and was not found.\n\n" +
                "Setup will now install Visual C++ 2015–2022 (x64" +
                (File.Exists(x86) ? " + x86" : "") + ").\n" +
                "Approve the UAC prompt if Windows asks.",
                "Alpha AI Tracker — Installing Visual C++",
                0x00000030 /* MB_ICONWARNING */);
        }
        else
        {
            Console.Error.WriteLine("[client] VC++ 2015-2022 x64 missing — force-installing from redist\\…");
        }

        var log = new System.Text.StringBuilder();
        log.AppendLine($"VC++ force-install {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"baseDir={baseDir}");

        var okX64 = RunRedist(x64, log);
        if (File.Exists(x86))
            RunRedist(x86, log);

        try
        {
            Directory.CreateDirectory(redistDir);
            File.WriteAllText(Path.Combine(redistDir, "vc_redist_runtime_install.log"), log.ToString());
        }
        catch { /* best-effort */ }

        // Re-check after install (1638 = already present also counts as success for RunRedist).
        if (IsVc14Installed(x64: true))
            return true;

        Fail(allowUi,
            "Visual C++ install finished but the runtime is still not registered.\n\n" +
            "Open Control Panel → Programs and look for Microsoft Visual C++ 2015–2022.\n" +
            "See redist\\vc_redist_runtime_install.log next to client.exe.\n\n" +
            "Install vc_redist.x64.exe manually from the redist folder, then relaunch.");
        return false;
    }

    public static bool IsVc14Installed(bool x64)
    {
        // Official detection: HKLM\…\VisualStudio\14.0\VC\Runtimes\{x64|x86}\Installed = 1
        foreach (var hive in new[] { RegistryHive.LocalMachine })
        {
            foreach (var view in x64
                         ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                         : new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    var path = $@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{(x64 ? "x64" : "x86")}";
                    using var key = baseKey.OpenSubKey(path);
                    if (key is null) continue;
                    var installed = key.GetValue("Installed");
                    if (installed is int i && i == 1) return true;
                    if (installed is long l && l == 1) return true;
                }
                catch { /* try next */ }
            }
        }
        return false;
    }

    private static bool RunRedist(string exePath, System.Text.StringBuilder log)
    {
        try
        {
            var elevated = IsProcessElevated();
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "/install /quiet /norestart",
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            // Force UAC when the tracker is a standard user — quiet MSI needs admin.
            if (!elevated)
                psi.Verb = "runas";

            using var p = Process.Start(psi);
            if (p is null)
            {
                log.AppendLine($"{Path.GetFileName(exePath)}: Process.Start returned null");
                return false;
            }
            p.WaitForExit(10 * 60 * 1000);
            if (!p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                log.AppendLine($"{Path.GetFileName(exePath)}: TIMEOUT");
                return false;
            }

            // 0 = success, 1638 = another version already installed, 3010 = success reboot required
            var code = p.ExitCode;
            log.AppendLine($"{Path.GetFileName(exePath)}: exit={code} elevated={elevated}");
            return code is 0 or 1638 or 3010;
        }
        catch (Exception ex)
        {
            // Win32Exception 1223 = UAC cancelled
            log.AppendLine($"{Path.GetFileName(exePath)}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void Fail(bool allowUi, string message)
    {
        Console.Error.WriteLine("[client] " + message.Replace('\n', ' '));
        if (allowUi)
        {
            MessageBoxW(IntPtr.Zero, message, "Alpha AI Tracker — Visual C++ required",
                0x00000010 /* MB_ICONERROR */);
        }
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
