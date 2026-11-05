using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Registers and unregisters the application for auto-start on system boot.
/// Platform-specific implementations:
/// - Windows: Registry HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// - Linux: ~/.config/autostart/ .desktop file
/// - macOS: ~/Library/LaunchAgents/ plist file
/// </summary>
public class AutoStartService
{
    private readonly string _executablePath;
    private readonly ILogger<AutoStartService> _logger;

    public AutoStartService(ILogger<AutoStartService> logger)
    {
        _executablePath = Environment.ProcessPath
                          ?? Environment.GetCommandLineArgs()[0];
        _logger = logger;
    }

    /// <summary>
    /// Register auto-start. Returns true if successful.
    /// </summary>
    public bool EnableAutoStart()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return EnableWindowsAutoStart();
            else if (OperatingSystem.IsLinux())
                return EnableLinuxAutoStart();
            else if (OperatingSystem.IsMacOS())
                return EnableMacOSAutoStart();

            _logger.LogWarning("Auto-start not supported on this platform");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable auto-start");
            return false;
        }
    }

    /// <summary>
    /// Register auto-start with forced re-registration.
    /// If already registered, re-writes it to prevent removal.
    /// Also schedules a startup task (Windows) or systemd service (Linux)
    /// that auto-restarts if the Run key / .desktop file is deleted.
    /// </summary>
    public bool EnableAutoStartForced()
    {
        var result = EnableAutoStart();

        // Also install platform-specific persistence layer
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Create a scheduled task that runs on startup as a backup.
                // --background = fully headless at boot (services only, NO GUI window
                // and no tray). The GUI appears only when the user launches the app
                // manually — the second launch signals this running instance to show it.
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = $"/Create /SC ONSTART /TN \"AlphaAITracker-Startup\" /TR \"cmd.exe /c start \"\" \\\"{_executablePath}\\\" --background\" /F /RL HIGHEST /DELAY 0000:10",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
                
                _logger.LogInformation("Windows forced auto-start: Run key + Scheduled Task");
            }
            else if (OperatingSystem.IsLinux())
            {
                // The .desktop file is already created by EnableAutoStart
                // Also verify systemd user service is installed
                var psi = new ProcessStartInfo
                {
                    FileName = "systemctl",
                    Arguments = $"--user enable {LinuxUserSystemdUnit.ServiceFileName}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
                
                _logger.LogInformation("Linux forced auto-start: .desktop + systemd service");
            }
            else if (OperatingSystem.IsMacOS())
            {
                _logger.LogInformation("macOS forced auto-start: launchd plist");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Forced auto-start extra layer failed");
        }

        return result;
    }

    /// <summary>
    /// Ensures the platform's "background guard" is actually configured — the thing the
    /// permission wizard's step 2 re-evaluates (MainViewModel.IsBackgroundGuardConfigured).
    ///
    /// On Linux that gate checks for the systemd USER unit
    /// <c>~/.config/systemd/user/alpha-ai-tracker.service</c>, but until now nothing in
    /// the wizard created that file: <see cref="EnableAutoStartForced"/> only wrote the
    /// <c>.desktop</c> autostart entry and ran <c>systemctl --user enable</c> against a
    /// unit that did not exist yet — so the step reported "Background guard enabled
    /// successfully." and then re-evaluated to ITSELF forever, with no way to reach step 3
    /// (stuck on "SETUP STEP 2 OF 4" on Linux, 2026-10-19).
    ///
    /// The unit is written, daemon-reloaded and enabled, but deliberately NOT started:
    /// the wizard runs inside the already-live process, and starting the unit here would
    /// spawn a second instance that fights the single-instance mutex in a Restart=always
    /// loop. Windows (Run key) and macOS (launchd plist) are configured by
    /// <see cref="EnableAutoStart"/>, so this only verifies them.
    ///
    /// Returns true when the guard is configured afterwards; false only when Linux could
    /// not write/enable the unit (the caller surfaces that as a failed step).
    /// </summary>
    public async Task<bool> EnsureBackgroundGuardAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
                var unitContent = LinuxUserSystemdUnit.BuildUnitContent(exePath);
                var servicePath = LinuxUserSystemdUnit.ServicePath;
                var systemdDir = Path.GetDirectoryName(servicePath)!;
                Directory.CreateDirectory(systemdDir);
                await File.WriteAllTextAsync(servicePath, unitContent, ct);

                // daemon-reload so the user manager sees the freshly written unit;
                // enable so it comes back at the next login. Both are best-effort — the
                // wizard's gate (and this return) only needs the unit file to exist and
                // to be enabled, and a headless session without a user bus still ends up
                // with a working guard on the next boot with a session.
                await RunSystemctlAsync("--user daemon-reload", ct);
                var enabled = await RunSystemctlAsync(
                    $"--user enable {LinuxUserSystemdUnit.ServiceFileName}", ct);
                await LinuxUserSystemdUnit.TryEnableLingerAsync(ct);

                _logger.LogInformation(
                    "Linux background guard installed at {Path} (RestartSec={Sec}, enable exit {Code})",
                    servicePath, LinuxUserSystemdUnit.RestartSec, enabled);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to install the systemd background guard unit");
                return false;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            // The launchd KeepAlive plist IS the macOS background guard (EnableAutoStart writes it).
            return File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "LaunchAgents", "com.alphaai.tracker.plist"));
        }

        // Windows: the Run key is both what EnableAutoStartForced writes and what the
        // wizard's gate reads.
        return IsAutoStartEnabled();
    }

    /// <summary>Runs <c>systemctl</c> with the given arguments and returns its exit code (-1 if it never started).</summary>
    private static async Task<int> RunSystemctlAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "systemctl",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return -1;
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    /// <summary>
    /// Remove auto-start registration.
    /// </summary>
    public bool DisableAutoStart()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return DisableWindowsAutoStart();
            else if (OperatingSystem.IsLinux())
                return DisableLinuxAutoStart();
            else if (OperatingSystem.IsMacOS())
                return DisableMacOSAutoStart();

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable auto-start");
            return false;
        }
    }

    /// <summary>
    /// Migrate legacy auto-start entries that still launch with --minimized (the old
    /// hidden-to-tray mode) to --background (fully headless at boot — the GUI only
    /// opens when the user manually launches the app). Returns true when a stale
    /// entry was refreshed. Called by the background guard every minute, so machines
    /// that already registered --minimized self-heal on the next boot.
    /// </summary>
    public bool EnsureBackgroundAutoStartFlag()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run");
                var value = key?.GetValue("AlphaAITracker") as string;
                if (value != null && value.Contains("--minimized", StringComparison.OrdinalIgnoreCase))
                {
                    EnableWindowsAutoStart(); // rewrites the value with --background
                    _logger.LogInformation("Windows auto-start migrated: --minimized → --background");
                    return true;
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                var desktopPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "autostart", "alpha-ai-tracker.desktop");
                if (File.Exists(desktopPath))
                {
                    var content = File.ReadAllText(desktopPath);
                    var wantsUnitStart = content.Contains(
                        $"systemctl --user start {LinuxUserSystemdUnit.ServiceFileName}",
                        StringComparison.Ordinal);
                    if (content.Contains("--minimized", StringComparison.OrdinalIgnoreCase)
                        || !wantsUnitStart)
                    {
                        EnableLinuxAutoStart(); // rewrites Exec= to systemctl --user start
                        _logger.LogInformation(
                            "Linux auto-start migrated to systemd unit start ({Unit})",
                            LinuxUserSystemdUnit.ServiceFileName);
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto-start flag migration check failed");
        }
        return false;
    }

    /// <summary>
    /// Check if auto-start is currently enabled.
    /// </summary>
    public bool IsAutoStartEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue("AlphaAITracker") != null;
            }
            else if (OperatingSystem.IsLinux())
            {
                var desktopPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "autostart", "alpha-ai-tracker.desktop");
                return File.Exists(desktopPath);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var plistPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "LaunchAgents", "com.alphaai.tracker.plist");
                return File.Exists(plistPath);
            }
        }
        catch { }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private bool EnableWindowsAutoStart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key == null) return false;

            // --background: at boot/power-on ONLY the background services run — no GUI
            // window, no tray. The GUI opens only when the user manually launches the app
            // (a second launch signals this instance to create + show its window).
            key.SetValue("AlphaAITracker",
                $"\"{_executablePath}\" --background",
                Microsoft.Win32.RegistryValueKind.String);
            _logger.LogInformation("Windows auto-start enabled");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning("Need admin rights to set auto-start on Windows");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private bool DisableWindowsAutoStart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key?.GetValue("AlphaAITracker") != null)
            {
                key.DeleteValue("AlphaAITracker");
            }
            _logger.LogInformation("Windows auto-start disabled");
            return true;
        }
        catch { return false; }
    }

    private bool EnableLinuxAutoStart()
    {
        var autostartDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart");
        Directory.CreateDirectory(autostartDir);

        // Start the user unit (not a bare Exec=client --background). Only the
        // unit's MainPID gets Restart=always after Resources Kill / kill -9.
        // Handoff in Program.cs still heals older .desktop files that launch
        // the binary directly.
        var unit = LinuxUserSystemdUnit.ServiceFileName;
        var desktopContent = $"""
            [Desktop Entry]
            Type=Application
            Name=Alpha AI Tracker
            Exec=systemctl --user start {unit}
            Terminal=false
            X-GNOME-Autostart-enabled=true
            X-GNOME-Autostart-Delay=5
            X-KDE-autostart-after=panel
            Categories=Utility;
            Comment=Employee Monitoring & Activity Tracker
            """;

        var desktopPath = Path.Combine(autostartDir, "alpha-ai-tracker.desktop");
        File.WriteAllText(desktopPath, desktopContent, Encoding.UTF8);
        _logger.LogInformation("Linux auto-start enabled at {Path} (systemd unit start)", desktopPath);
        return true;
    }

    private bool DisableLinuxAutoStart()
    {
        var desktopPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart", $"{client.Core.AppInfo.PackageName}.desktop");
        if (File.Exists(desktopPath))
        {
            File.Delete(desktopPath);
            _logger.LogInformation("Linux auto-start disabled (.desktop)");
        }

        // Also stop/disable the Restart=always user unit so protected-uninstall
        // is not fought by systemd reopen (L2/L3).
        try
        {
            var unit = LinuxUserSystemdUnit.ServiceFileName;
            var disablePsi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = $"--user disable --now {unit}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = Process.Start(disablePsi))
                proc?.WaitForExit(8_000);

            if (File.Exists(LinuxUserSystemdUnit.ServicePath))
                File.Delete(LinuxUserSystemdUnit.ServicePath);

            var reloadPsi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = "--user daemon-reload",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = Process.Start(reloadPsi))
                proc?.WaitForExit(8_000);

            _logger.LogInformation("Linux systemd user unit disabled and removed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to disable Linux systemd user unit");
        }

        return true;
    }

    private bool EnableMacOSAutoStart()
    {
        var launchAgentsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");
        Directory.CreateDirectory(launchAgentsDir);

        var plistContent = $""""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple Computer//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>com.alphaai.tracker</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{_executablePath}</string>
                    <string>--background</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>ProcessType</key>
                <string>Background</string>
                <key>ThrottleInterval</key>
                <integer>5</integer>
            </dict>
            </plist>
            """";

        var plistPath = Path.Combine(launchAgentsDir, "com.alphaai.tracker.plist");
        File.WriteAllText(plistPath, plistContent, Encoding.UTF8);

        // Load with launchctl
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = $"load {plistPath}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "launchctl load failed, auto-start is registered but not active until next login");
        }

        _logger.LogInformation("macOS auto-start enabled at {Path}", plistPath);
        return true;
    }

    private bool DisableMacOSAutoStart()
    {
        var plistPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", "com.alphaai.tracker.plist");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = $"unload {plistPath}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch { }

        if (File.Exists(plistPath))
            File.Delete(plistPath);

        _logger.LogInformation("macOS auto-start disabled");
        return true;
    }
}
