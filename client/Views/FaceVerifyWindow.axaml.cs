using Avalonia.Controls;
using client.ViewModels;

namespace client.Views;

public partial class FaceVerifyWindow : Window
{
    private FaceVerifyViewModel? _vm;

    public FaceVerifyWindow()
    {
        InitializeComponent();
        _vm = new FaceVerifyViewModel();
        DataContext = _vm;
        _vm.RequestClose += () =>
        {
            try { Close(); } catch { /* ignore */ }
        };

        Opened += async (_, _) =>
        {
            WindowState = WindowState.Maximized;
            Topmost = true;
            await _vm.StartAsync();
        };

        Closing += (_, e) =>
        {
            if (_vm is { AllowClose: false })
            {
                e.Cancel = true;
                WindowState = WindowState.Maximized;
                Topmost = true;
            }
        };

        Closed += (_, _) =>
        {
            _vm?.Dispose();
            _vm = null;
        };
    }
}
