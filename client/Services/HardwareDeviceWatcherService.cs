using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Local-only USB / peripheral / storage hotplug tracker (SQLite, no server sync yet).
///
/// Tracks when physical devices are plugged in / unplugged:
///   - udev monitor (subsystem=usb) gives real-time add/remove events,
///   - a periodic /sys enumeration backfills anything the monitor missed (devices that
///     were present at boot before this service started) and self-heals gaps,
///   - amixer polling detects analog audio jack plug/unplug for headsets/headphones.
///
/// Each plug-in writes a <c>hardware_devices</c> row (device_class/vendor/product/serial/
/// bus_path/device_node/plugged_at); the matching plug-out sets <c>unplugged_at</c>.
/// The open-row unique index (bus_path) keeps exactly one open row per physical slot.
/// </summary>
public class HardwareDeviceWatcherService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly ILogger<HardwareDeviceWatcherService> _logger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(5);

    public HardwareDeviceWatcherService(AppConfig config, ILogStore store, ILogger<HardwareDeviceWatcherService> logger)
    {
        _config = config;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("HardwareDeviceWatcherService parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!_config.HardwareDevicesEnabled)
        {
            _logger.LogInformation("Hardware device watcher disabled (enabled={Enabled})",
                _config.HardwareDevicesEnabled);
            return;
        }

        await _store.InitializeAsync(stoppingToken);

        // ── Windows: PnP device polling (2026-08-08) ──
        // Enumeration + present/absent diffing against the open rows. A 30s poll keeps
        // plug/unplug detection reasonably fresh without WM_DEVICECHANGE plumbing.
        if (OperatingSystem.IsWindows())
        {
            _logger.LogInformation("Hardware device watcher starting (Windows PnP polling)");

            try
            {
                await ReconcileWindowsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initial Windows hardware device enumeration failed");
            }

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    try
                    {
                        await ReconcileWindowsAsync(stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Windows hardware device reconcile failed");
                    }
                }
            }
            catch (OperationCanceledException) { }

            _logger.LogInformation("Hardware device watcher stopped");
            return;
        }

        // ── Non-Linux, non-Windows: no implementation ──
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogInformation("Hardware device watcher disabled (platform={Platform}, enabled={Enabled})",
                OperatingSystem.IsLinux() ? "linux" : "non-linux", _config.HardwareDevicesEnabled);
            return;
        }

        _logger.LogInformation("Hardware device watcher starting (Linux udev + /sys)");

        try
        {
            await ReconcileAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial hardware device enumeration failed");
        }

        var monitorTask = Task.Run(() => RunUdevMonitorAsync(stoppingToken), stoppingToken);
        var audioMonitorTask = Task.Run(() => RunAudioJackMonitorAsync(stoppingToken), stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ReconcileInterval, stoppingToken);
                    await ReconcileAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Hardware device reconcile failed");
                }
            }
        }
        finally
        {
            try { await monitorTask; } catch { }
            try { await audioMonitorTask; } catch { }
        }

        _logger.LogInformation("Hardware device watcher stopped");
    }

    // ────────────────────────────────────────
    // Windows — PnP device polling
    // ────────────────────────────────────────

    // PnP device classes that represent real physical peripherals (everything else — PCI,
    // system devices, processors, batteries, host controllers — is internal hardware).
    private static readonly HashSet<string> TrackedWindowsClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "DiskDrive", "KeyBoard", "Mouse", "HIDDevice", "Camera", "Image", "Media",
        "AudioEndpoint", "Monitor", "NetworkAdapter", "Bluetooth", "USB", "USBDevice",
        "Printer", "PortableDevice", "SmartCardReader", "Modem", "WPD",
    };

    // Virtual printer queues ("Microsoft Print to PDF", "AnyDesk Printer", "Fax"…)
    // appear under the PrintQueue class — they are software, not physical hardware.
    // Physical USB printers arrive as class "Printer" (or "USB"), which is tracked.

    private async Task ReconcileWindowsAsync(CancellationToken ct)
    {
        await _reconcileGate.WaitAsync(ct);
        try
        {
            var present = await EnumerateWindowsPnpDevicesAsync();
            if (present.Count == 0)
            {
                // Probe failed (PowerShell missing / no devices) — never close rows on a bad probe.
                _logger.LogDebug("Windows PnP probe returned no devices, skipping reconcile");
                return;
            }

            var now = DateTime.UtcNow;
            var open = await _store.GetOpenHardwareDevicesAsync(ct);
            var openPaths = open.Select(d => d.BusPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Insert newly-present devices that don't have an open row yet.
            var toInsert = new List<HardwareDevice>();
            foreach (var kvp in present)
            {
                if (openPaths.Contains(kvp.Key)) continue;
                toInsert.Add(new HardwareDevice
                {
                    DeviceClass = kvp.Value.cls,
                    Vendor = kvp.Value.vendor,
                    Product = kvp.Value.product,
                    BusPath = kvp.Key, // PnP DeviceInstanceId is the stable slot identity
                    PluggedAt = now,
                });
            }
            if (toInsert.Count > 0)
            {
                await _store.StoreHardwareDevicesAsync(toInsert, ct);
                foreach (var dev in toInsert)
                {
                    _logger.LogInformation("Hardware device plugged in: {Class} {Product} ({BusPath})",
                        dev.DeviceClass, dev.Product, dev.BusPath);
                }
            }

            // Close open rows whose device is no longer present (unplugged).
            foreach (var dev in open)
            {
                if (string.IsNullOrEmpty(dev.BusPath) || present.ContainsKey(dev.BusPath)) continue;
                await _store.CloseHardwareDeviceAsync(dev.Id, now, ct);
                _logger.LogInformation("Hardware device unplugged: {Class} {Product} ({BusPath})",
                    dev.DeviceClass, string.IsNullOrEmpty(dev.Product) ? "(unnamed)" : dev.Product, dev.BusPath);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>
    /// Enumerate present PnP peripherals via PowerShell Get-PnpDevice.
    /// Returns DeviceInstanceId → (device class, vendor, product). Keyed by the PnP id so the
    /// same physical slot keeps one open row across re-plugs of the same device model.
    ///
    /// NOTE: Win32_PnPEntity must NOT be used here — it has no "Class" property (only
    /// ClassGuid), so a Class-based filter silently matches nothing. Get-PnpDevice exposes
    /// the friendly Class name ("DiskDrive", "KeyBoard", "HIDDevice", …).
    /// </summary>
    private async Task<Dictionary<string, (string cls, string vendor, string product)>> EnumerateWindowsPnpDevicesAsync()
    {
        var result = new Dictionary<string, (string cls, string vendor, string product)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Select-Object Class, FriendlyName, Manufacturer, InstanceId | ConvertTo-Json -Compress\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return result;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30000);

            using var doc = JsonDocument.Parse(output);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : doc.RootElement.ValueKind == JsonValueKind.Object
                    ? new[] { doc.RootElement }.AsEnumerable()
                    : [];

            foreach (var item in items)
            {
                var cls = item.TryGetProperty("Class", out var c) ? c.GetString() ?? "" : "";
                var vendor = item.TryGetProperty("Manufacturer", out var m) ? m.GetString() ?? "" : "";
                var name = item.TryGetProperty("FriendlyName", out var n) ? n.GetString() ?? "" : "";
                var id = item.TryGetProperty("InstanceId", out var p) ? p.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(id) || !TrackedWindowsClasses.Contains(cls)) continue;

                // Internal USB infrastructure, not a peripheral (covers "USB Root Hub",
                // "Generic USB Hub", "… Host Controller").
                if (name.Contains("Hub", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Host Controller", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Only EXTERNAL USB peripherals are hotplug events — internal SATA/NVMe
                // drives (InstanceId SCSI\…/NVME\…/IDE\…) and onboard NICs are part of the
                // machine, so USB-storage/USB-NIC InstanceIds are required for those classes.
                if (cls.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase) &&
                    !id.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase) &&
                    !id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (cls.Equals("NetworkAdapter", StringComparison.OrdinalIgnoreCase) &&
                    !id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                    continue;

                result[id] = (ClassifyWindowsPnpClass(cls), vendor ?? "", name ?? "");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows PnP device enumeration failed");
        }
        return result;
    }

    private static string ClassifyWindowsPnpClass(string cls)
    {
        return cls.ToLowerInvariant() switch
        {
            "diskdrive" => "storage",
            "keyboard" or "mouse" or "hiddevice" => "input",
            "camera" or "image" or "monitor" => "display",
            "media" or "audioendpoint" => "audio",
            "networkadapter" => "network",
            "bluetooth" or "usb" or "usbdevice" => "usb",
            "printer" or "portabledevice" or "wpd" => "storage",
            _ => "usb",
        };
    }

    // ────────────────────────────────────────
    // Audio jack monitor — headset/headphone plug/unplug
    // ────────────────────────────────────────

    private async Task RunAudioJackMonitorAsync(CancellationToken ct)
    {
        if (!CommandExists("amixer"))
        {
            _logger.LogDebug("amixer not found — skipping audio jack monitoring");
            return;
        }

        _logger.LogDebug("Audio jack monitor starting (polling amixer for headphone/headset state)");

        var lastHeadphonePluggedIn = false;
        var lastHeadsetMicPluggedIn = false;

        try
        {
            var initialState = await ReadAudioJackStateAsync();
            if (initialState.HasValue)
            {
                lastHeadphonePluggedIn = initialState.Value.HeadphonePluggedIn;
                lastHeadsetMicPluggedIn = initialState.Value.HeadsetMicPluggedIn;
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    var currentState = await ReadAudioJackStateAsync();

                    if (!currentState.HasValue) continue;

                    var (headphoneIn, headsetMicIn, cardId, codecName) = currentState.Value;

                    if (headphoneIn != lastHeadphonePluggedIn)
                    {
                        if (headphoneIn)
                        {
                            _logger.LogInformation("Analog audio jack plugged: Headphone ({Card}/{Codec})", cardId, codecName);
                            await StoreAudioDeviceAsync("headphone", "Analog audio jack", $"Headphone jack ({codecName})", cardId, ct);
                        }
                        else
                        {
                            _logger.LogInformation("Analog audio jack unplugged: Headphone ({Card}/{Codec})", cardId, codecName);
                            await CloseAudioDeviceAsync("headphone", $"Headphone jack ({codecName})", ct);
                        }
                        lastHeadphonePluggedIn = headphoneIn;
                    }

                    if (headsetMicIn != lastHeadsetMicPluggedIn)
                    {
                        if (headsetMicIn)
                        {
                            _logger.LogInformation("Analog audio jack plugged: Headset Mic ({Card}/{Codec})", cardId, codecName);
                            await StoreAudioDeviceAsync("audio", "Headset", $"Headset mic jack ({codecName})", cardId, ct);
                        }
                        else
                        {
                            _logger.LogInformation("Analog audio jack unplugged: Headset Mic ({Card}/{Codec})", cardId, codecName);
                            await CloseAudioDeviceAsync("audio", $"Headset mic jack ({codecName})", ct);
                        }
                        lastHeadsetMicPluggedIn = headsetMicIn;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Audio jack poll failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio jack monitor failed");
        }
        finally
        {
            _logger.LogDebug("Audio jack monitor stopped");
        }
    }

    private async Task<(bool HeadphonePluggedIn, bool HeadsetMicPluggedIn, string CardId, string CodecName)?> ReadAudioJackStateAsync()
    {
        try
        {
            var soundCards = Directory.GetDirectories("/sys/class/sound/");
            string? cardId = null;
            string? codecName = null;

            foreach (var cardPath in soundCards)
            {
                var cardName = Path.GetFileName(cardPath);
                if (!cardName.StartsWith("card", StringComparison.Ordinal)) continue;

                var idFile = Path.Combine(cardPath, "id");
                if (File.Exists(idFile))
                {
                    cardId = File.ReadAllText(idFile).Trim();
                }
                else
                {
                    cardId = cardName;
                }

                var codecPath = Directory.GetDirectories(cardPath, "hw*").FirstOrDefault();
                if (codecPath != null)
                {
                    var codecIdFile = Path.Combine(codecPath, "device", "id");
                    if (File.Exists(codecIdFile))
                    {
                        codecName = File.ReadAllText(codecIdFile).Trim();
                    }
                }

                var hasHeadphone = await AmixerControlExistsAsync(cardName.Replace("card", ""), "Headphone");
                var hasHeadsetMic = await AmixerControlExistsAsync(cardName.Replace("card", ""), "Headset Mic");

                if (hasHeadphone || hasHeadsetMic)
                {
                    break;
                }
            }

            if (cardId == null) return null;

            var cardNum = cardId.Replace("card", "");

            var headphoneIn = false;
            if (await AmixerControlExistsAsync(cardNum, "Headphone"))
            {
                var output = await RunProcessAsync("amixer", $"-c {cardNum} sget 'Headphone'");
                if (!string.IsNullOrEmpty(output))
                {
                    headphoneIn = output.Contains("[on]", StringComparison.OrdinalIgnoreCase);
                }
            }

            var headsetMicIn = false;
            if (await AmixerControlExistsAsync(cardNum, "Headset Mic"))
            {
                var output = await RunProcessAsync("amixer", $"-c {cardNum} sget 'Headset Mic'");
                if (!string.IsNullOrEmpty(output))
                {
                    headsetMicIn = output.Contains("[on]", StringComparison.OrdinalIgnoreCase);
                }
            }

            return (headphoneIn, headsetMicIn, cardId ?? "card0", codecName ?? "ALC");
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> AmixerControlExistsAsync(string cardNum, string controlName)
    {
        try
        {
            var output = await RunProcessAsync("amixer", $"-c {cardNum} scontrols");
            if (string.IsNullOrEmpty(output)) return false;

            return output.Split('\n')
                .Any(line => line.Contains($"'{controlName}'", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private async Task StoreAudioDeviceAsync(string deviceClass, string vendor, string product, string cardId, CancellationToken ct)
    {
        await _reconcileGate.WaitAsync(ct);
        try
        {
            var busPath = $"/sys/class/sound/{cardId}";

            var existing = await _store.GetOpenHardwareDevicesAsync(ct);
            var alreadyOpen = existing.Any(d =>
                d.DeviceClass == deviceClass &&
                d.BusPath == busPath &&
                d.Product.Contains(product));

            if (alreadyOpen)
            {
                _logger.LogDebug("Audio device {Class} {Product} already tracked, skipping", deviceClass, product);
                return;
            }

            var device = new HardwareDevice
            {
                DeviceClass = deviceClass,
                Vendor = vendor,
                Product = product,
                BusPath = busPath,
                DeviceNode = $"/devices/pci0000:00/0000:00:1f.3/sound/{cardId}",
                PluggedAt = DateTime.UtcNow,
            };

            await _store.StoreHardwareDevicesAsync(new[] { device }, ct);
            _logger.LogInformation("Audio device plugged in: {Class} {Product} ({BusPath})", deviceClass, product, busPath);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task CloseAudioDeviceAsync(string deviceClass, string product, CancellationToken ct)
    {
        await _reconcileGate.WaitAsync(ct);
        try
        {
            var open = await _store.GetOpenHardwareDevicesAsync(ct);
            var toClose = open.Where(d =>
                d.DeviceClass == deviceClass &&
                d.Product.Contains(product)).ToList();

            if (toClose.Count == 0) return;

            var now = DateTime.UtcNow;
            foreach (var dev in toClose)
            {
                await _store.CloseHardwareDeviceAsync(dev.Id, now, ct);
                _logger.LogInformation("Audio device unplugged: {Class} {Product} ({Id})",
                    deviceClass, product, dev.Id);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    // ────────────────────────────────────────
    // udev monitor — real-time add/remove
    // ────────────────────────────────────────

    private async Task RunUdevMonitorAsync(CancellationToken ct)
    {
        if (!CommandExists("udevadm"))
        {
            _logger.LogDebug("udevadm not found — relying on periodic /sys reconcile only");
            return;
        }

        using var proc = StartProcess("udevadm", "monitor --property --subsystem-match=usb");
        if (proc == null)
        {
            _logger.LogDebug("Failed to start udevadm monitor");
            return;
        }

        _logger.LogDebug("udevadm monitor started");

        string? pendingAction = null;
        string? pendingDevPath = null;
        string? pendingSubsystem = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;

                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    if (!string.IsNullOrEmpty(pendingAction) && !string.IsNullOrEmpty(pendingDevPath))
                    {
                        try { await HandleUdevEventAsync(pendingAction, pendingDevPath, pendingSubsystem, ct); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Failed to handle udev event"); }
                    }
                    pendingAction = null;
                    pendingDevPath = null;
                    pendingSubsystem = null;
                    continue;
                }

                if (trimmed.StartsWith("ACTION=", StringComparison.Ordinal))
                    pendingAction = trimmed["ACTION=".Length..];
                else if (trimmed.StartsWith("DEVPATH=", StringComparison.Ordinal))
                    pendingDevPath = trimmed["DEVPATH=".Length..];
                else if (trimmed.StartsWith("SUBSYSTEM=", StringComparison.Ordinal))
                    pendingSubsystem = trimmed["SUBSYSTEM=".Length..];
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "udevadm monitor ended");
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { proc.Dispose(); } catch { }
        }
    }

    private async Task HandleUdevEventAsync(string action, string devPath, string? subsystem, CancellationToken ct)
    {
        switch (action)
        {
            case "add":
            case "bind":
                // Interfaces (…:1.0) and non-usb subsystems arrive too — a full re-scan
                // is idempotent (unique open-path index + store dedup), so just reconcile.
                await ReconcileAsync(ct);
                break;

            case "remove":
            case "unbind":
                // udev may emit the interface path (…:1.0) for the removal of the whole
                // device, so close every open row whose bus_path is this path OR a prefix
                // of this path (the device node is a parent of its interfaces).
                await CloseDevicesByPathAsync(devPath, ct);
                break;
        }
    }

    private async Task CloseDevicesByPathAsync(string devPath, CancellationToken ct)
    {
        var open = await _store.GetOpenHardwareDevicesAsync(ct);
        var now = DateTime.UtcNow;
        var matched = 0;
        foreach (var dev in open)
        {
            if (string.IsNullOrEmpty(dev.BusPath)) continue;
            // Match on component boundaries only: the device itself, or an interface under
            // it (…:1.0) being removed means the whole device is gone. NEVER treat a
            // device path as a match for an ANCESTOR row (a removed device must not close
            // the host-controller row it hangs off).
            if (dev.BusPath == devPath || devPath.StartsWith(dev.BusPath + "/", StringComparison.Ordinal))
            {
                await _store.CloseHardwareDeviceAsync(dev.Id, now, ct);
                matched++;
                _logger.LogInformation("Hardware device unplugged: {Class} {Product} ({BusPath})",
                    dev.DeviceClass, string.IsNullOrEmpty(dev.Product) ? "(unnamed)" : dev.Product, dev.BusPath);
            }
        }
        if (matched > 0)
        {
            _logger.LogDebug("Closed {Count} hardware device row(s) for DEVPATH {Path}", matched, devPath);
        }
    }

    // ────────────────────────────────────────
    // /sys enumeration (boot backfill + reconcile)
    // ────────────────────────────────────────

    private async Task ReconcileAsync(CancellationToken ct)
    {
        await _reconcileGate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var devices = new List<HardwareDevice>();

            // 1) USB devices with a real idVendor (skip interface sub-directories 1-8:1.0
            //    AND root hubs / host controllers usb1/usb2 — internal, always present,
            //    and a prefix of every device under them, which would make unplug matching
            //    close the controller row whenever any device is removed).
            var usbRoot = "/sys/bus/usb/devices";
            if (Directory.Exists(usbRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(usbRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (Regex.IsMatch(name, "^usb\\d+$")) continue;
                    var idVendor = ReadSysFile(Path.Combine(dir, "idVendor"));
                    if (string.IsNullOrWhiteSpace(idVendor)) continue;

                    var vendor = ReadSysFile(Path.Combine(dir, "manufacturer"));
                    var product = ReadSysFile(Path.Combine(dir, "product"));
                    var serial = ReadSysFile(Path.Combine(dir, "serial"));
                    var devClass = ClassifyUsbDevice(dir);

                    devices.Add(new HardwareDevice
                    {
                        DeviceClass = devClass,
                        Vendor = vendor ?? string.Empty,
                        Product = product ?? string.Empty,
                        Serial = serial ?? string.Empty,
                        BusPath = ResolveSysfsPath(dir),
                        DeviceNode = ReadSysFile(Path.Combine(dir, "devnum")) is { } dn && dn.Length > 0
                            ? $"/dev/bus/usb/{ReadSysFile(Path.Combine(dir, "busnum"))}/{dn}"
                            : string.Empty,
                        PluggedAt = now,
                    });
                }
            }

            // 2) Storage (block) devices that live on USB (external drives stick out).
            var blockRoot = "/sys/class/block";
            if (Directory.Exists(blockRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(blockRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal)) continue;
                    var linkTarget = ResolveSysfsPath(dir);
                    if (string.IsNullOrEmpty(linkTarget) || !linkTarget.Contains("/usb", StringComparison.Ordinal)) continue;

                    var vendor = ReadSysFile(Path.Combine(dir, "device/vendor")) ?? string.Empty;
                    var product = ReadSysFile(Path.Combine(dir, "device/model")) ?? string.Empty;
                    devices.Add(new HardwareDevice
                    {
                        DeviceClass = "storage",
                        Vendor = vendor,
                        Product = string.IsNullOrWhiteSpace(product) ? name : product.Trim(),
                        Serial = string.Empty,
                        BusPath = linkTarget,
                        DeviceNode = $"/dev/{name}",
                        PluggedAt = now,
                    });
                }
            }

            if (devices.Count > 0)
            {
                await _store.StoreHardwareDevicesAsync(devices, ct);
                _logger.LogDebug("Hardware device reconcile found {Count} present device(s)", devices.Count);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private static string ClassifyUsbDevice(string sysfsDir)
    {
        var bDeviceClass = ReadSysFile(Path.Combine(sysfsDir, "bDeviceClass")) ?? string.Empty;
        return bDeviceClass.Trim() switch
        {
            "08" => "storage",   // Mass Storage
            "03" => "input",     // HID (keyboard/mouse/gamepad)
            "01" => "audio",     // Audio
            "0e" => "display",   // Video/imaging (webcams)
            "02" => "usb",       // Communications
            _ => "usb",
        };
    }

    private static string? ReadSysFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path).Trim();
        }
        catch { return null; }
    }

    /// <summary>Resolve /sys/... to its udev-style DEVPATH (e.g. /devices/pci0000:00/.../1-8).</summary>
    private static string ResolveSysfsPath(string sysfsEntry)
    {
        try
        {
            var info = new DirectoryInfo(sysfsEntry);
            var target = info.LinkTarget;
            if (!string.IsNullOrEmpty(target))
            {
                // /sys/bus/usb/devices/1-8 → ../../../devices/pci.../usb1/1-8 (relative).
                // Resolve against /sys and strip the /sys prefix so it matches the
                // absolute DEVPATH that udev emits in monitor events.
                var full = Path.GetFullPath(target, "/sys");
                if (full.StartsWith("/sys", StringComparison.Ordinal))
                    return full["/sys".Length..].TrimStart('/');
                return full.TrimEnd('/');
            }
            return info.FullName;
        }
        catch { return sysfsEntry; }
    }

    // ────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────

    private static bool CommandExists(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("which", name)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static Process? StartProcess(string fileName, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            return Process.Start(psi);
        }
        catch { return null; }
    }

    private static async Task<string> RunProcessAsync(string fileName, string args)
    {
        try
        {
            using var proc = StartProcess(fileName, args);
            if (proc == null) return string.Empty;

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();

            try { proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(2000); } catch { }

            return string.IsNullOrEmpty(error) ? output : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
