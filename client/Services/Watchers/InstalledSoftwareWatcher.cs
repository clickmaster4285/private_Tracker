using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Core;

namespace client.Services.Watchers;

/// <summary>
/// Near-real-time install/uninstall detection for BOTH applications and packages. Watches
/// the OS locations where installation artifacts appear/disappear — app evidence
/// (.desktop files, Start Menu shortcuts, /Applications bundles) AND package-manager state
/// (dpkg status, npm global root, pip user site, snap db, flatpak runtimes, brew Cellar,
/// Windows %APPDATA%\npm) — and triggers an immediate <see cref="LogCollectorService"/>
/// inventory rescan when anything changes. So an app OR package install/uninstall through
/// terminal, software center, control panel, cmd/powershell, npm/pip/apt, or manual file
/// delete is recorded within seconds WITHOUT any GUI interaction and WITHOUT any
/// minute-based polling (user rule 2026-08-10: no periodic inventory scan — the watcher is
/// the ONLY runtime trigger; the one-time startup scan at boot is the only other scan).
///
/// Why file watching and not polling: install/uninstall is a discrete filesystem event
/// (.desktop created/deleted, dpkg status rewritten, npm package dir created/deleted,
/// .lnk created/deleted, .app moved into /Applications) — a watcher reacts instantly, a
/// poll can only be as fast as its interval. Events are debounced (an apt transaction
/// rewrites dpkg status several times) and coalesced (a rescan already running is not
/// stacked).
/// </summary>
public sealed class InstalledSoftwareWatcher : BackgroundService
{
    private readonly LogCollectorService _logCollector;
    private readonly ILogger<InstalledSoftwareWatcher> _logger;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();
    private DateTime _firstUnscannedChangeAt = DateTime.MinValue;
    private DateTime _lastChangeAt = DateTime.MinValue;
    private DateTime _lastScanStartedAt = DateTime.MinValue;
    private bool _rescanRunning;

    // Debounce: an apt/dpkg transaction fires many status rewrites in quick succession;
    // wait for the burst to settle before scanning. Anchored to the FIRST unscanned change
    // (not the latest): if events kept arriving faster than the debounce window, anchoring
    // to the latest would starve the rescan forever — a constant event source would keep
    // pushing the deadline out and no scan would ever start.
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(1500);
    // Min gap between rescans: even with debounce, a flood of unrelated events (e.g. a
    // watched tree being touched by something else) must not spin the collectors.
    private static readonly TimeSpan MinScanGap = TimeSpan.FromSeconds(5);

    public InstalledSoftwareWatcher(LogCollectorService logCollector, ILogger<InstalledSoftwareWatcher> logger)
    {
        _logCollector = logCollector;
        _logger = logger;
    }

