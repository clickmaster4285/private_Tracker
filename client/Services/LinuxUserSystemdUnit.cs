using System.Diagnostics;
using client.Core;

namespace client.Services;

/// <summary>
/// Linux user-systemd unit for crash reopen (~8s, matches Windows
/// <c>ALPHA_DLP_WATCHDOG_POLL_SEC</c>). Cooperative — not SCM ACL-locked.
/// </summary>
public static class LinuxUserSystemdUnit
{
    /// <summary>Restart delay after crash/exit. Aligns with Windows ~8s watchdog poll.</summary>
    public const int RestartSec = 8;

    public static string ServiceFileName => $"{AppInfo.PackageName}.service";

    public static string ServicePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user", ServiceFileName);

    public static string BuildUnitContent(string exePath) => $"""
        [Unit]
        Description={AppInfo.DisplayName} - Employee Monitoring Service
        StartLimitBurst=0
        StartLimitIntervalSec=0

        [Service]
        Type=simple
        ExecStart={exePath} --background
        Restart=always
        RestartSec={RestartSec}

        [Install]
        WantedBy=default.target
        """;

    /// <summary>
    /// True when the unit is missing or its RestartSec / ExecStart / StartLimit
    /// placement drift from the current product template (guard rewrites within 60s).
    /// </summary>
    public static bool NeedsRewrite(string? existingContent, string exePath)
    {
        if (string.IsNullOrWhiteSpace(existingContent))
            return true;
        if (!existingContent.Contains($"RestartSec={RestartSec}", StringComparison.Ordinal))
            return true;
        if (!existingContent.Contains("Restart=always", StringComparison.Ordinal))
            return true;
        // StartLimit* must live under [Unit] (systemd ignores them under [Service]).
        if (!UnitSectionHasStartLimit(existingContent))
            return true;
        // ExecStart may be quoted-ish; require --background and the live binary path.
        if (!existingContent.Contains("--background", StringComparison.Ordinal))
            return true;
        if (!string.IsNullOrWhiteSpace(exePath) &&
            !existingContent.Contains(exePath, StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>
    /// True when this process's cgroup is the user unit (systemd-supervised).
    /// </summary>
    public static bool IsRunningUnderOwnUnit()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            var marker = "/" + ServiceFileName;
            var cgroupPath = "/proc/self/cgroup";
            if (!File.Exists(cgroupPath))
                return false;

            foreach (var line in File.ReadLines(cgroupPath))
            {
                // cgroup v2: "0::/user.slice/.../alpha-ai-tracker.service"
                // cgroup v1: "name=systemd:/user.slice/.../alpha-ai-tracker.service"
                var slash = line.IndexOf(':');
                if (slash < 0) continue;
                var pathStart = line.IndexOf(':', slash + 1);
                if (pathStart < 0) continue;
                var path = line[(pathStart + 1)..];
                if (path.Contains(marker, StringComparison.Ordinal))
                    return true;
            }
        }
        catch
        {
            /* treat as unsupervised */
        }

        return false;
    }

    /// <summary>
    /// When a <c>--background</c> launch is outside the user unit, write/enable the
    /// unit, <c>systemctl --user start</c> it, and return true so the caller exits.
    /// Systemd then owns MainPID and <c>Restart=always</c> survives Resources Kill.
    /// Skip during remove-account / protected-uninstall (caller must gate those).
    /// </summary>
    /// <returns>True when this process should exit immediately (handoff started).</returns>
    public static bool TryHandoffIfUnsupervised()
    {
        if (!OperatingSystem.IsLinux())
            return false;
        if (IsRunningUnderOwnUnit())
            return false;
        if (BackgroundGuardService.SuppressWatchdog)
            return false;

        try
        {
            var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            var servicePath = ServicePath;
            var systemdDir = Path.GetDirectoryName(servicePath)!;
            Directory.CreateDirectory(systemdDir);

            string? existing = null;
            try
            {
                if (File.Exists(servicePath))
                    existing = File.ReadAllText(servicePath);
            }
            catch { /* rewrite */ }

            if (NeedsRewrite(existing, exePath))
                File.WriteAllText(servicePath, BuildUnitContent(exePath));

            // Best-effort linger so reopen survives logout.
            TryEnableLingerAsync(CancellationToken.None).GetAwaiter().GetResult();

            RunSystemctl("--user daemon-reload");
            RunSystemctl($"--user enable {ServiceFileName}");
            var startCode = RunSystemctl($"--user start {ServiceFileName}");
            if (startCode != 0)
            {
                Console.Error.WriteLine(
                    $"[systemd] handoff start failed (exit {startCode}); continuing unsupervised");
                return false;
            }

            Console.Error.WriteLine(
                $"[systemd] handed off --background to {ServiceFileName} (RestartSec={RestartSec})");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[systemd] handoff failed: {ex.Message}; continuing unsupervised");
            return false;
        }
    }

    /// <summary>
    /// Best-effort <c>loginctl enable-linger</c> so the user unit survives logout
    /// (without this, crash reopen only works while a session is active).
    /// </summary>
    public static async Task TryEnableLingerAsync(CancellationToken ct = default)
    {
        try
        {
            var user = Environment.UserName;
            if (string.IsNullOrWhiteSpace(user)) return;

            var psi = new ProcessStartInfo
            {
                FileName = "loginctl",
                Arguments = $"enable-linger {user}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            /* linger often needs polkit/root — cooperative best-effort */
        }
    }

    private static bool UnitSectionHasStartLimit(string content)
    {
        // Require StartLimit* before the [Service] header so they are under [Unit].
        var serviceIdx = content.IndexOf("[Service]", StringComparison.Ordinal);
        if (serviceIdx < 0)
            return false;
        var unitPart = content[..serviceIdx];
        return unitPart.Contains("StartLimitBurst=0", StringComparison.Ordinal)
               && unitPart.Contains("StartLimitIntervalSec=0", StringComparison.Ordinal);
    }

    private static int RunSystemctl(string arguments)
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
        if (!proc.WaitForExit(15_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return -1;
        }
        return proc.ExitCode;
    }
}
