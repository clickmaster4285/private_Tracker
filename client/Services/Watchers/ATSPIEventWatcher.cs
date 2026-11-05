using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;
using client.Core.DesktopEventBus;

namespace client.Services.Watchers;

public class ATSPIEventWatcher : IObservableEventSource
{
    private readonly ILogger<ATSPIEventWatcher> _logger;
    private DBusConnection? _connection;
    private bool _isActive;
    private IDisposable? _subscription;

    public string SourceName => "atspi";
    public bool IsActive => _isActive;

    public event EventHandler<RawDesktopEvent>? EventRaised;

    private const string AtSpiRegistryPath = "/org/a11y/atspi/registry";
    private const string AtSpiRegistryInterface = "org.a11y.atspi.Registry";

    private static string ReadEventName(Message msg, object? state)
    {
        var reader = msg.GetBodyReader();
        return reader.ReadString();
    }

    private static readonly MessageValueReader<string> EventNameReader = ReadEventName;

    public ATSPIEventWatcher(ILogger<ATSPIEventWatcher> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_isActive) return;

        try
        {
            _connection = new DBusConnection(DBusAddress.Session!);
            await _connection.ConnectAsync();

            await RegisterEventAsync("focus:", ct);
            await RegisterEventAsync("window:", ct);

            _subscription = await _connection.WatchSignalAsync<string>(
                sender: null,
                path: AtSpiRegistryPath,
                @interface: AtSpiRegistryInterface,
                signal: "AccessibleEvent",
                reader: EventNameReader,
                handler: OnAccessibleEvent,
                flags: ObserverFlags.None,
                emitOnCapturedContext: false,
                state: null);

            _isActive = true;
            _logger.LogInformation("AT-SPI event watcher started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start AT-SPI event watcher");
            _isActive = false;
        }
    }

    private async Task RegisterEventAsync(string eventName, CancellationToken ct)
    {
        try
        {
            var writer = _connection!.GetMessageWriter();
            writer.WriteMethodCallHeader(
                path: AtSpiRegistryPath,
                @interface: AtSpiRegistryInterface,
                member: "RegisterEvent",
                signature: "s",
                destination: "org.a11y.atspi.Registry",
                flags: MessageFlags.None);
            writer.WriteString(eventName);
            var msg = writer.CreateMessage();
            await _connection.CallMethodAsync(msg).WaitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "RegisterEvent({Name}) failed", eventName);
        }
    }

    private void OnAccessibleEvent(Notification<string> notification)
    {
        if (!_isActive || notification.IsCompletion) return;

        try
        {
            var eventName = notification.Value;

            if (!eventName.StartsWith("focus:", StringComparison.Ordinal) &&
                !eventName.StartsWith("window:", StringComparison.Ordinal))
                return;

            var (appName, windowTitle, currentPath) = DetectForegroundFileManager();

            if (string.IsNullOrEmpty(appName) || !DesktopEventValidator.IsFileManager(appName))
                return;

            var raw = new RawDesktopEvent
            {
                Source = "atspi",
                EventType = eventName switch
                {
                    string s when s.StartsWith("focus:") => "focus",
                    "window:activate" => "window:activate",
                    "window:deactivate" => "window:deactivate",
                    _ => eventName,
                },
                AppName = appName,
                WindowTitle = windowTitle,
                CurrentPath = currentPath,
                Timestamp = DateTime.UtcNow,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    rawEvent = eventName,
                }),
            };

            EventRaised?.Invoke(this, raw);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error processing accessible event");
        }
    }

    private static (string? appName, string? windowTitle, string? currentPath) DetectForegroundFileManager()
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return (null, null, null);

            var activePid = GetActiveWindowPid();
            if (activePid == null) return (null, null, null);

            var process = System.Diagnostics.Process.GetProcessById(activePid.Value);
            var appName = process.ProcessName;

            if (!DesktopEventValidator.IsFileManager(appName))
            {
                var cmdline = ReadCmdline(activePid.Value);
                if (!string.IsNullOrEmpty(cmdline) && DesktopEventValidator.IsFileManager(cmdline))
                    appName = cmdline;
                else
                    return (null, null, null);
            }

            var title = process.MainWindowTitle;
            if (string.IsNullOrEmpty(title)) title = appName;

            string? cwd = null;
            if (OperatingSystem.IsLinux())
            {
                var cwdLink = $"/proc/{activePid}/cwd";
                if (File.Exists(cwdLink))
                {
                    try
                    {
                        cwd = File.ResolveLinkTarget(cwdLink, true)?.FullName;
                    }
                    catch { }
                }
            }

            return (appName, title, cwd);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static int? GetActiveWindowPid()
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return null;

            var xdotool = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "xdotool",
                    Arguments = "getactivewindow getwindowpid",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            xdotool.Start();
            var output = xdotool.StandardOutput.ReadToEnd();
            xdotool.WaitForExit(2000);
            if (xdotool.ExitCode == 0 && int.TryParse(output.Trim(), out var pid))
                return pid;
        }
        catch { }

        try
        {
            var processes = System.Diagnostics.Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    var cmdline = ReadCmdline(p.Id);
                    if (!string.IsNullOrEmpty(cmdline) && DesktopEventValidator.IsFileManager(cmdline))
                        return p.Id;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    private static string? ReadCmdline(int pid)
    {
        try
        {
            var cmdlinePath = $"/proc/{pid}/cmdline";
            if (!File.Exists(cmdlinePath)) return null;
            var bytes = File.ReadAllBytes(cmdlinePath);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            var parts = text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : null;
        }
        catch
        {
            return null;
        }
    }

    public void Stop()
    {
        _isActive = false;
        _subscription?.Dispose();
        _subscription = null;
        if (_connection != null)
        {
            _connection.Dispose();
            _connection = null;
        }
        _logger.LogInformation("AT-SPI event watcher stopped");
    }
}
