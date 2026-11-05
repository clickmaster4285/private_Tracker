using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace client.Core.BrowserAccessibility;

/// <summary>
/// macOS implementation of <see cref="IAccessibilityBrowserReader"/> via the Accessibility
/// API surfaced through AppleScript (System Events). Best effort: window titles are always
/// available once the user grants Accessibility permission (the installer prompts for it);
/// the address-bar value is attempted per window and silently skipped when not exposed.
/// </summary>
public sealed class MacOsAccessibilityBrowserReader : IAccessibilityBrowserReader
{
    private static readonly string[] BrowserProcessNames =
    {
        "Google Chrome", "Safari", "Firefox", "Microsoft Edge",
        "Brave Browser", "Opera", "Vivaldi", "Arc",
    };

    private readonly ILogger<MacOsAccessibilityBrowserReader> _logger;
    private readonly IBrowserRegistry _browserRegistry;

    public string Platform => "macOS";
    public bool IsAvailable => OperatingSystem.IsMacOS();

    public MacOsAccessibilityBrowserReader(ILogger<MacOsAccessibilityBrowserReader> logger, IBrowserRegistry browserRegistry)
    {
        _logger = logger;
        _browserRegistry = browserRegistry;
    }

    public async Task<IReadOnlyList<AccessibilitySnapshot>> ReadAsync(CancellationToken ct)
    {
        var result = new List<AccessibilitySnapshot>();
        if (!OperatingSystem.IsMacOS() || ct.IsCancellationRequested)
            return result;

        try
        {
            var script = new StringBuilder();
            script.AppendLine("tell application \"System Events\"");
            script.AppendLine("  set out to \"\"");
            script.AppendLine("  repeat with p in (every process whose background only is false)");
            script.AppendLine("    set pn to name of p");
            script.AppendLine($"    if pn is in {AppleScriptList(_browserRegistry.GetAllBrowserDisplayNames().ToArray())} then");
            script.AppendLine("      try");
            script.AppendLine("        set fm to frontmost of p");
            script.AppendLine("        set wi to 0");
            script.AppendLine("        repeat with win in windows of p");
            script.AppendLine("          set wi to wi + 1");
            script.AppendLine("          try");
            script.AppendLine("            set wt to name of win");
            script.AppendLine("            set urlv to \"\"");
            script.AppendLine("            try");
            script.AppendLine("              set urlv to value of text field 1 of win");
            script.AppendLine("            end try");
            script.AppendLine("            set out to out & pn & \"\\t\" & wi & \"\\t\" & wt & \"\\t\" & urlv & \"\\t\" & fm & linefeed");
            script.AppendLine("          end try");
            script.AppendLine("        end repeat");
            script.AppendLine("      end try");
            script.AppendLine("    end if");
            script.AppendLine("  end repeat");
            script.AppendLine("  return out");
            script.AppendLine("end tell");

            var (exitCode, output, error) = await RunOsascriptAsync(script.ToString(), ct);
            if (exitCode != 0)
            {
                _logger.LogInformation(
                    "macOS accessibility reader unavailable (exit {Code}): {Err}. " +
                    "Grant Accessibility permission to Alpha AI Tracker in System Settings → Privacy & Security.",
                    exitCode, (error ?? string.Empty).Trim());
                return result;
            }

            if (string.IsNullOrWhiteSpace(output)) return result;

            var pids = ResolveBrowserPids();
            foreach (var line in output.Split('\n'))
            {
                ct.ThrowIfCancellationRequested();
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;

                var appName = parts[0];
                var title = parts[2];
                if (string.IsNullOrWhiteSpace(title)) continue;

                var url = parts.Length > 3 ? parts[3] : string.Empty;
                var pid = pids.GetValueOrDefault(appName, 0);
                // macOS: the first window of the FRONTMOST browser process is the focused
                // one (System Events enumerates windows in z-order); best effort.
                var frontmost = parts.Length > 4 && string.Equals(parts[4], "true", StringComparison.OrdinalIgnoreCase);

                result.Add(new AccessibilitySnapshot
                {
                    // macOS exposes no stable window id via AppleScript — use pid + window index;
                    // the tracker reconciles same-pid windows when the key shifts.
                    WindowKey = $"mac:{appName}:{parts[1]}:{pid}",
                    ProcessId = pid,
                    ProcessName = appName.ToLowerInvariant().Replace(" ", string.Empty),
                    WindowTitle = title,
                    Url = BrowserAccessibilityHelpers.NormalizeUrl(url),
                    IsIncognito = BrowserAccessibilityHelpers.TitleSuggestsIncognito(title),
                    IsActive = frontmost && parts[1] == "1",
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "macOS accessibility read failed");
        }

        return result;
    }

    private Dictionary<string, int> ResolveBrowserPids()
    {
        var displayNames = _browserRegistry.GetAllBrowserDisplayNames();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var name = proc.ProcessName;
                    foreach (var browser in displayNames)
                    {
                        var hint = browser.Replace(" ", string.Empty);
                        if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                        {
                            map[browser] = proc.Id;
                            break;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    private static string AppleScriptList(string[] items) =>
        "{" + string.Join(", ", items.Select(i => $"\"{i}\"")) + "}";

    private async Task<(int exitCode, string? output, string? error)> RunOsascriptAsync(string script, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        proc.Start();
        await proc.StandardInput.WriteAsync(script.AsMemory(), ct);
        proc.StandardInput.Close();
        var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errorTask = proc.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
        return (proc.ExitCode, await outputTask, await errorTask);
    }
}
