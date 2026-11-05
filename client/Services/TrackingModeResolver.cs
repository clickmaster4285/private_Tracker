using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Resolves effective Fixed/Roaming mode for local gates (P1).
/// Windows: LocalSystem pipe → ProgramData control.json → fail-safe Fixed.
/// Linux/macOS: LocalAppData control.json → fail-open roaming until first poll.
/// </summary>
public static class TrackingModeResolver
{
    public sealed record Snapshot(
        string Mode,
        string DesiredState,
        string Phase,
        long Generation);

    /// <summary>Fail-safe Fixed / running / collecting.</summary>
    public static Snapshot FailSafeFixed { get; } = new("fixed", "running", "collecting", 0);

    public static async Task<Snapshot> ResolveAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var r = await DlpPipeClient.GetTrackingStateDetailedAsync(ct).ConfigureAwait(false);
                if (r.Connected && !r.TimedOut && !string.IsNullOrWhiteSpace(r.Body))
                {
                    using var doc = JsonDocument.Parse(r.Body);
                    if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                        return FromJson(doc.RootElement);
                }
            }
            catch
            {
                /* fall through to file */
            }
        }

        try
        {
            var path = TrackingControlPaths.ControlJsonPath;
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
                return FromJson(doc.RootElement);
            }
        }
        catch
        {
            /* platform fail-safe below */
        }

        // Windows: missing/corrupt ProgramData ⇒ Fixed (LocalSystem fail-safe).
        // Linux/macOS: cooperative fail-open roaming until the first successful poll
        // writes control.json (no privileged arbitrator yet — see linuxplan.md L1).
        return OperatingSystem.IsWindows()
            ? FailSafeFixed
            : new Snapshot("roaming", "running", "collecting", 0);
    }

    public static bool IsFixed(Snapshot s) =>
        !string.Equals(s.Mode, "roaming", StringComparison.OrdinalIgnoreCase);

    private static Snapshot FromJson(JsonElement root)
    {
        var mode = root.TryGetProperty("trackingMode", out var m) || root.TryGetProperty("mode", out m)
            ? (m.GetString() ?? "fixed")
            : "fixed";
        mode = mode.Trim().ToLowerInvariant();
        if (mode is not ("fixed" or "roaming"))
            mode = "fixed";

        var desired = root.TryGetProperty("desiredState", out var d)
            ? (d.GetString() ?? "running")
            : "running";
        desired = desired.Trim().ToLowerInvariant();
        if (desired is not ("running" or "stopped"))
            desired = "running";

        var phase = root.TryGetProperty("phase", out var p)
            ? (p.GetString() ?? "collecting")
            : "collecting";

        var gen = root.TryGetProperty("generation", out var g) && g.TryGetInt64(out var gv) ? gv : 0L;
        return new Snapshot(mode, desired, phase, gen);
    }
}
