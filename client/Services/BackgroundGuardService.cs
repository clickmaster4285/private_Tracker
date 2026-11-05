using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Background guard service that provides resilience for already-configured permissions.
/// 
/// **Design principle**: This service NEVER creates auto-start or systemd services on its own.
/// Initial setup is done exclusively through the permission step UI (MainViewModel).
/// This service only WATCHDOGS — if something that was previously configured gets removed,
/// it re-installs it immediately.
/// 
/// Flow:
/// 1. User completes permission steps → auto-start + systemd are created
/// 2. BackgroundGuardService detects these exist → starts protecting them
/// 3. If user/process removes them → guard re-installs within 60 seconds
/// </summary>
    public class BackgroundGuardService : BackgroundService, IDisposable
{
    private readonly ILogger<BackgroundGuardService> _logger;
    private readonly AutoStartService _autoStart;

    /// <summary>
    /// Set during protected-uninstall / account removal so the watchdog does not
    /// reinstall the Run key or systemd unit while teardown is in progress.
    /// </summary>
    public static volatile bool SuppressWatchdog;

    // Track what's currently installed so we know what to watchdog
    private bool _autoStartWasEnabled;
    private bool _systemdWasInstalled;
    private string? _systemdServiceName;

    // Windows: self-restart capability
    private static readonly string RestartScriptPath =
        Path.Combine(
            Path.GetTempPath(),
            "AlphaAITracker_Restart.cmd");

