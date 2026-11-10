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
        TryPreload(Path.Combine(baseDir, "VCRUNTIME140.dll"));
        TryPreload(Path.Combine(baseDir, "vcruntime140.dll"));
        TryPreload(Path.Combine(baseDir, "VCRUNTIME140_1.dll"));
        TryPreload(Path.Combine(baseDir, "vcruntime140_1.dll"));

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
            var hasCrt = File.Exists(Path.Combine(baseDir, "VCRUNTIME140.dll"))
                         || File.Exists(Path.Combine(baseDir, "vcruntime140.dll"));
            return hasCrt
                ? $"Failed to load vpxmd.dll from {vpxPath} (CRT present). Check bitness / AV quarantine."
                : "Failed to load vpxmd.dll — VCRUNTIME140.dll is missing. Install VC++ 2015-2022 x64 redistributable or use client 1.0.8+ which ships it app-local.";
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
