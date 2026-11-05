using System.IO.Pipes;

namespace client.Core;

/// <summary>
/// Cross-platform single-instance activation for Alpha AI Tracker.
///
/// The app is designed as ONE long-lived process (the "background" instance
/// launched by auto-start / systemd with --minimized / --background). That
/// process owns the global mutex, runs all collection/sync services, and keeps
/// a hidden window + tray icon alive so closing the window does not stop
/// tracking.
///
/// When the user launches the app a SECOND time (e.g. clicks the desktop
/// entry while the background instance is already running), the mutex is
/// already held. Instead of exiting silently — which made the GUI "open and
/// instantly close" — the second launch sends a SHOW signal to the running
/// instance via a named pipe. The running instance then brings its window to
/// the front. This lets the user open/close the GUI any number of times
/// without ever stopping the background tracking.
/// </summary>
public static class SingleInstanceService
{
    /// <summary>
    /// Machine-wide mutex name used by every launch path.
    ///
    /// On Unix, an unqualified .NET named mutex is scoped to the process session
    /// (<c>/tmp/.dotnet/shm/session&lt;sid&gt;</c>). A desktop launch and a systemd
    /// user service have different session IDs, so both could acquire the old
    /// mutex and run collectors against the same SQLite database. The
    /// <c>Global\</c> namespace maps them to the same machine-wide mutex backing
    /// object. Windows uses the same prefix to cross Terminal Services sessions.
    /// </summary>
    public static string MutexName => $@"Global\{AppInfo.AppMutex}";

    /// <summary>
    /// Pipe name used for cross-instance signalling. The same name must be
    /// used by the server (running instance) and the client (second launch).
    /// Lower-case, no path separators — safe on Windows named pipes and on
    /// Linux/macOS (where .NET maps it to a Unix domain socket path).
    /// </summary>
    public const string PipeName = "alpha-ai-tracker-activation";

    private static CancellationTokenSource? _cts;

    /// <summary>
    /// Invoked on the pipe-listener thread when a second launch requests that
    /// the window be shown. Set by <see cref="App"/> once the main window is
    /// created. Implementations must marshal onto the UI thread (e.g. via
    /// <c>Dispatcher.UIThread.Post</c>).
    /// </summary>
    public static Action? OnShowRequested;

    /// <summary>Bring the existing window to the front.</summary>
    public const string CommandShow = "SHOW";

    /// <summary>
    /// Remove the login account from this machine: wipe every locally stored row,
    /// stop collecting and drop the UI back to the sign-in page.
    ///
    /// The RUNNING instance performs the removal — it owns the SQLite connection and
    /// the GUI, so it can empty the tables and re-render the sign-in page in place
    /// without a restart (a restart would have to race auto-start / systemd
    /// <c>Restart=always</c> for the mutex). The process that SENDS this command
    /// (<c>client --remove-account</c>) must exit afterwards and must NOT start a
    /// second tracker.
    /// </summary>
    public const string CommandRemoveAccount = "REMOVE_ACCOUNT";

    /// <summary>
    /// Protected uninstall with a server-issued removal code. Payload on the wire is
    /// <c>PROTECTED_UNINSTALL:&lt;raw-code&gt;</c>. Prefer <see cref="CommandProtectedUninstallGo"/>
    /// after the CLI has already verified (avoids double-consume → 401).
    /// </summary>
    public const string CommandProtectedUninstall = "PROTECTED_UNINSTALL";

    /// <summary>
    /// CLI already approved the removal code — running instance runs
    /// stop→sync→status→wipe→DLP teardown without verifying again.
    /// </summary>
    public const string CommandProtectedUninstallGo = "PROTECTED_UNINSTALL_GO";

    /// <summary>
    /// Invoked on the pipe-listener thread when a second launch asks to remove the
    /// local account. Two owners, mirroring the two GUI states: while the GUI is
    /// still headless this is handled in <c>Program.cs</c> (create the GUI, then let
    /// its boot consume the pending request) and once the GUI exists
    /// <c>App.axaml.cs</c> takes it over (show the window, then reset it to the
    /// sign-in page). Implementations must marshal onto the UI thread.
    /// </summary>
    public static Action? OnRemoveAccountRequested;

    /// <summary>
    /// Invoked with the raw removal code when a second launch sends
    /// <see cref="CommandProtectedUninstall"/>. Empty string means
    /// <see cref="CommandProtectedUninstallGo"/> (already approved).
    /// </summary>
    public static Action<string>? OnProtectedUninstallRequested;

    /// <summary>
    /// Start the named-pipe server on the primary (mutex-owning) instance.
    /// Loops forever, accepting one connection at a time, until
    /// <see cref="StopServer"/> is called.
    /// </summary>
    public static void StartServer()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(_cts.Token));
    }

    private static async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // maxNumberOfServerInstances = 1: only the primary instance
                // should ever own this pipe.
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(ct);
                if (string.Equals(line, CommandShow, StringComparison.Ordinal))
                {
                    try { OnShowRequested?.Invoke(); }
                    catch { /* never let a UI callback error kill the listener */ }
                }
                else if (string.Equals(line, CommandRemoveAccount, StringComparison.Ordinal))
                {
                    try { OnRemoveAccountRequested?.Invoke(); }
                    catch { /* never let a UI callback error kill the listener */ }
                }
                else if (string.Equals(line, CommandProtectedUninstallGo, StringComparison.Ordinal))
                {
                    // CLI already verified — empty code ⇒ codeAlreadyVerified path.
                    try { OnProtectedUninstallRequested?.Invoke(""); }
                    catch { /* never let a UI callback error kill the listener */ }
                }
                else if (line is not null &&
                         line.StartsWith(CommandProtectedUninstall + ":", StringComparison.Ordinal))
                {
                    var code = line[(CommandProtectedUninstall.Length + 1)..].Trim();
                    try { OnProtectedUninstallRequested?.Invoke(code); }
                    catch { /* never let a UI callback error kill the listener */ }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Transient pipe error (e.g. a client connected then
                // disappeared). Recreate the server and keep listening.
                // Yield briefly to avoid a tight loop on persistent failures.
                try { await Task.Delay(250, ct); } catch { break; }
            }
            finally
            {
                try { server?.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Stop the pipe server. Called on application shutdown.
    /// </summary>
    public static void StopServer()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    /// <summary>
    /// Attempt to signal an already-running instance to show its window
    /// (<paramref name="command"/> = <see cref="CommandShow"/>) or to remove the
    /// local account (<see cref="CommandRemoveAccount"/>).
    /// Called by a second launch that failed to acquire the global mutex.
    /// Returns true if the signal was delivered.
    /// </summary>
    public static bool SignalExistingInstance(string command = CommandShow)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.None);

            // 2s timeout: the running instance should already be listening.
            client.Connect(2000);

            using var writer = new StreamWriter(client);
            writer.WriteLine(command);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
