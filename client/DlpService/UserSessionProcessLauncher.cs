using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AlphaAITracker.Service;

/// <summary>
/// Launch a process in the interactive console user's session from LocalSystem
/// (Session 0 isolation). Required so the watchdog can restart the Avalonia
/// tracker after Task Manager End Task — Process.Start from the service would
/// land in Session 0 and never reach the desktop.
/// </summary>
internal static class UserSessionProcessLauncher
{
    public static bool TryLaunch(string exePath, string arguments, ILogger? log = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            log?.LogWarning("Watchdog: executable missing at {Path}", exePath);
            return false;
        }

        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            log?.LogDebug("Watchdog: no active console session");
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            log?.LogDebug(
                "Watchdog: WTSQueryUserToken failed for session {Session} (err={Err})",
                sessionId, Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            if (!DuplicateTokenEx(
                    userToken,
                    TOKEN_ALL_ACCESS,
                    IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    TOKEN_TYPE.TokenPrimary,
                    out var primaryToken))
            {
                log?.LogWarning(
                    "Watchdog: DuplicateTokenEx failed (err={Err})",
                    Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                var env = IntPtr.Zero;
                try
                {
                    if (!CreateEnvironmentBlock(out env, primaryToken, false))
                        env = IntPtr.Zero;

                    var si = new STARTUPINFO
                    {
                        cb = Marshal.SizeOf<STARTUPINFO>(),
                        lpDesktop = "winsta0\\default",
                    };
                    var cmd = new StringBuilder();
                    cmd.Append('"').Append(exePath).Append('"');
                    if (!string.IsNullOrWhiteSpace(arguments))
                        cmd.Append(' ').Append(arguments);

                    var workDir = Path.GetDirectoryName(exePath) ?? "";
                    var created = CreateProcessAsUser(
                        primaryToken,
                        null,
                        cmd,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                        env,
                        string.IsNullOrEmpty(workDir) ? null : workDir,
                        ref si,
                        out var pi);

                    if (!created)
                    {
                        log?.LogWarning(
                            "Watchdog: CreateProcessAsUser failed (err={Err})",
                            Marshal.GetLastWin32Error());
                        return false;
                    }

                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
                    log?.LogInformation(
                        "Watchdog: relaunched {Exe} in session {Session} (pid pending)",
                        exePath, sessionId);
                    return true;
                }
                finally
                {
                    if (env != IntPtr.Zero)
                        DestroyEnvironmentBlock(env);
                }
            }
            finally
            {
                CloseHandle(primaryToken);
            }
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    /// <summary>True when a process named <paramref name="processName"/> (no .exe) is running.</summary>
    public static bool IsProcessRunning(string processName)
    {
        try
        {
            var procs = Process.GetProcessesByName(processName);
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when a named mutex already exists (held by another process). Used so the
    /// watchdog can detect the headless tracker (<c>Global\{APP_MUTEX}</c>).
    /// </summary>
    public static bool IsNamedMutexPresent(string mutexName)
    {
        try
        {
            using var existing = Mutex.OpenExisting(mutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    private enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityImpersonation = 2,
    }

    private enum TOKEN_TYPE
    {
        TokenPrimary = 1,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel,
        TOKEN_TYPE tokenType,
        out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
