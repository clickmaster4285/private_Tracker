using System.Diagnostics;
using client.Configuration;
using client.Core;
using client.Core.Models;
using Microsoft.Data.Sqlite;

namespace client.Services.FaceVerify;

/// <summary>
/// Soft-gate spawn of <c>--face-verify</c> on every Windows session start
/// (process boot restore, GUI restore, login, unlock). Never skipped because of a prior
/// successful local verify — only skipped if a verify window is already open.
/// Tracking never awaits this process.
/// </summary>
public static class FaceVerifyLauncher
{
    private static FaceVerifySupervisor? _supervisor;

    /// <summary>Alias for <see cref="TryLaunchOnSessionStart"/> (login / restore call sites).</summary>
    public static void TryLaunchAfterLogin(AppConfig config, EmployeeInfo info) =>
        TryLaunchOnSessionStart(config, info, trigger: "login");

    private static FaceVerifySupervisor GetSupervisor(AppConfig config) =>
        _supervisor ??= new FaceVerifySupervisor(config);

    /// <summary>
    /// Early launch before Host.Build on --background: read employee from tracker SQLite
    /// with no DI host.
    /// </summary>
    public static void TryLaunchEarly(AppConfig config, string trigger = "boot")
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!config.FaceVerifyEnabled) return;
        if (FaceVerifySession.IsVerifyProcessRunning()) return;

        if (!TryReadEmployeeLight(config, out var info) || info == null)
            return;

        TryLaunchOnSessionStart(config, info, trigger);
    }

    public static void TryLaunchOnSessionStart(AppConfig config, EmployeeInfo info, string trigger = "boot")
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!config.FaceVerifyEnabled) return;
        // Soft-lock uses local biometric.db only; token/server optional (warmup still needs them).
        if (FaceVerifySession.IsVerifyProcessRunning()) return;
        if (string.IsNullOrWhiteSpace(info.EmployeeId)) return;

        // Supervisor owns spawn + crash relaunch (fail-closed).
        GetSupervisor(config).RequestLaunch(info, trigger);
    }

    internal static void SpawnProcess(AppConfig config, EmployeeInfo info, string trigger)
    {
        if (FaceVerifySession.IsVerifyProcessRunning()) return;

        // Locker is zero-network: do not hand a device token to the soft-lock process.
        FaceVerifySession.Write(new FaceVerifySession
        {
            EmployeeId = info.EmployeeId,
            EmployeeName = info.Name,
            DeviceToken = "",
            ApiBase = "",
            MachineId = config.ClientId,
            MatchThreshold = config.FaceMatchThreshold,
            Trigger = string.IsNullOrWhiteSpace(trigger) ? "boot" : trigger,
            RecModelUrl = null,
        });

        try
        {
            FaceVerifyYield.Set("soft_lock");
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--face-verify",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            try
            {
                if (proc != null)
                    proc.PriorityClass = ProcessPriorityClass.High;
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[client] Failed to launch --face-verify: {ex.Message}");
        }
    }

    private static bool TryReadEmployeeLight(AppConfig config, out EmployeeInfo? info)
    {
        info = null;
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = TrackerDeviceTokenReader.CandidatePaths(local, profile, config.DbPath);
            if (!TrackerDeviceTokenReader.TryPickNewest(paths, out var token, out var employeeId, out var sourcePath, out _))
                return false;
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(sourcePath))
                return false;

            using var conn = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT employee_id, name, COALESCE(device_token, token) FROM employee_info " +
                "WHERE length(COALESCE(device_token, token, '')) > 0 LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return false;
            var empId = r.IsDBNull(0) ? employeeId ?? "" : r.GetString(0);
            var name = r.IsDBNull(1) ? "" : r.GetString(1);
            var tok = r.IsDBNull(2) ? token : r.GetString(2);
            if (string.IsNullOrWhiteSpace(empId) || string.IsNullOrWhiteSpace(tok)) return false;
            info = new EmployeeInfo
            {
                EmployeeId = empId,
                Name = name,
                DeviceToken = tok,
                Token = tok,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
}
