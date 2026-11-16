using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace client.Services.FaceVerify;

/// <summary>
/// Ensures Windows face soft-lock can load CPU <c>onnxruntime.dll</c> from the install
/// directory (same pattern as <c>VpxNativeBootstrap</c> for live-stream). Soft-lock is a
/// fail-closed gate — missing CRT / native ORT must surface an honest message, not an
/// opaque <c>NativeMethods</c> type-initializer crash.
/// </summary>
public static class OrtNativeBootstrap
{
    private const uint LoadWithAlteredSearchPath = 0x00000008;
    private const int FormatMessageFromSystem = 0x00001000;
    private const int FormatMessageIgnoreInserts = 0x00000200;

    // Win32 codes that mean "something on the box refused our own file" (AV / policy),
    // as opposed to a packaging problem (bad image, missing dependency).
    private const int ErrorAccessDenied = 5;
    private const int ErrorVirusInfected = 225;
    private const int ErrorVirusDeleted = 226;
    private const int ErrorAccessDisabledByPolicy = 1260;
    private const int ErrorSystemIntegrityPolicy = 4551;
    private const int ErrorModNotFound = 126;
    private const int ErrorBadExeFormat = 193;

    private static int _done;

    public static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Interlocked.Exchange(ref _done, 1) != 0) return;

        var baseDir = AppContext.BaseDirectory;
        // Full VC++ CRT first (ORT needs VCRUNTIME + MSVCP), then providers_shared, then ORT.
        foreach (var name in new[]
                 {
                     "VCRUNTIME140.dll", "vcruntime140.dll",
                     "VCRUNTIME140_1.dll", "vcruntime140_1.dll",
                     "MSVCP140.dll", "msvcp140.dll",
                     "MSVCP140_1.dll", "msvcp140_1.dll",
                     "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll",
                     "concrt140.dll", "vccorlib140.dll",
                 })
            TryPreload(Path.Combine(baseDir, name));
        TryPreload(Path.Combine(baseDir, "onnxruntime_providers_shared.dll"));
        TryLoadLibraryEx(Path.Combine(baseDir, "onnxruntime.dll"), out _);
    }

    /// <summary>Allow a fresh preload after the DLP service restored / excluded our files.</summary>
    public static void ResetForRetry() => Interlocked.Exchange(ref _done, 0);

    /// <summary>
    /// Probe loadability before constructing ORT types. Returns null on success.
    /// </summary>
    public static string? ProbeWindowsLoadError() => ProbeWindowsLoadError(out _);

    /// <param name="avSuspected">
    /// True when antivirus / policy is the likely cause (file vanished = quarantined, or a
    /// virus / access / policy Win32 code) — the caller should ask the DLP service to repair
    /// exclusions and restore quarantined product files, then probe again.
    /// </param>
    public static string? ProbeWindowsLoadError(out bool avSuspected)
    {
        avSuspected = false;
        if (!OperatingSystem.IsWindows()) return null;
        EnsureWindows();

        var baseDir = AppContext.BaseDirectory;
        var ortPath = Path.Combine(baseDir, "onnxruntime.dll");
        var sharedPath = Path.Combine(baseDir, "onnxruntime_providers_shared.dll");

        if (!File.Exists(ortPath))
        {
            avSuspected = true;
            return $"onnxruntime.dll missing next to client ({baseDir}). " +
                   "It was either quarantined by antivirus or not installed — reinstall the Windows package.";
        }

        if (!File.Exists(sharedPath))
        {
            avSuspected = true;
            return $"onnxruntime_providers_shared.dll missing next to client ({baseDir}). " +
                   "It was either quarantined by antivirus or not installed — reinstall 2.4.3+ Windows Setup.";
        }

        var hasVcruntime = File.Exists(Path.Combine(baseDir, "vcruntime140.dll"))
                           || File.Exists(Path.Combine(baseDir, "VCRUNTIME140.dll"));
        var hasMsvcp = File.Exists(Path.Combine(baseDir, "msvcp140.dll"))
                       || File.Exists(Path.Combine(baseDir, "MSVCP140.dll"));

        // Prefer LoadLibraryEx so dependents resolve from the install dir and we get Win32 codes.
        var handle = TryLoadLibraryEx(ortPath, out var win32);
        if (handle == IntPtr.Zero && NativeLibrary.TryLoad(ortPath, out var nh))
            handle = nh;

        if (handle == IntPtr.Zero)
        {
            if (!hasVcruntime || !hasMsvcp)
            {
                var missing = !hasVcruntime && !hasMsvcp
                    ? "VCRUNTIME140.dll and MSVCP140.dll"
                    : !hasVcruntime ? "VCRUNTIME140.dll" : "MSVCP140.dll";
                return $"Failed to load onnxruntime.dll — {missing} missing next to client. " +
                       "Install/reinstall 2.4.5+ Windows Setup (ships VC++ CRT + quiet redistributable), " +
                       "or install Microsoft Visual C++ 2015-2022 x64 redistributable.";
            }

            avSuspected = win32 is ErrorAccessDenied or ErrorVirusInfected or ErrorVirusDeleted
                or ErrorAccessDisabledByPolicy or ErrorSystemIntegrityPolicy;

            var hint = win32 switch
            {
                ErrorVirusInfected or ErrorVirusDeleted =>
                    " Microsoft Defender flagged the file — the tracker service restores and excludes it automatically.",
                ErrorAccessDenied =>
                    " Access denied — antivirus / Controlled Folder Access blocked the read.",
                ErrorAccessDisabledByPolicy =>
                    " Blocked by software-restriction / AppLocker policy.",
                ErrorSystemIntegrityPolicy =>
                    " Blocked by Smart App Control / WDAC code-integrity policy.",
                ErrorBadExeFormat =>
                    " Wrong bitness or corrupt file — reinstall the x64 Windows Setup.",
                ErrorModNotFound when File.Exists(Path.Combine(baseDir, "DirectML.dll")) =>
                    " Stale DirectML.dll leftover detected — uninstall fully or install 2.4.3+ (removes it).",
                ErrorModNotFound =>
                    " A dependency DLL is missing — reinstall the Windows Setup.",
                _ =>
                    " Check Defender quarantine / Controlled Folder Access, or bitness mismatch.",
            };

            return win32 != 0
                ? $"Failed to load onnxruntime.dll from {ortPath} (Win32 {win32}: {FormatWin32(win32)}).{hint}"
                : $"Failed to load onnxruntime.dll from {ortPath}.{hint}";
        }

        try { FreeLibrary(handle); } catch { /* ignore */ }
        return null;
    }

    private static IntPtr TryLoadLibraryEx(string path, out int win32)
    {
        win32 = 0;
        if (!File.Exists(path)) return IntPtr.Zero;
        try
        {
            // LOAD_WITH_ALTERED_SEARCH_PATH: resolve imports from the DLL's directory first.
            var h = LoadLibraryExW(path, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (h == IntPtr.Zero) win32 = Marshal.GetLastPInvokeError();
            return h;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void TryPreload(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var h = TryLoadLibraryEx(path, out _);
            if (h == IntPtr.Zero)
                NativeLibrary.TryLoad(path, out _);
        }
        catch
        {
            // Best-effort; ProbeWindowsLoadError reports the real failure.
        }
    }

    private static string FormatWin32(int code)
    {
        if (code == 0) return "unknown";
        try
        {
            return new Win32Exception(code).Message;
        }
        catch
        {
            var sb = new StringBuilder(256);
            _ = FormatMessage(
                FormatMessageFromSystem | FormatMessageIgnoreInserts,
                IntPtr.Zero,
                (uint)code,
                0,
                sb,
                sb.Capacity,
                IntPtr.Zero);
            var text = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(text) ? $"error {code}" : text;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int FormatMessage(
        int dwFlags,
        IntPtr lpSource,
        uint dwMessageId,
        int dwLanguageId,
        StringBuilder lpBuffer,
        int nSize,
        IntPtr arguments);
}
