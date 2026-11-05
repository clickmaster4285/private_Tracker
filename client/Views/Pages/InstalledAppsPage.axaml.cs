using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using client.ViewModels;

namespace client.Views.Pages;

/// <summary>
/// Page 6 — installed applications. Bound to <see cref="ViewModels.InstalledAppsViewModel"/>.
/// While the page is on screen a lightweight DispatcherTimer re-reads the stored SQLite
/// inventory every few seconds (PollAsync — no OS scan), so installs/uninstalls that the
/// background watcher recorded appear live without clicking Rescan or re-navigating.
/// </summary>
public partial class InstalledAppsPage : UserControl
{
    private readonly DispatcherTimer _liveTimer;

    public InstalledAppsPage()
    {
        InitializeComponent();

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _liveTimer.Tick += async (_, _) =>
        {
            if (DataContext is InstalledAppsViewModel vm)
                await vm.PollAsync();
        };

        // Start/stop the live poll when the page is shown/hidden (nav rail switches pages
        // by toggling IsVisible, so this fires on every navigation in/out).
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible)
            {
                _liveTimer.Start();
                // Immediate catch-up on first show (don't wait 5s for the first tick).
                if (DataContext is InstalledAppsViewModel vm)
                    _ = vm.PollAsync();
            }
            else
            {
                _liveTimer.Stop();
            }
        };
    }
}
