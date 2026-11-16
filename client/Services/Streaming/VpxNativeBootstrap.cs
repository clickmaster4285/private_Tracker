using System.Reflection;
using System.Runtime.InteropServices;
using vpxmd;

namespace client.Services.Streaming;

/// <summary>
/// Ensures Windows live-stream can load <c>vpxmd.dll</c> from the install directory.
/// SIPSorceryMedia.Encoders ships the DLL, but it depends on VC++ CRT
/// (<c>VCRUNTIME140.dll</c>). Machines without the redistributable fail with a bare
/// <see cref="DllNotFoundException"/> ("Unable to load DLL 'vpxmd'") even when
/// <c>vpxmd.dll</c> sits next to the exe — LoadLibrary cannot resolve dependents.
/// We (1) preload app-local CRT if present, (2) resolve <c>vpxmd</c> from
/// <see cref="AppContext.BaseDirectory"/> so cwd never matters.
/// </summary>
public static class VpxNativeBootstrap
{
    private static int _done;

    public static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Interlocked.Exchange(ref _done, 1) != 0) return;

        var baseDir = AppContext.BaseDirectory;
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

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(vpx_encoder).Assembly, ResolveVpx);
        }
        catch (InvalidOperationException)
        {
            // Resolver already registered (re-entry / second host).
        }
    }

    /// <summary>
    /// Probe loadability without encoding a frame. Returns null on success.
    /// </summary>
    public static string? ProbeWindowsLoadError()
    {
        if (!OperatingSystem.IsWindows()) return null;
        EnsureWindows();

        var baseDir = AppContext.BaseDirectory;
        var vpxPath = Path.Combine(baseDir, "vpxmd.dll");
        if (!File.Exists(vpxPath))
            return $"vpxmd.dll missing next to client ({baseDir}). Reinstall or rebuild the Windows package.";

        if (!NativeLibrary.TryLoad(vpxPath, out var handle))
        {
            var hasVcruntime = File.Exists(Path.Combine(baseDir, "VCRUNTIME140.dll"))
                               || File.Exists(Path.Combine(baseDir, "vcruntime140.dll"));
            var hasMsvcp = File.Exists(Path.Combine(baseDir, "MSVCP140.dll"))
                           || File.Exists(Path.Combine(baseDir, "msvcp140.dll"));
            if (!hasVcruntime || !hasMsvcp)
            {
                var missing = !hasVcruntime && !hasMsvcp
                    ? "VCRUNTIME140.dll and MSVCP140.dll"
                    : !hasVcruntime ? "VCRUNTIME140.dll" : "MSVCP140.dll";
                return $"Failed to load vpxmd.dll — {missing} missing. " +
                       "Install/reinstall 2.4.5+ Windows Setup or Microsoft Visual C++ 2015-2022 x64.";
            }
            return $"Failed to load vpxmd.dll from {vpxPath} (CRT present). Check bitness / AV quarantine.";
        }

        NativeLibrary.Free(handle);
        return null;
    }

    private static IntPtr ResolveVpx(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("vpxmd", StringComparison.OrdinalIgnoreCase)
            && !libraryName.Equals("vpxmd.dll", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        var path = Path.Combine(AppContext.BaseDirectory, "vpxmd.dll");
        if (NativeLibrary.TryLoad(path, out var handle))
            return handle;

        if (NativeLibrary.TryLoad("vpxmd", out handle))
            return handle;

        return IntPtr.Zero;
    }

    private static void TryPreload(string path)
    {
        if (!File.Exists(path)) return;
        NativeLibrary.TryLoad(path, out _);
    }
}
