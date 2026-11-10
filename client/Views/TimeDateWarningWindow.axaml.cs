using Avalonia.Controls;
using client.Services.TimeDateWarning;
using client.ViewModels;

namespace client.Views;

public partial class TimeDateWarningWindow : Window
{
    public TimeDateWarningWindow()
    {
        InitializeComponent();
        DataContext = new TimeDateWarningViewModel();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // × / Alt+F4 counts as dismiss so the hourly worker honors cooldown.
        try { TimeDateWarningState.MarkDismissed(); }
        catch { /* ignore */ }
    }
}
