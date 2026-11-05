using System.Diagnostics;
using Avalonia.Threading;
using client.Core;
using client.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace client.ViewModels;

/// <summary>
/// Offline Roaming Start/Stop modal. Writes <see cref="LocalRoamingControl"/> and
/// signals the tracker via <see cref="SingleInstanceService"/> — no DLP service / server required.
/// </summary>
public sealed class TrackingControlViewModel : ObservableObject
{
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _tick;
    private long _elapsedActiveSec;
    private bool _busy;
    private bool _running;

    private string _elapsedText = "00:00:00";
    private string _elapsedHmText = "00:00";
    private string _elapsedSecText = ":00";
    private bool _canStart = true;
    private bool _canStop;
    private string? _errorText;

    public TrackingControlViewModel()
    {
        StartTrackingCommand = new AsyncRelayCommand(StartTrackingAsync, () => CanStart && !_busy);
        StopTrackingCommand = new AsyncRelayCommand(StopTrackingAsync, () => CanStop && !_busy);
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => RefreshFromLocal();
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) =>
        {
            // Working timer: grows only while running; freezes when stopped; midnight → 0.
            // Always recompute from disk (and bump +1s if running) so the UI never freezes
            // after hide-to-tray / focus loss.
            try
            {
                ApplyState(LocalRoamingControl.ReadOrDefault());
            }
            catch
            {
                if (_running)
                {
                    _elapsedActiveSec++;
                    FormatElapsed();
                }
            }
        };
    }

    public IAsyncRelayCommand StartTrackingCommand { get; }
    public IAsyncRelayCommand StopTrackingCommand { get; }

    public event Action? RequestExitFixed;

    public string ElapsedText
    {
        get => _elapsedText;
        set => SetProperty(ref _elapsedText, value);
    }

    /// <summary>Hours:minutes portion of the working timer (larger type).</summary>
    public string ElapsedHmText
    {
        get => _elapsedHmText;
        set => SetProperty(ref _elapsedHmText, value);
    }

    /// <summary>Seconds portion including leading colon (smaller type).</summary>
    public string ElapsedSecText
    {
        get => _elapsedSecText;
        set => SetProperty(ref _elapsedSecText, value);
    }

    public bool CanStart
    {
        get => _canStart;
        set
        {
            if (SetProperty(ref _canStart, value))
            {
                StartTrackingCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(StartOpacity));
            }
        }
    }

    public bool CanStop
    {
        get => _canStop;
        set
        {
            if (SetProperty(ref _canStop, value))
            {
                StopTrackingCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(StopOpacity));
            }
        }
    }

    public double StartOpacity => CanStart && !_busy ? 1.0 : 0.45;
    public double StopOpacity => CanStop && !_busy ? 1.0 : 0.45;

    public string? ErrorText
    {
        get => _errorText;
        set
        {
            if (SetProperty(ref _errorText, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public void Start()
    {
        // Modal is Roaming-only; exit if effective mode flipped back to Fixed.
        if (IsEffectiveFixed())
        {
            RequestExitFixed?.Invoke();
            return;
        }

        RefreshFromLocal();
        _poll.Start();
        _tick.Start();
    }

    public void StopTimers()
    {
        _poll.Stop();
        _tick.Stop();
    }

    private async Task StartTrackingAsync()
    {
        if (_busy || !CanStart) return;
        _busy = true;
        NotifyBusy();
        ErrorText = null;
        try
        {
            if (IsEffectiveFixed())
            {
                RequestExitFixed?.Invoke();
                return;
            }

            var state = LocalRoamingControl.WriteStart();
            ApplyState(state);

            // Prefer an already-running tracker; otherwise start --background offline.
            if (!SingleInstanceService.SignalExistingInstance(SingleInstanceService.CommandTrackingStart))
                LaunchBackgroundTracker();

            await Task.Delay(200);
            RefreshFromLocal();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            _busy = false;
            NotifyBusy();
        }
    }

    private async Task StopTrackingAsync()
    {
        if (_busy || !CanStop) return;
        _busy = true;
        NotifyBusy();
        ErrorText = null;
        try
        {
            var state = LocalRoamingControl.WriteStop();
            ApplyState(state);

            SingleInstanceService.SignalExistingInstance(SingleInstanceService.CommandTrackingStop);

            await Task.Delay(200);
            RefreshFromLocal();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            _busy = false;
            NotifyBusy();
        }
    }

    private void NotifyBusy()
    {
        StartTrackingCommand.NotifyCanExecuteChanged();
        StopTrackingCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(StartOpacity));
        OnPropertyChanged(nameof(StopOpacity));
    }

    private void RefreshFromLocal()
    {
        try
        {
            if (IsEffectiveFixed())
            {
                RequestExitFixed?.Invoke();
                return;
            }

            ApplyState(LocalRoamingControl.ReadOrDefault());
            ErrorText = null;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private void ApplyState(LocalRoamingControl.State state)
    {
        _elapsedActiveSec = LocalRoamingControl.ElapsedActiveSec(state);
        _running = string.Equals(state.DesiredState, "running", StringComparison.OrdinalIgnoreCase);
        CanStart = !_running;
        CanStop = _running;
        FormatElapsed();
    }

    private void FormatElapsed()
    {
        var sec = Math.Max(0, _elapsedActiveSec);
        var hours = sec / 3600;
        var minutes = (sec % 3600) / 60;
        var seconds = sec % 60;
        ElapsedHmText = $"{hours:D2}:{minutes:D2}";
        ElapsedSecText = $":{seconds:D2}";
        ElapsedText = $"{ElapsedHmText}{ElapsedSecText}";
    }

    /// <summary>
    /// Fixed only when control.json / pipe explicitly says fixed.
    /// Missing service ⇒ allow offline Roaming modal (local-roaming.json drives Start/Stop).
    /// </summary>
    private static bool IsEffectiveFixed()
    {
        try
        {
            if (TryReadProgramDataMode(out var mode))
                return !string.Equals(mode, "roaming", StringComparison.OrdinalIgnoreCase);

            // No ProgramData control yet — if user already used offline Start/Stop, stay Roaming.
            if (File.Exists(LocalRoamingControl.FilePath))
                return false;

            var snap = TrackingModeResolver.ResolveAsync().GetAwaiter().GetResult();
            // Fail-safe Fixed from missing pipe/file must not kill the offline modal.
            if (snap.Generation == 0 &&
                string.Equals(snap.Mode, "fixed", StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(TrackingControlPaths.ControlJsonPath))
                return false;

            return TrackingModeResolver.IsFixed(snap);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadProgramDataMode(out string mode)
    {
        mode = OperatingSystem.IsWindows() ? "fixed" : "roaming";
        try
        {
            var path = TrackingControlPaths.ControlJsonPath;
            if (!File.Exists(path)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            mode = (root.TryGetProperty("mode", out var m) || root.TryGetProperty("trackingMode", out m)
                ? m.GetString() : mode) ?? mode;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void LaunchBackgroundTracker()
    {
        try
        {
            var exe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exe)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--background",
                UseShellExecute = false,
            });
        }
        catch
        {
            /* Start still recorded locally; watchdog / next launch can pick up */
        }
    }
}
