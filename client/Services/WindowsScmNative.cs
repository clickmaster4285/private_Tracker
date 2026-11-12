using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace client.Services;

/// <summary>
/// In-process SCM helpers (advapi32) so teardown can query/stop/delete the DLP
/// service without spawning <c>sc.exe</c> — AppLocker default-deny blocks that
/// EXE for interactive users while the already-running process may still call
/// OpenService / ControlService / DeleteService.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsScmNative
{
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint DELETE = 0x10000;
    private const uint SERVICE_CONTROL_STOP = 0x00000001;
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;

    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;
    private const int ERROR_ACCESS_DENIED = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        public IntPtr lpRebootMsg;
        public IntPtr lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, out SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfig2W(IntPtr hService, uint dwInfoLevel, IntPtr lpInfo);

    /// <summary>
    /// Returns <c>absent</c>, <c>RUNNING</c>, <c>STOPPED</c>, <c>STOP_PENDING</c>,
    /// <c>present</c>, or <c>query_failed:…</c> — same vocabulary as the old sc.exe path.
    /// </summary>
    public static string QueryServiceState(string serviceName)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero)
            return "query_failed:OpenSCManager:" + Marshal.GetLastWin32Error();

        try
        {
            var svc = OpenServiceW(scm, serviceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                return err == ERROR_SERVICE_DOES_NOT_EXIST
                    ? "absent"
                    : "query_failed:OpenService:" + err;
            }

            try
            {
                if (!QueryServiceStatus(svc, out var status))
                    return "query_failed:QueryServiceStatus:" + Marshal.GetLastWin32Error();

                return status.dwCurrentState switch
                {
                    1 => "STOPPED",
                    2 => "START_PENDING",
                    3 => "STOP_PENDING",
                    4 => "RUNNING",
                    5 => "CONTINUE_PENDING",
                    6 => "PAUSE_PENDING",
                    7 => "PAUSED",
                    _ => "present",
                };
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static bool ServiceExists(string serviceName)
    {
        var state = QueryServiceState(serviceName);
        if (state == "absent") return false;
        if (state.StartsWith("query_failed", StringComparison.Ordinal)) return false;
        return true;
    }

    public static (bool Ok, string Detail) TryClearFailureActions(string serviceName)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            return (false, "OpenSCManager:" + Marshal.GetLastWin32Error());

        try
        {
            var svc = OpenServiceW(scm, serviceName, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_SERVICE_DOES_NOT_EXIST) return (true, "absent");
                return (false, "OpenService:" + err);
            }

            try
            {
                var actions = new SERVICE_FAILURE_ACTIONS
                {
                    dwResetPeriod = 0,
                    lpRebootMsg = IntPtr.Zero,
                    lpCommand = IntPtr.Zero,
                    cActions = 0,
                    lpsaActions = IntPtr.Zero,
                };
                var size = Marshal.SizeOf<SERVICE_FAILURE_ACTIONS>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(actions, ptr, false);
                    if (!ChangeServiceConfig2W(svc, SERVICE_CONFIG_FAILURE_ACTIONS, ptr))
                        return (false, "ChangeServiceConfig2:" + Marshal.GetLastWin32Error());
                    return (true, "cleared");
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static (bool Ok, string Detail) TryStop(string serviceName)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            return (false, "OpenSCManager:" + Marshal.GetLastWin32Error());

        try
        {
            var svc = OpenServiceW(scm, serviceName, SERVICE_STOP | SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_SERVICE_DOES_NOT_EXIST) return (true, "absent");
                return (false, "OpenService:" + err);
            }

            try
            {
                if (!ControlService(svc, SERVICE_CONTROL_STOP, out _))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ERROR_SERVICE_NOT_ACTIVE) return (true, "already_stopped");
                    return (false, "ControlService:" + err);
                }
                return (true, "stop_sent");
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static (bool Ok, string Detail) TryDelete(string serviceName)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            return (false, "OpenSCManager:" + Marshal.GetLastWin32Error());

        try
        {
            var svc = OpenServiceW(scm, serviceName, DELETE);
            if (svc == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_SERVICE_DOES_NOT_EXIST) return (true, "absent");
                return (false, "OpenService:" + err);
            }

            try
            {
                if (!DeleteService(svc))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ERROR_SERVICE_DOES_NOT_EXIST) return (true, "absent");
                    return (false, "DeleteService:" + err);
                }
                return (true, "deleted");
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// Kill processes by image name without spawning taskkill.exe (AppLocker-safe).
    /// </summary>
    public static (int Killed, string Detail) TryKillByImageName(string imageFileName)
    {
        var name = imageFileName;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        var killed = 0;
        var errors = new StringBuilder();
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
                    using (p)
                    {
                        p.Kill(entireProcessTree: true);
                        killed++;
                    }
                }
                catch (Exception ex)
                {
                    if (errors.Length > 0) errors.Append(';');
                    errors.Append(ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            return (0, "enum_error:" + ex.Message);
        }

        return (killed, killed > 0
            ? "killed=" + killed
            : errors.Length > 0 ? "none:" + errors : "none_running");
    }

    /// <summary>
    /// Create + run a one-shot SYSTEM task via Task Scheduler COM (no schtasks.exe).
    /// Requires elevation to register as SYSTEM; returns false when COM/UAC denies.
    /// </summary>
    public static (bool Ok, string Detail) TryRunSystemCmdViaTaskScheduler(string taskName, string cmdLine)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
                return (false, "Schedule.Service ProgID missing");

            dynamic? scheduler = Activator.CreateInstance(type);
            if (scheduler is null)
                return (false, "Schedule.Service create failed");

            try
            {
                scheduler.Connect();
                dynamic folder = scheduler.GetFolder("\\");
                try { folder.DeleteTask(taskName, 0); } catch { /* first run */ }

                dynamic definition = scheduler.NewTask(0);
                definition.RegistrationInfo.Description = "Alpha AI Tracker teardown cleanup";
                definition.Settings.Enabled = true;
                definition.Settings.AllowDemandStart = true;
                definition.Settings.StartWhenAvailable = true;
                definition.Settings.DisallowStartIfOnBatteries = false;
                definition.Settings.StopIfGoingOnBatteries = false;
                definition.Settings.Hidden = true;
                definition.Principal.UserId = "SYSTEM";
                definition.Principal.LogonType = 5; // TASK_LOGON_SERVICE_ACCOUNT
                definition.Principal.RunLevel = 1;  // TASK_RUNLEVEL_HIGHEST

                dynamic action = definition.Actions.Create(0); // TASK_ACTION_EXEC
                action.Path = "cmd.exe";
                action.Arguments = "/c " + cmdLine;

                // 6 = TASK_CREATE_OR_UPDATE
                folder.RegisterTaskDefinition(
                    taskName, definition, 6, null, null, 5, null);

                dynamic task = folder.GetTask(taskName);
                task.Run(null);
                return (true, "scheduled_and_run");
            }
            finally
            {
                try
                {
                    dynamic folder = scheduler.GetFolder("\\");
                    // Best-effort delete after a short settle is done by the caller.
                    _ = folder;
                }
                catch { /* ignore */ }
                try { Marshal.FinalReleaseComObject(scheduler); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ":" + ex.Message);
        }
    }

    public static void TryDeleteScheduledTask(string taskName)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return;
            dynamic? scheduler = Activator.CreateInstance(type);
            if (scheduler is null) return;
            try
            {
                scheduler.Connect();
                dynamic folder = scheduler.GetFolder("\\");
                folder.DeleteTask(taskName, 0);
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(scheduler); } catch { /* ignore */ }
            }
        }
        catch
        {
            /* ignore */
        }
    }

    // Silence unused-access warning for ERROR_ACCESS_DENIED when callers match on Detail.
    internal static bool IsAccessDeniedDetail(string detail) =>
        detail.Contains(ERROR_ACCESS_DENIED.ToString(), StringComparison.Ordinal)
        || detail.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);
}
