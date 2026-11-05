using System.Diagnostics;
using System.IO.Pipes;
using client.Core;

namespace client.Services;

/// <summary>
/// Activation pipe for the separate <c>--tracking-controls</c> process.
/// Distinct from <see cref="SingleInstanceService"/> so the headless tracker
/// and the Start/Stop modal can both listen without colliding.
/// </summary>
public static class TrackingControlsActivation
{
    public const string PipeName = "alpha-ai-tracker-tracking-controls";
    public const string CommandShow = "SHOW";

    public static string MutexName => $@"Global\{AppInfo.AppMutex}-tracking-controls";

    public static Action? OnShowRequested;

    private static CancellationTokenSource? _cts;

    public static void StartServer()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(_cts.Token));
    }

    public static void StopServer()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts = null;
    }

    /// <summary>True when the Start/Stop agent holds its global mutex.</summary>
    public static bool IsRunning()
    {
        try
        {
            using var existing = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Mutex exists but we cannot open it — treat as running.
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Linux: require a graphical session before spawning Avalonia (linger can
    /// start the tracker before GNOME is up).
    /// </summary>
    public static bool HasGraphicalSession()
    {
        if (!OperatingSystem.IsLinux()) return true;
        RefreshLinuxDesktopEnvironment();
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
               || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
    }

    /// <summary>Ask a running Start/Stop agent to show its window.</summary>
    public static bool TrySignalShow()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(CommandShow);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Signal existing agent, or spawn <c>--tracking-controls</c>.</summary>
    public static void ShowOrLaunch(bool refreshDesktopEnv = false)
    {
        if (TrySignalShow()) return;

        try
        {
            if (refreshDesktopEnv || OperatingSystem.IsLinux())
                RefreshLinuxDesktopEnvironment();

            if (OperatingSystem.IsLinux() && !HasGraphicalSession())
                return;

            var exe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exe)) return;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--tracking-controls",
                UseShellExecute = false,
            };
            // Inherit refreshed DISPLAY / WAYLAND / XAUTHORITY for Avalonia.
            Process.Start(psi);
        }
        catch
        {
            /* best-effort */
        }
    }

    /// <summary>
    /// Pull live graphical env from the user systemd manager (GNOME Wayland
    /// rotates XAUTHORITY under /run/user).
    /// </summary>
    public static void RefreshLinuxDesktopEnvironment()
    {
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = "--user show-environment",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return;
            }

            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "DISPLAY",
                "WAYLAND_DISPLAY",
                "XAUTHORITY",
                "XDG_RUNTIME_DIR",
                "DBUS_SESSION_BUS_ADDRESS",
            };
            foreach (var line in outputTask.GetAwaiter().GetResult().Split('\n'))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var name = line[..separator];
                if (!allowed.Contains(name)) continue;
                Environment.SetEnvironmentVariable(name, line[(separator + 1)..].TrimEnd('\r'));
            }
        }
        catch
        {
            /* existing env may already be valid */
        }
    }

    private static async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.Equals(line, CommandShow, StringComparison.Ordinal))
                {
                    try { OnShowRequested?.Invoke(); }
                    catch { /* never kill the listener */ }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(250, ct).ConfigureAwait(false); }
                catch { break; }
            }
            finally
            {
                try { server?.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
