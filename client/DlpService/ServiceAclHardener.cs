using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AlphaAITracker.Service;

/// <summary>
/// Locks the Windows service DACL for this product's DLP service only
/// (<see cref="ServiceIdentity.ServiceName"/>). Other installed software is untouched.
/// Prefer native SetServiceObjectSecurity so AppLocker cannot block <c>sc.exe</c>
/// for LOCAL SYSTEM under app allowlist lockdown.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceAclHardener
{
    /// <summary>
    /// SY: full control (incl. stop/delete/change DACL).
    /// BA: query/enumerate/read only — no SERVICE_STOP, DELETE, or CHANGE_CONFIG.
    /// </summary>
    public const string LockedSddl =
        "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWLOCRRC;;;BA)";

    /// <summary>
    /// After a verified removal code: SY + BA full so the service can stop and
    /// the Inno uninstaller can sc delete.
    /// </summary>
    public const string OpenSddl =
        "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)";

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint WRITE_DAC = 0x00040000;
    private const uint READ_CONTROL = 0x00020000;
    private const uint DACL_SECURITY_INFORMATION = 0x00000004;
    private const uint SDDL_REVISION_1 = 1;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string StringSecurityDescriptor,
        uint StringSDRevision,
        out IntPtr SecurityDescriptor,
        out uint SecurityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceObjectSecurity(
        IntPtr hService,
        uint dwSecurityInformation,
        IntPtr lpSecurityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static bool TryApplyLocked(ILogger? logger = null)
    {
        return TrySdSet(LockedSddl, logger);
    }

    public static bool TryApplyOpen(ILogger? logger = null)
    {
        return TrySdSet(OpenSddl, logger);
    }

    private static bool TrySdSet(string sddl, ILogger? logger)
    {
        var serviceName = ServiceIdentity.ServiceName;
        if (TrySdSetNative(serviceName, sddl, out var nativeDetail))
        {
            logger?.LogInformation("Service DACL updated (native) for {Service}", serviceName);
            return true;
        }

        logger?.LogWarning(
            "Native service DACL update failed for {Service}: {Detail}; falling back to sc.exe",
            serviceName, nativeDetail);

        // Legacy fallback — may be AppLocker-blocked even for SYSTEM under lockdown.
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"sdset {serviceName} \"{sddl}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            p.WaitForExit(10_000);
            if (p.ExitCode != 0)
            {
                var err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                logger?.LogWarning("sc sdset exit {Code}: {Err}", p.ExitCode, err.Trim());
                return false;
            }
            logger?.LogInformation("Service DACL updated (sc.exe) for {Service}", serviceName);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to set service DACL for {Service}", serviceName);
            return false;
        }
    }

    private static bool TrySdSetNative(string serviceName, string sddl, out string detail)
    {
        detail = "";
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            detail = "OpenSCManager:" + Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            var svc = OpenServiceW(scm, serviceName, WRITE_DAC | READ_CONTROL);
            if (svc == IntPtr.Zero)
            {
                detail = "OpenService:" + Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                        sddl, SDDL_REVISION_1, out var sd, out _))
                {
                    detail = "ConvertSD:" + Marshal.GetLastWin32Error();
                    return false;
                }

                try
                {
                    if (!SetServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, sd))
                    {
                        detail = "SetServiceObjectSecurity:" + Marshal.GetLastWin32Error();
                        return false;
                    }
                    return true;
                }
                finally
                {
                    LocalFree(sd);
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
}
