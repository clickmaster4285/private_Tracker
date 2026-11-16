using CommunityToolkit.Mvvm.ComponentModel;
using client.Core;
using client.Services.FaceVerify;

namespace client.ViewModels;

public partial class FaceModelDownloadViewModel : ViewModelBase
{
    [ObservableProperty] private string _appName = AppInfo.DisplayName;
    [ObservableProperty] private string _title = "Downloading face models";
    [ObservableProperty] private string _statusText = "Preparing…";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private string _mbText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _canClose;
    [ObservableProperty] private bool _isFailed;

    public void Apply(FaceDownloadProgress p)
    {
        StatusText = string.IsNullOrWhiteSpace(p.Step) ? StatusText : p.Step;
        MbText = p.MbLine;
        EtaText = p.EtaText ?? "";
        if (p.Fraction is { } f)
        {
            ProgressValue = Math.Clamp(f, 0, 1);
            IsIndeterminate = false;
        }
        else
        {
            IsIndeterminate = true;
        }

        if (p.IsComplete)
        {
            Title = "Face models ready";
            StatusText = p.Step;
            ProgressValue = 1;
            IsIndeterminate = false;
            EtaText = "";
            CanClose = true;
            IsFailed = false;
        }
        else if (p.IsFailed)
        {
            Title = "Download failed";
            StatusText = p.Error ?? p.Step;
            CanClose = true;
            IsFailed = true;
        }
        else
        {
            CanClose = false;
            IsFailed = false;
        }
    }
}
