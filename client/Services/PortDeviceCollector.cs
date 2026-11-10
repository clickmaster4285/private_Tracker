using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Hardware &amp; Port Control scanner. Enumerates every port / interface / device
/// on this machine (3 categories: external ports, internal interfaces, wireless/
/// virtual), writes the full inventory to SQLite (machine_ports), and lets
/// SyncService drain it to the server. The DlpService enforces block/allow.
///
/// Windows: Get-PnpDevice (the OS device tree). Other platforms: no-op for now.
/// </summary>
public class PortDeviceCollector : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly ILogger<PortDeviceCollector> _logger;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);

    public PortDeviceCollector(AppConfig config, ILogStore store, ILogger<PortDeviceCollector> logger)
    {
        _config = config;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
            return;
        }
        if (!_config.PortScanEnabled)
        {
            _logger.LogInformation("PortDeviceCollector disabled (ALPHA_PORT_SCAN_ENABLED=false)");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("PortDeviceCollector: no scanner for this platform yet");
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        _logger.LogInformation("PortDeviceCollector starting (Windows PnP port scan)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ports = await ScanWindowsAsync(stoppingToken);
                if (ports.Count > 0)
                {
                    await _store.ReplaceMachinePortScanAsync(ports, stoppingToken);
                    _logger.LogDebug("Port scan stored {Count} device(s)", ports.Count);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Port scan failed");
            }

            try { await Task.Delay(ScanInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Classes we treat as real ports/interfaces. Everything else (System, Processor,
    // Volume, Software*, Firmware…) is OS noise and skipped.
    private static readonly Dictionary<string, int> ClassCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        // 1 = external physical ports
        ["USB"] = 1, ["USBDevice"] = 1, ["Keyboard"] = 1, ["Mouse"] = 1, ["HIDClass"] = 1,
        ["Camera"] = 1, ["Image"] = 1, ["Media"] = 1, ["AudioEndpoint"] = 1,
        ["Monitor"] = 1, ["Printer"] = 1, ["WPD"] = 1, ["PortableDevice"] = 1,
        ["SmartCardReader"] = 1, ["Ports"] = 1, ["Biometric"] = 1, ["SCMDisk"] = 1,
        // 2 = internal interfaces
        ["DiskDrive"] = 2, ["SCSIAdapter"] = 2, ["HDC"] = 2, ["Display"] = 2, ["CDROM"] = 2,
        // 3 = wireless / virtual
        ["Net"] = 3, ["Bluetooth"] = 3, ["Modem"] = 3,
    };

    // Not individually blockable: video outputs, NICs (use Network Lockdown for
    // internet), + internal disks are alert-only.
    private static readonly HashSet<string> AlertOnlyClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Display", "Monitor",
        "Net", // never Disable-PnpDevice — kills path to the management server
    };

    private async Task<IReadOnlyList<MachinePort>> ScanWindowsAsync(CancellationToken ct)
    {
        var json = await RunPowerShellAsync(
            "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | " +
            "Select-Object Class, FriendlyName, Manufacturer, InstanceId, Status, ClassGuid | " +
            "ConvertTo-Json -Compress", ct);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<MachinePort>();

        var ports = new List<MachinePort>();
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : doc.RootElement.ValueKind == JsonValueKind.Object
                ? new[] { doc.RootElement }.AsEnumerable()
                : Enumerable.Empty<JsonElement>();

        foreach (var item in items)
        {
            var cls = Str(item, "Class");
            var instanceId = Str(item, "InstanceId");
            if (string.IsNullOrWhiteSpace(instanceId)) continue;
            if (!ClassCategory.TryGetValue(cls, out var category)) continue; // skip OS noise

            var name = Str(item, "FriendlyName");
            var bus = BusFromInstanceId(instanceId);
            var blockable = ComputeBlockable(cls, instanceId);

            ports.Add(new MachinePort
            {
                InstanceId = instanceId,
                Category = category,
                PnpClass = cls,
                ClassGuid = Str(item, "ClassGuid"),
                BusType = bus,
                FriendlyName = string.IsNullOrWhiteSpace(name) ? instanceId : name,
                Manufacturer = Str(item, "Manufacturer"),
                Present = true,
                Status = Str(item, "Status"),
                Blockable = blockable,
            });
        }
        return ports;
    }

    private static bool ComputeBlockable(string cls, string instanceId)
    {
        if (AlertOnlyClasses.Contains(cls)) return false; // video outputs
        // Internal (non-USB) disks are the system/data disks — never hard-block.
        if (cls.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase)
            && !instanceId.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)
            && !instanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static string BusFromInstanceId(string id)
    {
        if (id.StartsWith("NVME\\", StringComparison.OrdinalIgnoreCase)) return "NVMe";
        if (id.StartsWith("SCSI\\", StringComparison.OrdinalIgnoreCase)) return "SATA/SAS";
        if (id.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)) return "USB";
        if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) return "USB";
        if (id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) return "PCIe";
        if (id.StartsWith("BTH\\", StringComparison.OrdinalIgnoreCase)) return "Bluetooth";
        if (id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase)) return "ACPI";
        if (id.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)) return "Software";
        return "";
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private async Task<string> RunPowerShellAsync(string command, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -Command \"" + command + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            try { proc.WaitForExit(30_000); } catch { }
            return output;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PowerShell port probe failed");
            return "";
        }
    }
}
