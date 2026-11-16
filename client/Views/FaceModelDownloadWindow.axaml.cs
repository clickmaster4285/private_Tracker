using Avalonia.Controls;
using Avalonia.Threading;
using client.Services.FaceVerify;
using client.ViewModels;

namespace client.Views;

public partial class FaceModelDownloadWindow : Window
{
    private readonly FaceModelDownloadViewModel _vm = new();
    private Action<FaceDownloadProgress>? _handler;

    public FaceModelDownloadWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Closing += OnClosing;
    }

    public void Attach()
    {
        _handler = p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _vm.Apply(p);
                if (p.IsComplete)
                {
                    // Brief pause so the user sees 100%, then close.
                    _ = CloseAfterDelayAsync();
                }
            });
        };
        FaceDownloadStatus.Changed += _handler;
        if (FaceDownloadStatus.Current is { } cur)
            _vm.Apply(cur);
    }

    private async Task CloseAfterDelayAsync()
    {
        await Task.Delay(600);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try { Close(); } catch { /* already closed */ }
        });
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_vm.CanClose)
        {
            e.Cancel = true;
            return;
        }
        if (_handler != null)
        {
            FaceDownloadStatus.Changed -= _handler;
            _handler = null;
        }
    }
}
