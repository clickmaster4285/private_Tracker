using System.Runtime.InteropServices;
using System.Text;

namespace client.Services;

/// <summary>
/// WinExe has no console by default — AttachConsole alone leaves .NET's
/// Console.Out disconnected from PowerShell. Rebind CONOUT$/CONIN$, show an
/// apt/npm-style spinner with live phase labels, then pause so the final line
/// is readable before the process exits.
/// </summary>
public static class CliConsole
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private static bool _ready;

    public static void Ensure()
    {
        if (_ready) return;
        if (!OperatingSystem.IsWindows())
        {
            _ready = true;
            return;
        }

        try
        {
            // Exe subsystem already inherits PowerShell's console — do NOT rebind
            // CONOUT$ (that steals/breaks the inherited stdout pipes).
            // Only attach/alloc + rebind when we started with no console (legacy).
            if (GetConsoleWindow() == IntPtr.Zero)
            {
                if (!AttachConsole(AttachParentProcess))
                    AllocConsole();
                RebindStdio();
            }

            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            try
            {
                AllocConsole();
                RebindStdio();
            }
            catch { /* ignore */ }
        }

        _ready = true;
    }

    /// <summary>
    /// Drop the console window for normal GUI / --background launches (OutputType=Exe).
    /// Must run before Avalonia so employees never see a black console flash.
    /// </summary>
    public static void DetachForGui()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero)
                ShowWindow(hwnd, SwHide);
            FreeConsole();
        }
        catch { /* ignore */ }
    }

    private static void RebindStdio()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        // Prefer CONOUT$/CONIN$ so AttachConsole actually receives WriteLine.
        Stream? stdout = null;
        Stream? stderr = null;
        Stream? stdin = null;
        try
        {
            stdout = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write, FileShare.Write);
            stderr = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write, FileShare.Write);
            stdin = new FileStream("CONIN$", FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            stdout = Console.OpenStandardOutput();
            stderr = Console.OpenStandardError();
            stdin = Console.OpenStandardInput();
        }

        Console.SetOut(new StreamWriter(stdout, utf8) { AutoFlush = true });
        Console.SetError(new StreamWriter(stderr, utf8) { AutoFlush = true });
        Console.SetIn(new StreamReader(stdin, utf8));
        Console.OutputEncoding = utf8;
    }

    /// <summary>
    /// Run work under a spinner. <paramref name="work"/> may call the progress
    /// action to change the phase label mid-flight (e.g. verify → write gate → unlock).
    /// </summary>
    public static async Task<bool> RunWithSpinnerAsync(
        string label,
        Func<CancellationToken, Task<bool>> work,
        string successMessage,
        string failureMessage,
        CancellationToken ct = default)
    {
        return await RunWithSpinnerAsync(
            label,
            (_, token) => work(token),
            successMessage,
            failureMessage,
            ct).ConfigureAwait(false);
    }

    public static async Task<bool> RunWithSpinnerAsync(
        string initialLabel,
        Func<Action<string>, CancellationToken, Task<bool>> work,
        string successMessage,
        string failureMessage,
        CancellationToken ct = default)
    {
        Ensure();
        using var spinner = new Spinner(initialLabel);
        spinner.Start();
        bool ok;
        try
        {
            ok = await work(spinner.SetLabel, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            spinner.Stop(ok: false);
            Console.Error.WriteLine($"{failureMessage} — timed out");
            return false;
        }
        catch (Exception ex)
        {
            spinner.Stop(ok: false);
            Console.Error.WriteLine($"{failureMessage} — {ex.Message}");
            return false;
        }

        spinner.Stop(ok);
        if (ok)
            Console.WriteLine(successMessage);
        else
            Console.Error.WriteLine(failureMessage);
        return ok;
    }

    /// <summary>Keep the host console open until Enter (or --no-pause).</summary>
    public static void PauseBeforeExit(string[] args)
    {
        Ensure();
        if (args.Any(a => string.Equals(a, "--no-pause", StringComparison.OrdinalIgnoreCase)))
            return;

        try
        {
            if (Console.IsInputRedirected) return;
            Console.WriteLine();
            Console.Write("Press Enter to close...");
            Console.Out.Flush();
            Console.ReadLine();
        }
        catch
        {
            // Non-interactive / no console — exit normally.
        }
    }

    private sealed class Spinner : IDisposable
    {
        private static readonly char[] Frames = ['|', '/', '-', '\\'];
        private readonly object _gate = new();
        private string _label;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _frame;

        public Spinner(string label) => _label = label;

        public void SetLabel(string label)
        {
            lock (_gate) _label = label;
        }

        public void Start()
        {
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    string label;
                    lock (_gate) label = _label;
                    var ch = Frames[_frame++ % Frames.Length];
                    try
                    {
                        var line = $"  {ch} {label}...";
                        var pad = Math.Max(0, 72 - line.Length);
                        Console.Write($"\r{line}{new string(' ', pad)}");
                        Console.Out.Flush();
                    }
                    catch { /* ignore */ }

                    try { await Task.Delay(100, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        public void Stop(bool ok)
        {
            string label;
            lock (_gate) label = _label;
            try { _cts.Cancel(); } catch { /* ignore */ }
            try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }

            var clear = new string(' ', 80);
            try
            {
                Console.Write($"\r{clear}\r");
                Console.WriteLine(ok ? $"  [ok] {label}" : $"  [fail] {label}");
                Console.Out.Flush();
            }
            catch
            {
                Console.WriteLine(ok ? $"[ok] {label}" : $"[fail] {label}");
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    private const int SwHide = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
