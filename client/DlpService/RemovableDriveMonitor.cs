using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Polls for Removable drive roots. When USB storage is policy-blocked, emits a
/// DLP alert (and audits) for each newly-seen volume so admins see the attempt.
/// </summary>
public sealed class RemovableDriveMonitor : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly ILogger<RemovableDriveMonitor> _logger;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private bool _usbBlocked;

    public RemovableDriveMonitor(DlpConfig cfg, PolicyStore store, ILogger<RemovableDriveMonitor> logger)
    {
        _cfg = cfg;
        _store = store;
        _logger = logger;
    }

    public void SetUsbBlocked(bool blocked) => _usbBlocked = blocked;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.Enabled)
        {
            _logger.LogInformation("RemovableDriveMonitor parked (ALPHA_DLP_ENABLED=false)");
            return;
        }
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            _logger.LogInformation("RemovableDriveMonitor parked (unsupported OS)");
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        // Seed known set so already-mounted sticks don't flood alerts on boot.
        foreach (var root in ListRemovableRoots())
            _known.Add(root);

        _logger.LogInformation("RemovableDriveMonitor watching ({Count} volumes at start)", _known.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RemovableDriveMonitor tick failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var (version, _, bundleJson) = await _store.GetAppliedAsync(ct);
        var bundle = PolicyBundleParser.Parse(bundleJson);
        var block = _cfg.UsbBlock && PolicyBundleParser.WantsUsbStorageBlock(bundle);
        _usbBlocked = block;

        var current = ListRemovableRoots();
        foreach (var root in current)
        {
            if (_known.Add(root))
            {
                _logger.LogInformation("Removable volume detected: {Root} (usbBlocked={Blocked})", root, block);
                await _store.AuditAsync("usb_volume_seen", root, ct);
                if (block)
                {
                    var rule = PolicyBundleParser.FirstUsbBlockRule(bundle);
                    var evidence = JsonSerializer.Serialize(new
                    {
                        path = root,
                        device = "removable",
                        usbstorBlocked = true,
                    });
                    await _store.EnqueueAlertAsync(
                        Guid.NewGuid().ToString(),
                        string.IsNullOrEmpty(rule?.Id) ? null : rule.Id,
                        version,
                        "usb_storage",
                        rule?.Severity ?? "high",
                        "blocked",
                        DateTimeOffset.UtcNow,
                        evidence,
                        ct);
                }
            }
        }

        _known.RemoveWhere(k => !current.Contains(k, StringComparer.OrdinalIgnoreCase));
    }

    private static List<string> ListRemovableRoots()
    {
        var list = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType == DriveType.Removable && d.IsReady)
                    list.Add(d.RootDirectory.FullName);
            }
        }
        catch { /* ignore transient drive errors */ }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (var mediaRoot in new[] { "/media", "/run/media" })
                {
                    if (!Directory.Exists(mediaRoot)) continue;
                    foreach (var userDir in Directory.EnumerateDirectories(mediaRoot))
                    {
                        foreach (var vol in Directory.EnumerateDirectories(userDir))
                            list.Add(vol);
                    }
                }
            }
            catch { /* ignore */ }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