    /// <summary>
    /// The exact trees where install/uninstall leaves filesystem evidence, per platform.
    /// Deliberately structural (OS-provided paths) — no product-name lists.
    /// </summary>
    private static string[] GetWatchDirectories()
    {
        var dirs = new List<string>();

        if (OperatingSystem.IsLinux())
        {
            // .desktop entries — the GUI-app install signal (user + system + flatpak + snap exports).
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "applications"));
            dirs.Add("/usr/share/applications");
            dirs.Add("/usr/local/share/applications");
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "flatpak", "exports", "share", "applications"));
            dirs.Add("/var/lib/flatpak/exports/share/applications");
            // dpkg status — the apt/dpkg package signal (rewritten on every install/uninstall).
            dirs.Add("/var/lib/dpkg");
        }
        else if (OperatingSystem.IsWindows())
        {
            // Start Menu shortcuts — the Windows GUI-app install signal (user + common).
            dirs.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"));
            dirs.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"));
            // Program Files root changes when an installer adds/removes a top-level app folder.
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(pf)) dirs.Add(pf);
            if (!string.IsNullOrWhiteSpace(pfX86) && !string.Equals(pf, pfX86, StringComparison.OrdinalIgnoreCase))
                dirs.Add(pfX86);
        }
        else if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/Applications");
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"));
        }

        return dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToArray();
    }

    /// <summary>
    /// The trees where PACKAGE installs leave filesystem evidence, per platform. Watched in
    /// addition to <see cref="GetWatchDirectories"/> so npm/pip/snap/flatpak/brew/winget
    /// installs and uninstalls trigger the same instant rescan as GUI-app installs.
    /// All locations are structural (package-manager conventions) — no product names.
    /// </summary>
    private static string[] GetPackageWatchDirectories()
    {
        var dirs = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsLinux())
        {
            // npm global installs: the package dir is created/deleted as a DIRECT child of
            // the global node_modules root, so one non-recursive watcher there sees
            // `npm install -g` / `npm uninstall -g` instantly. Resolved at runtime because
            // the global root varies per setup (nvm/fnm/user prefix/deb node).
            foreach (var root in ResolveNpmGlobalRoots())
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                    dirs.Add(root);
            // pip --user installs land under ~/.local/lib/python3.x/site-packages (PEP 370).
            // Watched recursively — the versioned site-packages dir is a grandchild.
            dirs.Add(Path.Combine(home, ".local", "lib"));
            // snap state DB — rewritten in place on every `snap install`/`snap remove`.
            dirs.Add("/var/lib/snapd/db");
            // flatpak runtimes; new runtime dirs are direct children of these roots (and the
            // trees contain full runtime filesystems, so they are watched top-level only).
            dirs.Add("/var/lib/flatpak/runtime");
            dirs.Add(Path.Combine(home, ".local", "share", "flatpak", "runtime"));
        }
        else if (OperatingSystem.IsWindows())
        {
            // npm global on Windows: %APPDATA%\npm\node_modules (direct child = package dir).
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
                dirs.Add(Path.Combine(appData, "npm", "node_modules"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            // npm global installs (same runtime resolution as Linux — npm is a shell script
            // here) plus Homebrew: each formula gets a top-level dir in the Cellar.
            foreach (var root in ResolveNpmGlobalRoots())
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                    dirs.Add(root);
            dirs.Add("/usr/local/Cellar");
            dirs.Add("/opt/homebrew/Cellar");
        }

        return dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToArray();
    }

    /// <summary>
    /// Resolve the npm global root via `npm root -g` (time-boxed probe), falling back to the
    /// two standard system roots when npm itself isn't on PATH. Never blocks long: the probe
    /// is hard-capped by <see cref="ProcessFilter.RunProbe"/>.
    /// </summary>
    private static IEnumerable<string> ResolveNpmGlobalRoots()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "npm",
            Arguments = "root -g",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var output = ProcessFilter.RunProbe(psi, 5000)?.Trim();
        if (!string.IsNullOrWhiteSpace(output))
            yield return output;
        // Standard system npm global roots (used when npm is installed but not on PATH).
        yield return "/usr/local/lib/node_modules";
        yield return "/usr/lib/node_modules";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("InstalledSoftwareWatcher parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        var watched = 0;
        var dirs = GetWatchDirectories().Concat(GetPackageWatchDirectories()).Distinct().ToArray();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                // LastWrite is only needed where state is REWRITTEN in place rather than
                // recreated — /var/lib/dpkg status and /var/lib/snapd/db. Applying it to
                // every tree (e.g. Program Files, where apps/AV write files in place
                // constantly) would turn every write into a rescan trigger.
                var needsInPlaceRewrite = dir.Contains("dpkg", StringComparison.OrdinalIgnoreCase)
                    || dir.Contains("snapd", StringComparison.OrdinalIgnoreCase);
                // Program Files, node_modules and flatpak runtime trees are watched
                // top-level only: install/uninstall creates/removes a top-level entry there,
                // and the recursive app watcher (Start Menu / .desktop dirs) is the primary
                // GUI signal anyway — recursion inside package trees only adds noise (every
                // file of every package would fire events; a flatpak runtime tree is a full
                // filesystem).
                var recurse = !(dir.Contains("Program Files", StringComparison.OrdinalIgnoreCase)
                    || dir.Contains("node_modules", StringComparison.OrdinalIgnoreCase)
                    || dir.EndsWith("flatpak/runtime", StringComparison.OrdinalIgnoreCase));
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = recurse,
                    // FileName/DirectoryName: install/uninstall creates/deletes entries.
                    NotifyFilter = needsInPlaceRewrite
                        ? NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                        : NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                watcher.Created += OnFsChange;
                watcher.Deleted += OnFsChange;
                watcher.Renamed += OnFsChange;
                watcher.Changed += OnFsChange;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                watched++;
                _logger.LogDebug("Installed-software watcher attached to {Dir}", dir);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not attach install watcher to {Dir} (may need elevated rights)", dir);
            }
        }
        _logger.LogInformation("Installed-software watcher watching {Count} location(s)", watched);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                await MaybeRescanAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        finally
        {
            foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            _watchers.Clear();
        }
    }

    private void OnFsChange(object sender, FileSystemEventArgs e)
    {
        // Ignore our own DB dirs (never watched, but be safe) and editor temp noise.
        if (e.FullPath.Contains("alpha-ai-tracker", StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate)
        {
            _lastChangeAt = DateTime.UtcNow;
            // Remember the FIRST change of the burst — the debounce anchor. Re-armed only
            // while no rescan is consuming this burst (see MaybeRescanAsync).
            if (_firstUnscannedChangeAt == DateTime.MinValue)
                _firstUnscannedChangeAt = _lastChangeAt;
        }
    }

    private async Task MaybeRescanAsync(CancellationToken ct)
    {
        DateTime firstChange;
        lock (_gate) { firstChange = _firstUnscannedChangeAt; }

        if (firstChange == DateTime.MinValue) return;                        // nothing changed yet
        if (DateTime.UtcNow - firstChange < DebounceDelay) return;           // burst settling (from FIRST event — a constant event stream can't starve this)
        if (DateTime.UtcNow - _lastScanStartedAt < MinScanGap) return;       // avoid event floods

        bool run;
        lock (_gate)
        {
            if (_rescanRunning) return;   // a rescan is already in flight — coalesce
            _rescanRunning = true;
            // This burst is now being scanned. Any change arriving DURING the rescan
            // re-arms _firstUnscannedChangeAt via OnFsChange, so nothing is ever lost.
            _firstUnscannedChangeAt = DateTime.MinValue;
            run = true;
        }
        if (!run) return;

        // Capture the scan-start time BEFORE running, so the finally can consume only the
        // change that triggered THIS rescan (see the race-condition comment below).
        _lastScanStartedAt = DateTime.UtcNow;
        var scannedAt = _lastScanStartedAt;
        try
        {
            _logger.LogInformation("Software inventory change detected — rescanning (background)");
            // Defense-in-depth cap on the STORE/lifecycle phase (the detector phase is not
            // cancellation-aware, but each CLI probe inside it is hard time-boxed by
            // ProcessFilter.RunProbe, so the whole scan is bounded either way). This stops
            // a pathological store from holding the in-flight flag and freezing all later
            // install/uninstall events — the bug that made the DB look stale until restart.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));
            await _logCollector.RescanInventoryAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background inventory rescan failed");
        }
        finally
        {
            lock (_gate)
            {
                // Consume the change we just scanned — but only if no NEWER change arrived
                // while the rescan was running (otherwise that newer change would be lost
                // and its install/uninstall never recorded until the next periodic scan).
                if (_lastChangeAt <= scannedAt) _lastChangeAt = DateTime.MinValue;
                _rescanRunning = false;
            }
        }
    }
}
