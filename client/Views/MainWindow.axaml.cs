using Avalonia.Controls;
using Avalonia.Interactivity;
using client.ViewModels;

namespace client.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        // Open at ~90% of the screen's working area by default.
        ResizeToNinetyPercent();

        // Play the boot/splash sequence once on open, then reveal the
        // login / setup / shell UI.
        if (DataContext is MainViewModel vm)
        {
            try
            {
                await vm.RunSplashSequenceAsync();
            }
            catch
            {
                // Never let a splash hiccup take down the window — always
                // hand off to the login / profile UI.
                vm.IsSplashVisible = false;
            }
        }
    }

    /// <summary>
    /// Sizes the window to 90% of the monitor's working area (the on-screen
    /// region excluding taskbars/docks). Falls back to the XAML Width/Height
    /// if no screen is reported yet.
    /// </summary>
    private void ResizeToNinetyPercent()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var work = screen.WorkingArea;
        Width = Math.Max(MinWidth, work.Width);
        Height = Math.Max(MinHeight, work.Height);
    }
}