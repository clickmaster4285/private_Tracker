using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Resolves <see cref="ClientProcessMode.RunMode"/> before the DI host starts producers.
/// Windows: pipe / ProgramData control.json (fail-safe Fixed → Full).
/// Linux/macOS: LocalAppData control.json (fail-open roaming → Full; follower pauses when stopped).
/// </summary>
public static class TrackingModeGate
{
    public static TrackingRunMode Resolve(string[] args)
    {
        if (HasFlag(args, "--gui"))
        {
            ClientProcessMode.GuiAgent = true;
            ClientProcessMode.RunMode = TrackingRunMode.GuiAgent;
            return TrackingRunMode.GuiAgent;
        }

        // Explicit CLI overrides (watchdog / Control Shell launchers).
        if (HasFlag(args, "--control-shell"))
        {
            ClientProcessMode.RunMode = TrackingRunMode.ControlShell;
            return TrackingRunMode.ControlShell;
        }

        if (HasFlag(args, "--drain-only"))
        {
            ClientProcessMode.RunMode = TrackingRunMode.DrainOnly;
            return TrackingRunMode.DrainOnly;
        }

        // Windows fail-safe Fixed; Linux/macOS fail-open roaming until control.json exists.
        var desired = "running";
        var phase = "collecting";
        var mode = OperatingSystem.IsWindows() ? "fixed" : "roaming";

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var (ok, body) = DlpPipeClient.SendAsync(new { op = "GetTrackingState" })
                    .GetAwaiter().GetResult();
                if (ok && !string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
                    {
                        mode = doc.RootElement.TryGetProperty("trackingMode", out var m)
                            ? m.GetString() ?? "fixed" : "fixed";
                        desired = doc.RootElement.TryGetProperty("desiredState", out var d)
                            ? d.GetString() ?? "running" : "running";
                        phase = doc.RootElement.TryGetProperty("phase", out var p)
                            ? p.GetString() ?? "collecting" : "collecting";
                    }
                }
                else
                {
                    TryReadControlFile(ref mode, ref desired, ref phase);
                }
            }
            catch
            {
                TryReadControlFile(ref mode, ref desired, ref phase);
            }
        }
        else
        {
            TryReadControlFile(ref mode, ref desired, ref phase);
        }

        mode = mode.Trim().ToLowerInvariant();
        desired = desired.Trim().ToLowerInvariant();
        phase = phase.Trim().ToLowerInvariant();

        // Fixed always collects (fail-safe).
        if (mode != "roaming")
        {
            ClientProcessMode.RunMode = TrackingRunMode.Full;
            return TrackingRunMode.Full;
        }

        // Offline Roaming Start/Stop lives in LocalRoamingControl + follower.
        // Always boot Full; follower PauseTracking when local desired=stopped.
        _ = desired;
        _ = phase;
        ClientProcessMode.RunMode = TrackingRunMode.Full;
        return TrackingRunMode.Full;
    }

    private static void TryReadControlFile(ref string mode, ref string desired, ref string phase)
    {
        try
        {
            var path = TrackingControlPaths.ControlJsonPath;
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("mode", out var m) || root.TryGetProperty("trackingMode", out m))
                mode = m.GetString() ?? mode;
            if (root.TryGetProperty("desiredState", out var d))
                desired = d.GetString() ?? desired;
            if (root.TryGetProperty("phase", out var p))
                phase = p.GetString() ?? phase;
        }
        catch
        {
            /* platform default mode/desired via callers */
        }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
}
