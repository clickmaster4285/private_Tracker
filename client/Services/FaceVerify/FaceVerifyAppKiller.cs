using System.Diagnostics;
using client.Core;

namespace client.Services.FaceVerify;

/// <summary>
/// While the soft-locker is open, periodically kill interactive user-session apps
/// outside a never-kill set so CPU/GPU go to face verify. Never touches session 0,
/// Explorer/shell, DLP service, or the tracker/soft-lock processes.
/// Gated by <c>ALPHA_FACE_VERIFY_KILL_APPS</c> (default true).
/// </summary>
public static class FaceVerifyAppKiller
{
    private static readonly object Gate = new();
    private static CancellationTokenSource? _cts;

    public static void StartIfEnabled()
    {
        if (!OperatingSystem.IsWindows()) return;
        var env = Environment.GetEnvironmentVariable("ALPHA_FACE_VERIFY_KILL_APPS");
        if (env is "0" or "false" or "False") return;

        lock (Gate)
        {
            StopUnlocked();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => LoopAsync(ct), ct);
        }
    }

    public static void Stop()
    {
        lock (Gate) StopUnlocked();
    }

    private static void StopUnlocked()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        // First pass quickly after soft-lock paints.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (FaceVerifyYield.ShouldYield)
                    SweepOnce();
            }
            catch
            {
                // Never let the sweeper kill the soft-lock loop.
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static void SweepOnce()
    {
        var selfId = Environment.ProcessId;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                using (proc)
                {
                    if (proc.Id <= 4 || proc.Id == selfId)
                        continue;
                    int session;
                    try { session = proc.SessionId; }
                    catch { continue; }
                    if (session <= 0)
                        continue; // services / session 0

                    var name = proc.ProcessName;
                    if (IsNeverKillName(name))
                        continue;

                    try
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Access denied / already exited.
                    }
                }
            }
            catch
            {
                // Process exited mid-loop.
            }
        }
    }

    private static bool IsNeverKillName(string processName)
    {
        var n = processName.Trim();
        // Product + soft-lock (same exe name as tracker).
        if (n.Equals(AppInfo.ExecutableName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.Equals("client", StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.Equals(AppInfo.WindowsServiceName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.Equals("AlphaAITracker.Service", StringComparison.OrdinalIgnoreCase))
            return true;

        // Critical OS + shell + camera/audio stack needed for soft-lock webcam.
        string[] forever =
        [
            "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
            "svchost", "dwm", "fontdrvhost", "conhost", "explorer", "userinit", "LogonUI",
            "sihost", "taskhostw", "RuntimeBroker", "ApplicationFrameHost", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellHost", "TextInputHost",
            "ctfmon", "dllhost", "backgroundTaskHost",
            "SecurityHealthService", "MsMpEng", "NisSrv", "SgrmBroker", "Memory Compression",
            "audiodg", "AudioDg", "WUDFHost",
            "WindowsCamera", "FrameServer", "FrameServerClient",
            "NVDisplay.Container", "nvidiawebhelper", "NVIDIA Share", "NVIDIA Web Helper",
            "RadeonSoftware", "amdow", "igfxEM", "igfxCUIService", "IntelGraphicsSoftware",
        ];
        foreach (var f in forever)
        {
            if (n.Equals(f, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
