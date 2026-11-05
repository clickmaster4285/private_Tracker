using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;
using client.Services;

namespace client.ViewModels;

/// <summary>
/// Page 4 — Operational Overview. Aggregates what the collectors already persist
/// (hardware snapshot, network, attached devices, employee identity) into the
/// at-a-glance tiles the dashboard shows. Read-only: nothing here writes to the DB.
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly LogCollectorService _logCollector;

    /// <summary>Self-update state — same singleton the top bar binds, so the banner and the
    /// top-bar buttons always agree.</summary>
    public AppUpdateService Update { get; }

    public DashboardViewModel(
        ILogStore store,
        AppConfig config,
        LogCollectorService logCollector,
        AppUpdateService updateService)
    {
        _store = store;
        _config = config;
        _logCollector = logCollector;
        Update = updateService;
    }

    // ─── Identity ───

    [ObservableProperty] private string _employeeName = string.Empty;
    [ObservableProperty] private string _employeeId = string.Empty;
    [ObservableProperty] private string _employeeDepartment = string.Empty;
    [ObservableProperty] private string _employeeEmail = string.Empty;
    [ObservableProperty] private string _employeeShift = string.Empty;
    [ObservableProperty] private string _employeeAvatar = string.Empty;
    [ObservableProperty] private string _employeeAvatarColor = "#8B5CF6";

    // ─── Status tiles ───

    [ObservableProperty] private bool _isTracking;
    [ObservableProperty] private string _trackingLabel = "Inactive";
    [ObservableProperty] private string _sessionUptime = "—";
    [ObservableProperty] private string _lastHeartbeat = "—";
    [ObservableProperty] private string _syncStateLabel = "Not synced yet";
    [ObservableProperty] private bool _isSyncHealthy;

    [ObservableProperty] private int _installedAppCount;
    [ObservableProperty] private int _attachedDeviceCount;
    [ObservableProperty] private int _storageDeviceCount;

    // ─── System snapshot ───

    [ObservableProperty] private string _hostname = "—";
    [ObservableProperty] private string _osSummary = "—";
    [ObservableProperty] private string _cpuSummary = "—";
    [ObservableProperty] private string _ramSummary = "—";
    [ObservableProperty] private string _privateIp = "—";
    [ObservableProperty] private string _publicIp = "—";
    [ObservableProperty] private string _networkInterface = "—";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _lastRefreshed = string.Empty;

    /// <summary>Devices currently plugged in (unplugged_at IS NULL).</summary>
    public ObservableCollection<HardwareDevice> AttachedDevices { get; } = new();

    public bool HasAttachedDevices => AttachedDevices.Count > 0;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var employee = await _store.GetEmployeeInfoAsync(ct);
            if (employee != null)
            {
                EmployeeName = employee.Name;
                EmployeeId = employee.EmployeeId;
                EmployeeDepartment = string.IsNullOrWhiteSpace(employee.Department) ? "—" : employee.Department;
                EmployeeEmail = string.IsNullOrWhiteSpace(employee.Email) ? "—" : employee.Email;
                EmployeeShift = string.IsNullOrWhiteSpace(employee.Shift) ? "—" : employee.Shift;
                EmployeeAvatar = string.IsNullOrWhiteSpace(employee.Avatar)
                    ? InitialsOf(employee.Name)
                    : employee.Avatar!;
                EmployeeAvatarColor = employee.AvatarColor ?? "#8B5CF6";
            }

            IsTracking = _logCollector.IsTrackingEnabled;
            TrackingLabel = IsTracking ? "Monitoring active" : "Monitoring paused";

            var uptime = DateTime.UtcNow - SessionInfo.StartedAt;
            SessionUptime = FormatDuration(uptime);

            var heartbeat = await _store.GetStatusAsync("last_heartbeat_at", ct);
            if (DateTime.TryParse(heartbeat, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var hb))
            {
                var age = DateTime.UtcNow - hb.ToUniversalTime();
                LastHeartbeat = FormatAgo(age);
                // The collector heartbeats once per collect interval; allow 3 missed
                // cycles (min 2 min) before calling the pipeline unhealthy.
                var tolerance = TimeSpan.FromSeconds(Math.Max(120, _config.CollectIntervalSec * 3));
                IsSyncHealthy = age <= tolerance;
                SyncStateLabel = IsSyncHealthy ? "Pipeline healthy" : "Pipeline stalled";
            }
            else
            {
                LastHeartbeat = "—";
                IsSyncHealthy = false;
                SyncStateLabel = "Awaiting first cycle";
            }

            var hardware = await _store.GetLastDeviceHardwareInfoAsync(ct);
            if (hardware != null)
            {
                Hostname = Or(hardware.Hostname);
                OsSummary = Join(hardware.OsName, hardware.OsVersion);
                CpuSummary = hardware.CpuCores > 0
                    ? $"{Or(hardware.CpuModel)} · {hardware.CpuCores} cores"
                    : Or(hardware.CpuModel);
                RamSummary = hardware.RamTotalMb > 0 ? FormatMb(hardware.RamTotalMb) : "—";
            }

            var network = await _store.GetLastNetworkInfoAsync(ct);
            if (network != null)
            {
                PrivateIp = Or(network.PrivateIp);
                PublicIp = Or(network.PublicIp);
                NetworkInterface = Or(network.NetworkInterfaceName);
            }

            var devices = await _store.GetOpenHardwareDevicesAsync(ct);
            AttachedDevices.Clear();
            foreach (var d in devices.OrderByDescending(d => d.PluggedAt).Take(8))
                AttachedDevices.Add(d);
            AttachedDeviceCount = devices.Count;
            OnPropertyChanged(nameof(HasAttachedDevices));

            StorageDeviceCount = (await _store.GetLatestStorageDevicesAsync(ct)).Count;

            // Read the stored inventory count straight from SQLite — the exact same
            // table the Installed Applications page renders (same empty-name filter), so
            // the tile and the page always agree with the DB and upstream. Only OPEN
            // install cycles count as installed software — closed rows are history.
            InstalledAppCount = (await _store.GetAllInstalledAppsAsync(ct))
                .Count(a => a.IsInstalled && !string.IsNullOrWhiteSpace(a.AppName));

            LastRefreshed = $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            // Page navigated away mid-refresh — nothing to report.
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string InitialsOf(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant();
    }

    internal static string Or(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v.Trim();

    internal static string Join(string? a, string? b)
    {
        var l = (a ?? string.Empty).Trim();
        var r = (b ?? string.Empty).Trim();
        if (l.Length == 0 && r.Length == 0) return "—";
        if (l.Length == 0) return r;
        if (r.Length == 0) return l;
        return $"{l} {r}";
    }

    internal static string FormatMb(long mb)
    {
        if (mb <= 0) return "—";
        if (mb >= 1024 * 1024) return $"{mb / 1024.0 / 1024.0:0.##} TB";
        if (mb >= 1024) return $"{mb / 1024.0:0.#} GB";
        return $"{mb} MB";
    }

    internal static string FormatDuration(TimeSpan t)
    {
        if (t.TotalSeconds < 0) t = TimeSpan.Zero;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalSeconds}s";
    }

    internal static string FormatAgo(TimeSpan age)
    {
        if (age.TotalSeconds < 10) return "just now";
        if (age.TotalMinutes < 1) return $"{(int)age.TotalSeconds}s ago";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
