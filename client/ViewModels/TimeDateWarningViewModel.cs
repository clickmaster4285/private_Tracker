using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client.Core;
using client.Services.TimeDateWarning;

namespace client.ViewModels;

public partial class TimeDateWarningViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "Date & time may be incorrect";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _appName = AppInfo.DisplayName;

    public TimeDateWarningViewModel()
    {
        LoadFromState();
    }

    private void LoadFromState()
    {
        var state = TimeDateWarningState.TryRead();
        if (state is null)
        {
            Summary = "This PC’s clock or timezone does not match the expected time for your location.";
            Details = "No diagnostic details were available. Open Date & Time settings and turn on automatic time.";
            return;
        }

        var reasons = state.Reasons ?? Array.Empty<string>();
        var parts = new List<string>();
        if (reasons.Contains("clock_skew"))
        {
            var abs = Math.Abs(state.SkewSeconds);
            var dir = state.SkewSeconds > 0 ? "behind" : "ahead of";
            parts.Add($"This PC’s clock is about {FormatSkew(abs)} {dir} the server.");
        }
        if (reasons.Contains("timezone_mismatch"))
            parts.Add("This PC’s timezone does not match the timezone for your location.");

        Summary = parts.Count > 0
            ? string.Join(" ", parts)
            : "This PC’s clock or timezone looks incorrect.";

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(state.OsLocalNow))
            sb.AppendLine($"PC local time:  {state.OsLocalNow}");
        if (!string.IsNullOrWhiteSpace(state.ExpectedLocalNow))
            sb.AppendLine($"Expected local: {state.ExpectedLocalNow}");
        if (!string.IsNullOrWhiteSpace(state.OsTimeZoneId))
            sb.AppendLine($"PC timezone:    {state.OsTimeZoneId}");
        if (!string.IsNullOrWhiteSpace(state.ExpectedTimeZoneId))
            sb.AppendLine($"Expected TZ:    {state.ExpectedTimeZoneId}" +
                          (string.IsNullOrWhiteSpace(state.LocationSource)
                              ? ""
                              : $" ({state.LocationSource})"));
        sb.AppendLine($"Clock skew:     {state.SkewSeconds:0} s");
        Details = sb.ToString().TrimEnd();
    }

    private static string FormatSkew(double absSeconds)
    {
        if (absSeconds < 120) return $"{absSeconds:0} seconds";
        var mins = absSeconds / 60.0;
        if (mins < 120) return $"{mins:0} minutes";
        return $"{mins / 60.0:0.#} hours";
    }

    [RelayCommand]
    private void OpenDateTimeSettings()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:dateandtime",
                    UseShellExecute = true,
                });
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                foreach (var (file, args) in new[]
                         {
                             ("gnome-control-center", "datetime"),
                             ("systemsettings5", "clock"),
                             ("timedatectl", "status"),
                         })
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = file,
                            Arguments = args,
                            UseShellExecute = false,
                        });
                        return;
                    }
                    catch { /* try next */ }
                }
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = "x-apple.systempreferences:com.apple.preference.datetime",
                    UseShellExecute = false,
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[timedatewarning] Open settings failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        try { TimeDateWarningState.MarkDismissed(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[timedatewarning] Dismiss stamp failed: {ex.Message}");
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Environment.Exit(0);
        }
    }
}