    public BackgroundGuardService(
        ILogger<BackgroundGuardService> logger,
        AutoStartService autoStart)
    {
        _logger = logger;
        _autoStart = autoStart;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("BackgroundGuardService parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        _logger.LogInformation("BackgroundGuardService starting...");

        // ─── Initial State Detection ───
        // Detect what's already configured (may have been set up by permission UI
        // in a previous session). We do NOT install anything here — only observe.
        _autoStartWasEnabled = _autoStart.IsAutoStartEnabled();
        _systemdWasInstalled = DetectSystemdService();
        _systemdServiceName = LinuxUserSystemdUnit.ServiceFileName;

        if (_autoStartWasEnabled)
            _logger.LogInformation("Auto-start already configured — will watchdog");
        if (_systemdWasInstalled)
        {
            _logger.LogInformation("Systemd service already installed — will watchdog");
            await RemoveStaleDisplayOverridesAsync(stoppingToken);
        }

        // ─── Watchdog Loop ───
        // Only protects things that were previously set up. Never creates new ones.
        var healthCheckInterval = TimeSpan.FromSeconds(60);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(healthCheckInterval, stoppingToken);

                if (SuppressWatchdog)
                {
                    _logger.LogInformation("Guard watchdog suppressed (uninstall/removal in progress)");
                    continue;
                }

                _logger.LogTrace("Guard watchdog heartbeat OK");

                // Watchdog: auto-start — detect if present, protect if previously seen
                if (_autoStart.IsAutoStartEnabled())
                {
                    _autoStartWasEnabled = true; // Seen at least once → start protecting

                    // 2026-08-15: boot must be FULLY headless (no GUI at reboot/power-on).
                    // Legacy entries launch with --minimized (hidden-to-tray, UI still
                    // created) — migrate them to --background so only background jobs run
                    // at boot and the GUI appears solely on a manual user launch.
                    if (_autoStart.EnsureBackgroundAutoStartFlag())
                        _logger.LogInformation("Auto-start entry migrated to --background (headless boot)");
                }
                else if (_autoStartWasEnabled)
                {
                    // Was previously seen as enabled, now missing — re-install
                    _logger.LogWarning("Auto-start was removed! Re-installing immediately...");
                    _autoStart.EnableAutoStartForced();
                }

                // Watchdog: systemd service — only if it was previously installed
                // 🟡 FIX 2026-07-28: Do NOT check systemctl is-active or try to start the service.
                // When running via `dotnet run` in development, the systemd service unit file
                // exists (from a prior install) but the service is NOT managed by systemd —
                // the current process is running directly. Starting it via systemctl would
                // launch a parallel instance that fails on the mutex and enters Restart=always
                // loop, filling the journal with crash logs.
                // Instead, only ensure the unit file exists so it's available on next boot/when
                // the user runs in --background mode. The user manually starts via systemctl.
                if (_systemdWasInstalled && _systemdServiceName != null)
                {
                    var svcPath = LinuxUserSystemdUnit.ServicePath;
                    var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
                    string? existing = null;
                    try { if (File.Exists(svcPath)) existing = await File.ReadAllTextAsync(svcPath, stoppingToken); }
                    catch { /* rewrite */ }

                    if (!File.Exists(svcPath) ||
                        LinuxUserSystemdUnit.NeedsRewrite(existing, exePath))
                    {
                        _logger.LogWarning(
                            "Systemd unit missing or stale (RestartSec/ExecStart) — reinstalling...");
                        await InstallSystemdServiceAsync(stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Guard watchdog error");
            }
        }

        _logger.LogInformation("BackgroundGuardService stopped");
    }

    // ─── State Detection ───

    private static bool DetectSystemdService()
    {
        try
        {
            return File.Exists(LinuxUserSystemdUnit.ServicePath);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSystemdActive(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = $"--user is-active {serviceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            return output.Trim().Equals("active", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ─── Linux Systemd Integration ───

    /// <summary>
    /// Older units forced DISPLAY=:0 and ~/.Xauthority. GNOME Wayland rotates
    /// Xwayland authorization under /run/user/&lt;uid&gt;, so the background
    /// process could track normally but could not create its lazy GUI. Remove
    /// only those legacy overrides; the user systemd manager already carries
    /// the live desktop environment.
    /// </summary>
    private async Task RemoveStaleDisplayOverridesAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            var servicePath = LinuxUserSystemdUnit.ServicePath;
            if (!File.Exists(servicePath)) return;

            var lines = await File.ReadAllLinesAsync(servicePath, ct);
            var repaired = lines
                .Where(line =>
                {
                    var trimmed = line.Trim();
                    return !trimmed.StartsWith("Environment=DISPLAY=", StringComparison.Ordinal) &&
                           !trimmed.StartsWith("Environment=XAUTHORITY=", StringComparison.Ordinal);
                })
                .ToArray();
            if (repaired.Length == lines.Length) return;

            await File.WriteAllLinesAsync(servicePath, repaired, ct);
            await RunSystemctlAsync("--user daemon-reload", ct);
            _logger.LogInformation(
                "Removed stale DISPLAY/XAUTHORITY overrides from the systemd unit");

            // The current service process inherited the stale override before
            // repairing the file. Restart only when we are actually running
            // under systemd (never from dotnet run), and do it non-blocking so
            // systemctl cannot wait on the process that invoked it.
            if (!string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("INVOCATION_ID")))
            {
                await RunSystemctlAsync(
                    $"--user restart --no-block {LinuxUserSystemdUnit.ServiceFileName}",
                    ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to repair systemd display environment");
        }
    }

    private async Task InstallSystemdServiceAsync(CancellationToken ct)
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            var serviceContent = LinuxUserSystemdUnit.BuildUnitContent(exePath);
            var servicePath = LinuxUserSystemdUnit.ServicePath;
            var systemdDir = Path.GetDirectoryName(servicePath)!;
            Directory.CreateDirectory(systemdDir);
            await File.WriteAllTextAsync(servicePath, serviceContent, ct);
            await LinuxUserSystemdUnit.TryEnableLingerAsync(ct);

            // Enable the service
            var enablePsi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = $"--user enable {servicePath}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var enableProc = Process.Start(enablePsi))
            {
                if (enableProc != null)
                {
                    await enableProc.WaitForExitAsync(ct);
                    if (enableProc.ExitCode == 0)
                    {
                        _systemdWasInstalled = true;
                        _logger.LogInformation(
                            "Systemd user service reinstalled (RestartSec={Sec})",
                            LinuxUserSystemdUnit.RestartSec);
                    }
                    else
                    {
                        var err = await enableProc.StandardError.ReadToEndAsync(ct);
                        _logger.LogWarning("Systemd enable failed: {Error}", err.Trim());
                    }
                }
            }

            // daemon-reload so RestartSec / ExecStart edits take effect next restart.
            try
            {
                var reloadPsi = new ProcessStartInfo
                {
                    FileName = "systemctl",
                    Arguments = "--user daemon-reload",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var reloadProc = Process.Start(reloadPsi);
                if (reloadProc != null)
                    await reloadProc.WaitForExitAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "systemctl --user daemon-reload failed");
            }

            // Start the service
            var startPsi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = $"--user start {LinuxUserSystemdUnit.ServiceFileName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var startProc = Process.Start(startPsi))
            {
                if (startProc != null)
                {
                    await startProc.WaitForExitAsync(ct);
                    if (startProc.ExitCode == 0)
                    {
                        _logger.LogInformation("Systemd user service started");
                    }
                    else
                    {
                        var err = await startProc.StandardError.ReadToEndAsync(ct);
                        _logger.LogWarning("Systemd start warning: {Error}", err.Trim());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install systemd user service");
        }
    }

    private static async Task RunSystemctlAsync(string arguments, CancellationToken ct)
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
        if (proc == null) return;
        await proc.WaitForExitAsync(ct);
    }

    #region Windows Persistence

    private void InstallWindowsRestartScript()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            var script = $"""
                @echo off
                :loop
                tasklist /FI "IMAGENAME eq {Path.GetFileName(exePath)}" 2>NUL | find /I "{Path.GetFileName(exePath)}" >NUL
                if errorlevel 1 (
                    start "" "{exePath}" --background
                )
                timeout /t 30 /nobreak >NUL
                goto loop
                """;
            File.WriteAllText(RestartScriptPath, script);
            _logger.LogInformation("Windows restart script at {Path}", RestartScriptPath);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start /MIN \"\" \"{RestartScriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install Windows restart script");
        }
    }

    private void TrySetCriticalProcess()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = $"/Create /SC ONLOGON /TN \"AlphaAITracker\" /TR \"\\\"{Environment.ProcessPath}\\\" --background\" /F /RL HIGHEST /DELAY 0000:05",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(10000);
                if (proc.ExitCode == 0)
                    _logger.LogInformation("Windows Scheduled Task installed");
                else
                    _logger.LogWarning("Scheduled Task failed (exit {Code})", proc.ExitCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install Windows Scheduled Task");
        }
    }

    #endregion

    public override void Dispose()
    {
        base.Dispose();
    }
}
