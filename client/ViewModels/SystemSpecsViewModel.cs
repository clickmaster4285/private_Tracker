using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.ViewModels;

/// <summary>
/// Page 5 — System Specifications. Renders the newest hardware snapshot the
/// collector persisted, plus its storage children, the current network identity
/// and everything still physically plugged in. Read-only.
/// </summary>
public partial class SystemSpecsViewModel : ViewModelBase
{
    private readonly ILogStore _store;

    public SystemSpecsViewModel(ILogStore store) => _store = store;

    // ─── Machine ───

    [ObservableProperty] private string _hostname = "—";
    [ObservableProperty] private string _macAddress = "—";
    [ObservableProperty] private string _osName = "—";
    [ObservableProperty] private string _osVersion = "—";
    [ObservableProperty] private string _collectedAt = "—";

    // ─── Compute ───

    [ObservableProperty] private string _cpuModel = "—";
    [ObservableProperty] private string _cpuCores = "—";
    [ObservableProperty] private string _ramTotal = "—";
    [ObservableProperty] private string _gpuModel = "—";
    [ObservableProperty] private string _gpuVram = "—";

    // ─── Network ───

    [ObservableProperty] private string _privateIp = "—";
    [ObservableProperty] private string _publicIp = "—";
    [ObservableProperty] private string _networkInterface = "—";
    [ObservableProperty] private string _networkSince = "—";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _lastRefreshed = string.Empty;

    public ObservableCollection<StorageDevice> StorageDevices { get; } = new();
    public ObservableCollection<HardwareDevice> AttachedDevices { get; } = new();

    public bool HasStorage => StorageDevices.Count > 0;
    public bool HasAttached => AttachedDevices.Count > 0;

    /// <summary>True until the collector has written its first hardware row.</summary>
    [ObservableProperty] private bool _hasSnapshot;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var hw = await _store.GetLastDeviceHardwareInfoAsync(ct);
            HasSnapshot = hw != null;
            if (hw != null)
            {
                Hostname = DashboardViewModel.Or(hw.Hostname);
                MacAddress = DashboardViewModel.Or(hw.MacAddress);
                OsName = DashboardViewModel.Or(hw.OsName);
                OsVersion = DashboardViewModel.Or(hw.OsVersion);
                CollectedAt = hw.CollectedAt == default
                    ? "—"
                    : $"{hw.CollectedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {DashboardViewModel.FormatAgo(DateTime.UtcNow - hw.CollectedAt)}";

                CpuModel = DashboardViewModel.Or(hw.CpuModel);
                CpuCores = hw.CpuCores > 0 ? $"{hw.CpuCores} cores" : "—";
                RamTotal = DashboardViewModel.FormatMb(hw.RamTotalMb);
                GpuModel = DashboardViewModel.Or(hw.GpuModel);
                GpuVram = hw.GpuVramMb > 0 ? DashboardViewModel.FormatMb(hw.GpuVramMb) : "—";
            }

            var net = await _store.GetLastNetworkInfoAsync(ct);
            if (net != null)
            {
                PrivateIp = DashboardViewModel.Or(net.PrivateIp);
                PublicIp = DashboardViewModel.Or(net.PublicIp);
                NetworkInterface = DashboardViewModel.Or(net.NetworkInterfaceName);
                NetworkSince = net.FirstSeenAt.HasValue
                    ? $"since {net.FirstSeenAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                    : "—";
            }

            StorageDevices.Clear();
            foreach (var d in await _store.GetLatestStorageDevicesAsync(ct))
                StorageDevices.Add(d);
            OnPropertyChanged(nameof(HasStorage));

            AttachedDevices.Clear();
            var attached = await _store.GetOpenHardwareDevicesAsync(ct);
            foreach (var d in attached.OrderBy(d => d.DeviceClass).ThenBy(d => d.Product))
                AttachedDevices.Add(d);
            OnPropertyChanged(nameof(HasAttached));

            LastRefreshed = $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-refresh.
        }
        finally
        {
            IsBusy = false;
        }
    }
}
