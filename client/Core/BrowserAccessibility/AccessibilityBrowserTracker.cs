using System.Text.Json;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace client.Core.BrowserAccessibility;

/// <summary>
/// Option B — accessibility-based browser journey tracker.
///
/// Polls the OS accessibility tree (AT-SPI / UIA / AX) and writes the employee's REAL
/// browser journeys into SQLite as <c>app_sessions</c> + <c>app_items</c>:
///   - one session per browser window (keyed by the a11y object identity),
///   - a <c>browser_tab</c> root item carrying the active tab's exact URL (from the
///     address bar) + title,
///   - <c>browser_navigation</c> child items whenever the address-bar URL changes,
///   - downloads in the Downloads folder appended to the most recent browser session.
///
/// Works on every browser and every Chrome version — no debugger, no extension, no
/// catalog dependency. Incognito windows are flagged; their URL is only stored when
/// <see cref="AppConfig.BrowserCaptureIncognito"/> is enabled (default: off, legal-safe).
/// </summary>
public sealed class AccessibilityBrowserTracker : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IAccessibilityBrowserReader _reader;
    private readonly BrowserHistoryReader? _history;
    private readonly ILogStore _store;
    private readonly ILogger<AccessibilityBrowserTracker> _logger;
    private readonly IBrowserRegistry? _browserRegistry;

    private sealed class TrackedWindow
    {
        public required string SessionId { get; init; }
        // Mutable: each page navigation closes the current browser_tab root and opens
        // a new one, so the tracker must re-point at the latest root item.
        public string RootItemId { get; set; } = string.Empty;
        public required string JourneyId { get; init; }
        public required int ProcessId { get; init; }
        public string? LastUrl { get; set; }
        public string LastTitle { get; set; } = string.Empty;
        public bool IsIncognito { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        /// <summary>Title seen only briefly (badge/timer flicker) — rotated only once stable.</summary>
        public string? PendingTitle { get; set; }
        public DateTime PendingSinceUtc { get; set; }
        public DateTime? LastSeen { get; set; }
    }

    // A browser window can be transiently absent from the a11y tree / sessionstore for
    // a few polls (Chrome rebuilds its accessible tree on navigation; Firefox sessionstore
    // is written every ~15s). Give it 5 polls (~15s) before declaring it closed, so a
    // transient miss can never churn the session (the old 3-poll grace caused windows to
    // close and reopen, which together with the PID re-key produced the duplicate rows).
    private const int MissingPollsToClose = 5;
    /// <summary>Minimum dwell before a title-only change rotates the browser_tab record
    /// (badges/timers/player-state titles change often and would fragment the journey).</summary>
    private static readonly TimeSpan MinTabRotationInterval = TimeSpan.FromSeconds(10);
    /// <summary>
    /// When a previously-resolved URL goes missing on the next navigation, the history-DB
    /// fallback lags real navigation by a few seconds — but for private-browsing windows
    /// (and about:/file:/offline pages) it can NEVER catch up, because those visits are
    /// never written to the profile history. The wait for the URL is therefore BOUNDED:
    /// after this timeout the tab rotates on its title alone. Without this bound, the
    /// "history-lag" hold froze the tab forever and swallowed every later same-tab
    /// navigation (the Firefox private-window freeze).
    /// </summary>
    private static readonly TimeSpan HistoryLagTimeout = TimeSpan.FromSeconds(25);
    /// <summary>
    /// Max age of the persisted heartbeat for which a relaunch is considered "fast"
    /// (same live browser windows) and the window registry is hydrated from open DB
    /// sessions. Matches the main loop's CrashGracePeriod — a stale heartbeat means
    /// those sessions are being closed as crashed, not reused.
    /// </summary>
    private static readonly TimeSpan HydrateGracePeriod = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, TrackedWindow> _tracked = new();
    // Per-session foreground/background focus totals, accumulated once per poll from the
    // OS ACTIVE/FOCUSED window and flushed every 10 polls + on close (mirrors the main
    // collector loop). Keyed by app_session id.
    private readonly Dictionary<string, (double Fg, double Bg)> _sessionFocusSeconds = new();
    private int _pollCount;
    private readonly List<FileSystemWatcher> _downloadWatchers = new();
    // Per-PID window counts from the previous poll — used to distinguish a navigation
    // of the only window (re-key) from a second window returning after a transient
    // absence (fresh session), which is what prevented window-stealing before.
    private IReadOnlyDictionary<int, int> _lastPollPidCounts = new Dictionary<int, int>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _stopping;

    private string? _employeeId;
    private string? _employeeName;
    private DateTime _lastEmployeeRefresh = DateTime.MinValue;

    public AccessibilityBrowserTracker(
        AppConfig config,
        IAccessibilityBrowserReader reader,
        ILogStore store,
        ILogger<AccessibilityBrowserTracker> logger,
        BrowserHistoryReader? history = null,
        IBrowserRegistry? browserRegistry = null)
    {
        _config = config;
        _reader = reader;
        _history = history;
        _store = store;
        _logger = logger;
        _browserRegistry = browserRegistry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("AccessibilityBrowserTracker parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        _logger.LogInformation(
            "Accessibility browser tracker starting (platform={Platform}, poll={Poll}s, idle-close={Idle}min, capture-incognito={Incognito})",
            _reader.Platform, _config.BrowserAccessibilityPollSec, _config.BrowserJourneyIdleMinutes, _config.BrowserCaptureIncognito);

        await _store.InitializeAsync(stoppingToken);
        await RefreshEmployeeInfoAsync(stoppingToken);
        await HydrateTrackedWindowsAsync(stoppingToken);
        StartDownloadWatchers();
        _stopping = false;

        var interval = TimeSpan.FromSeconds(Math.Max(2, _config.BrowserAccessibilityPollSec));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (UpdatePriority.ShouldYield)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Accessibility browser tracker poll failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Graceful shutdown: close whatever is still open so no orphan sessions remain.
        _stopping = true;
        try
        {
            foreach (var watcher in _downloadWatchers)
            {
                try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
            }
            _downloadWatchers.Clear();

            await CloseAllAsync(DateTime.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to close browser sessions on shutdown");
        }
    }

    /// <summary>
    /// Rebuild the in-memory window registry from open DB sessions on a FAST relaunch.
    ///
    /// The _tracked dictionary is process-local, but the open app_sessions for browser
    /// windows survive a restart when the heartbeat is still fresh (the main loop's
    /// boot reconciliation skips browser-owned sessions so it never closes live windows).
    /// Without hydration, a restart with browser windows still open would OPEN A SECOND
    /// session per window while the previous rows stay open → duplicate open sessions.
    ///
    /// Hydration is skipped when the last heartbeat is stale (&gt; grace period): those old
    /// sessions belong to a dead process and are closed by the main loop's crash recovery;
    /// any hydrated entry with no matching live window also self-heals via the missing-polls
    /// close path (5 polls), so a stale hydration can never leak a session.
    /// </summary>
    private async Task HydrateTrackedWindowsAsync(CancellationToken ct)
    {
        try
        {
            var heartbeatStr = await _store.GetStatusAsync("last_heartbeat_at", ct);
            if (string.IsNullOrEmpty(heartbeatStr)) return;

            if (!DateTime.TryParse(heartbeatStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastHeartbeat))
                return;

            if (DateTime.UtcNow - lastHeartbeat > HydrateGracePeriod)
            {
                _logger.LogDebug("Browser tracker skip hydration — heartbeat stale ({Stale:F0}s ago)",
                    (DateTime.UtcNow - lastHeartbeat).TotalSeconds);
                return;
            }

            var openRecords = await _store.GetOpenSessionRecordsAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var rec in openRecords)
            {
                if (rec.ProcessId <= 0) continue;
                // Hydrate BOTH real-browser windows AND embedded webview windows — the
                // latter carry a browser_tab root but a non-browser process (e.g. VS Code
                // Simple Browser). The main loop skips browser_tab-rooted sessions too, so
                // both stay owned by this tracker across a fast relaunch.
                if (_browserRegistry is not null &&
                    !_browserRegistry.IsBrowser(
                        AppProcessClassifier.ExtractBaseProcessName(rec.ProcessName)) &&
                    rec.ItemType != "browser_tab") continue;

                // Use the persisted root browser_tab title/URL as the baseline so the
                // first poll's title-match / URL-match in UpdateWindowAsync sees "no change"
                // and doesn't rotate a duplicate tab record immediately after relaunch.
                var key = $"hydrated:{rec.AppSessionId}";
                _tracked[key] = new TrackedWindow
                {
                    SessionId = rec.AppSessionId,
                    RootItemId = rec.RootItemId,
                    JourneyId = rec.AppSessionId,
                    ProcessId = rec.ProcessId,
                    LastUrl = string.IsNullOrEmpty(rec.RootItemUrl) ? null : rec.RootItemUrl,
                    LastTitle = rec.RootItemTitle,
                    LastActivity = now,
                    LastSeen = now,
                };
            }

            if (_tracked.Count > 0)
            {
                _logger.LogInformation(
                    "Hydrated {Count} browser window(s) from open sessions (fast relaunch)",
                    _tracked.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to hydrate browser tracker windows");
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        if ((now - _lastEmployeeRefresh).TotalMinutes >= 10)
            await RefreshEmployeeInfoAsync(ct);

        var snapshots = await _reader.ReadAsync(ct);
        snapshots = await EnrichUrlsFromHistoryAsync(snapshots, ct);

        var pidCounts = snapshots
            .Where(s => s.ProcessId > 0)
            .GroupBy(s => s.ProcessId)
            .ToDictionary(g => g.Key, g => g.Count());

        var present = new HashSet<string>();
        foreach (var snap in snapshots)
        {
            if (string.IsNullOrWhiteSpace(snap.WindowTitle) && string.IsNullOrWhiteSpace(snap.Url))
                continue;
            present.Add(snap.WindowKey);
        }

        await _gate.WaitAsync(ct);
        try
        {
            foreach (var snap in snapshots)
            {
                var key = ResolveWindowKey(snap, pidCounts, _lastPollPidCounts);
                if (!_tracked.TryGetValue(key, out var tw))
                {
                    await OpenWindowAsync(key, snap, ct);
                    continue;
                }

                tw.LastSeen = now;
                await UpdateWindowAsync(tw, snap, ct);
            }

            // ── Foreground/background focus accounting ──
            // The OS marks exactly ONE window ACTIVE/FOCUSED per poll (AT-SPI
            // STATE_ACTIVE/STATE_FOCUSED on Linux, foreground HWND on Windows, frontmost
            // process on macOS — see the readers' IsActive). Credit that window's session
            // with the poll interval as FOREGROUND; every other open window earns
            // BACKGROUND. Totals are flushed every 10 polls and on close so the server
            // learns growing values (the row re-syncs via is_synced=0). This closes the
            // gap where browser sessions — the majority of usage — never earned any
            // foreground/background time at all (they are owned here, not by the main
            // collector loop, so its focus accounting never touched them).
            string? activeSessionId = null;
            foreach (var snap in snapshots)
            {
                if (!snap.IsActive) continue;
                var activeKey = ResolveWindowKey(snap, pidCounts, _lastPollPidCounts);
                if (_tracked.TryGetValue(activeKey, out var activeTw))
                {
                    activeSessionId = activeTw.SessionId;
                    break;
                }
            }

            var step = intervalSeconds;
            foreach (var kv in _tracked.ToList())
            {
                var tw = kv.Value;
                if (!present.Contains(kv.Key)) continue; // only windows seen this poll earn time
                var cur = _sessionFocusSeconds.TryGetValue(tw.SessionId, out var c) ? c : (0.0, 0.0);
                var isFg = !string.IsNullOrEmpty(activeSessionId) && tw.SessionId == activeSessionId;
                _sessionFocusSeconds[tw.SessionId] = isFg
                    ? (cur.Item1 + step, cur.Item2)
                    : (cur.Item1, cur.Item2 + step);
            }

            _pollCount++;
            if (_pollCount % 10 == 0)
                await FlushBrowserFocusAsync(ct);

            // Close windows that vanished (browser closed). Give a few polls for
            // transient a11y-tree misses before declaring the window gone.
            foreach (var kv in _tracked.ToList())
            {
                var tw = kv.Value;
                if (present.Contains(kv.Key)) continue;
                if (tw.LastSeen is { } lastSeen &&
                    (now - lastSeen).TotalSeconds >= intervalSeconds * MissingPollsToClose)
                {
                    _logger.LogDebug("Browser window closed (no longer visible): {Title}", tw.LastTitle);
                    await CloseWindowAsync(kv.Key, tw, now, ct);
                }
            }

            // Idle close: no URL/title activity for BrowserJourneyIdleMinutes.
            var idleLimit = TimeSpan.FromMinutes(Math.Max(1, _config.BrowserJourneyIdleMinutes));
            foreach (var kv in _tracked.ToList())
            {
                if ((now - kv.Value.LastActivity) >= idleLimit)
                {
                    _logger.LogDebug("Browser journey closed after {Min}min idle: {Title}",
                        idleLimit.TotalMinutes, kv.Value.LastTitle);
                    await CloseWindowAsync(kv.Key, kv.Value, now, ct);
                }
            }

            await _store.SetStatusAsync("browser_tracking_method", "accessibility", ct);

            _lastPollPidCounts = pidCounts;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Window-identity resolution. Browser windows do NOT expose a stable id everywhere:
    /// the AT-SPI registry path churns on navigation, macOS has no window id at all, and
    /// Firefox sessionstore window indices can shift. When a brand-new key arrives we try
    /// to prove it is an EXISTING window before opening a new session:
    ///   1. single tracked window with the same PID AND a matching page title → key churn;
    ///   2. multiple windows share the PID (Chrome/Edge/Firefox all do) → re-key ONLY when
    ///      the stripped page title matches, so one window can never steal another's session
    ///      (the old "exactly one match" rule let a second window hijack the first one's
    ///      session and alternate titles every poll — the source of the duplicate rows).
    ///   3. the incoming window is title-less (transient tree) → attach to the ONLY
    ///      same-pid tracked window; otherwise treat as a genuinely new window.
    ///   4. (Bug #8 — 2026-09-04) When the reader returns a NEW WindowKey for a window
    ///      that was just seen (within poll×2) under the same PID + same appDisplayName,
    ///      treat the new key as key churn of the FRESHEST same-PID tracked window.
    ///      This collapses chrome's "open 3 tabs in one window" case where each tab
    ///      gets a fresh WindowKey from the a11y tree and the old title-match rule let
    ///      each one open a separate session. The recency guard prevents two real
    ///      long-lived windows from collapsing when both have been open for a while.
    /// </summary>
    private string ResolveWindowKey(
        AccessibilitySnapshot snap,
        IReadOnlyDictionary<int, int> pidCounts,
        IReadOnlyDictionary<int, int> lastPidCounts)
    {
        if (_tracked.ContainsKey(snap.WindowKey)) return snap.WindowKey;
        if (snap.ProcessId <= 0) return snap.WindowKey;

        var samePid = _tracked.Where(kv => kv.Value.ProcessId == snap.ProcessId).ToList();
        if (samePid.Count == 0) return snap.WindowKey;

        var incoming = BrowserAccessibilityHelpers.StripBrowserSuffix(snap.WindowTitle, _browserRegistry?.GetDisplayName(snap.ProcessName)).Trim();
        var incomingUrl = (snap.Url ?? string.Empty).Trim();

        // Exact page-title match — identity is proven by (pid, page). Works for the
        // multi-window case (Chrome/Edge/Firefox share one PID) when a key churns
        // without a navigation.
        foreach (var kv in samePid)
        {
            var tracked = BrowserAccessibilityHelpers.StripBrowserSuffix(kv.Value.LastTitle, null).Trim();
            if (tracked.Length > 0 && string.Equals(tracked, incoming, StringComparison.OrdinalIgnoreCase))
                return ReKey(kv, snap.WindowKey, snap.ProcessId);
        }

        // Exact URL match — stronger than title (URLs are stable identifiers per page).
        // Helps collapse the "3 tabs, all titled 'Loading…'" transient case where the
        // title is empty for a few polls after a navigation. Only matches when both
        // sides have a non-empty URL — empty incoming URLs fall through to the rules
        // below to avoid accidentally merging genuine separate windows.
        if (incomingUrl.Length > 0)
        {
            foreach (var kv in samePid)
            {
                var trackedUrl = (kv.Value.LastUrl ?? string.Empty).Trim();
                if (trackedUrl.Length > 0 &&
                    string.Equals(trackedUrl, incomingUrl, StringComparison.OrdinalIgnoreCase))
                    return ReKey(kv, snap.WindowKey, snap.ProcessId);
            }
        }

        // Title-less snapshot (transient tree state) with a single tracked window:
        // safest assumption is key churn of that same window.
        if (incoming.Length == 0 && samePid.Count == 1)
            return ReKey(samePid[0], snap.WindowKey, snap.ProcessId);

        // A single tracked window whose window-count did NOT change between polls: a
        // changed title is a NAVIGATION of that same window (macOS exposes no stable
        // window id at all, so its key churns every poll; a single-window Chrome on
        // Wayland can too). Re-key so navigation never fragments the session. The count
        // guard is what prevents one window from STEALING another's session: when a
        // second window returns after a transient absence the count jumps 1→2 and the
        // guard fails, so a fresh session is opened instead of hijacking the first one.
        if (samePid.Count == 1 &&
            lastPidCounts.TryGetValue(snap.ProcessId, out var lastCount) && lastCount == 1 &&
            pidCounts.TryGetValue(snap.ProcessId, out var curCount) && curCount == 1)
            return ReKey(samePid[0], snap.WindowKey, snap.ProcessId);

        // Bug #8 — 2026-09-04: fresh-key churn. If a tracked same-PID window was seen
        // within the last (poll×2) seconds, treat the new key as churn of the FRESHEST
        // such window. This collapses chrome's "open tab in already-open window" case
        // where the a11y reader returns a fresh WindowKey per tab. The recency guard
        // is what keeps two real long-lived windows from collapsing: when both are old,
        // both LastSeen are far in the past and the guard fails on every candidate, so
        // a new session is opened instead.
        var recencyLimit = TimeSpan.FromSeconds(
            Math.Max(2, _config.BrowserAccessibilityPollSec * 2));
        var now = DateTime.UtcNow;
        var freshest = samePid
            .Where(kv => (now - kv.Value.LastSeen) <= recencyLimit)
            .OrderByDescending(kv => kv.Value.LastSeen)
            .FirstOrDefault();
        if (freshest.Value != null)
            return ReKey(freshest, snap.WindowKey, snap.ProcessId);

        return snap.WindowKey;
    }

    private string ReKey(KeyValuePair<string, TrackedWindow> kv, string newKey, int pid)
    {
        var tw = kv.Value;
        _tracked.Remove(kv.Key);
        _tracked[newKey] = tw;
        _logger.LogTrace("Re-keyed window {Old} → {New} (pid {Pid})", kv.Key, newKey, pid);
        return newKey;
    }

    private async Task OpenWindowAsync(string key, AccessibilitySnapshot snap, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var now = snap.CapturedAt;
        var url = ResolveStoredUrl(snap);

        string? installedAppId = null;
        string? displayName = null;
        try
        {
            var app = await _store.GetInstalledAppByBinaryNameFuzzyAsync(snap.ProcessName, ct)
                      ?? await _store.GetInstalledAppByBinaryNameAsync(snap.ProcessName, ct);
            installedAppId = app?.Id;
            displayName = app?.AppName;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Installed-app lookup failed for {Proc}", snap.ProcessName);
        }

        var session = new AppSession
        {
            Id = sessionId,
            ProcessName = snap.ProcessName,
            AppDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? BrowserAccessibilityHelpers.StripBrowserSuffix(snap.WindowTitle, _browserRegistry?.GetDisplayName(snap.ProcessName))
                : displayName,
            StartedAt = now,
            MachineId = _config.ClientId,
            EmployeeId = _employeeId,
            EmployeeName = _employeeName,
            SessionId = SessionInfo.SessionId,
            Platform = _reader.Platform,
            InstalledAppId = installedAppId,
            ProcessId = snap.ProcessId,
            ContextLabel = snap.WindowTitle,
        };

        var rootItem = new AppItem
        {
            AppSessionId = sessionId,
            ItemType = "browser_tab",
            Title = string.IsNullOrWhiteSpace(snap.WindowTitle) ? "Browser Window" : snap.WindowTitle,
            Identifier = key,
            Url = url,
            Domain = BrowserAccessibilityHelpers.ExtractDomain(url),
            OpenedAt = now,
            ProcessId = snap.ProcessId,
            ObjectType = "Tab",
            Action = "open",
            JourneyId = sessionId,
            Sequence = 1,
            CurrentPath = url,
            WindowId = BrowserAccessibilityHelpers.StableInt32(key),
            MetadataJson = BuildMetadata(snap, key),
        };

        await _store.StoreAppSessionsAsync(new[] { session }, ct);
        await _store.StoreAppItemsAsync(new[] { rootItem }, ct);

        _tracked[key] = new TrackedWindow
        {
            SessionId = sessionId,
            RootItemId = rootItem.Id,
            JourneyId = sessionId,
            ProcessId = snap.ProcessId,
            LastUrl = url,
            LastTitle = snap.WindowTitle,
            IsIncognito = snap.IsIncognito,
            LastActivity = now,
            LastSeen = now,
        };

        // Debug level: emitted once per window open (and on every re-open), so it would
        // flood the terminal during `dotnet run`. The DB is the source of truth for
        // journeys; set ALPHA_LOG_LEVEL=debug to watch them live.
        _logger.LogDebug(
            "Browser journey opened via accessibility: {App} | {Title} | {Url}",
            session.AppDisplayName, snap.WindowTitle, string.IsNullOrEmpty(url) ? "(no url)" : url);
    }

    private async Task UpdateWindowAsync(TrackedWindow tw, AccessibilitySnapshot snap, CancellationToken ct)
    {
        var url = ResolveStoredUrl(snap);
        var titleChanged = !string.Equals(tw.LastTitle, snap.WindowTitle, StringComparison.Ordinal);
        var urlChanged = !string.Equals(tw.LastUrl ?? string.Empty, url, StringComparison.Ordinal);

        if (!titleChanged && !urlChanged)
            return;

        var now = snap.CapturedAt;

        // Title-only change: wait for the title to persist ~MinTabRotationInterval before
        // rotating the tab record, so badge/timer/player-state flicker never fragments the
        // journey. A real URL change (true navigation) rotates immediately. LastTitle is
        // NOT advanced during the hold, so the next poll still sees the pending change and
        // the video page is never silently merged into the previous page's record.
        //
        // History-lag case: the title already changed but the history-DB fallback has not
        // flushed the new URL yet (enrichment returned empty). Wait for the URL on the
        // FIRST poll that carries it (an immediate rotation instead of a spurious
        // empty-URL tab record) — but the wait is BOUNDED by HistoryLagTimeout: private
        // windows and URL-less pages never produce a URL, and an unbounded hold would
        // freeze the tab on the previous record, silently dropping every later
        // same-tab navigation.
        var historyLag = string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(tw.LastUrl);
        var pendingDriven = false; // rotation decided by the stability/lag window below
        if (titleChanged && (!urlChanged || historyLag))
        {
            var stable = string.Equals(snap.WindowTitle, tw.PendingTitle, StringComparison.Ordinal)
                         && now - tw.PendingSinceUtc >= MinTabRotationInterval;
            // Ready to rotate: either the title has been stable for the anti-flicker
            // window (and no URL is pending), or the history-lag grace period expired
            // (the URL is never coming — rotate on the title alone).
            var lagExpired = historyLag && now - tw.PendingSinceUtc >= HistoryLagTimeout;
            var ready = (!historyLag && stable) || lagExpired;
            if (!ready)
            {
                if (!string.Equals(snap.WindowTitle, tw.PendingTitle, StringComparison.Ordinal))
                {
                    tw.PendingTitle = snap.WindowTitle;
                    tw.PendingSinceUtc = now;
                }
                tw.LastActivity = now;
                return;
            }

            if (historyLag && !stable)
            {
                // Title kept changing through the whole lag window — a fresh navigation
                // started; restart the pending window for the new title.
                tw.PendingTitle = snap.WindowTitle;
                tw.PendingSinceUtc = now;
                tw.LastActivity = now;
                return;
            }

            pendingDriven = true;
        }

        var newTitle = string.IsNullOrWhiteSpace(snap.WindowTitle) ? tw.LastTitle : snap.WindowTitle;
        // Accurate page-start time: for a title/lag-driven rotation this is when the new
        // title FIRST appeared (not when the stability/lag window elapsed); for a
        // URL-driven rotation it is the poll that observed the new URL.
        var openedAt = pendingDriven ? tw.PendingSinceUtc : now;

        // A persisted blank/unchanged title (transient load state) is not a new page —
        // don't rotate a duplicate-looking record.
        if (!urlChanged && string.Equals(newTitle, tw.LastTitle, StringComparison.Ordinal))
        {
            tw.PendingTitle = null;
            tw.PendingSinceUtc = now;
            tw.LastActivity = now;
            return;
        }

        // Each page visit gets its OWN browser_tab record — never overwrite the previous
        // page's title in place (that made "YouTube - Google Chrome" silently become the
        // video page). Close the current tab root keeping its ORIGINAL title/url/metadata
        // intact, then open a fresh root for the new page. The window session itself stays
        // open across navigations; browser_navigation children still record transitions.
        var newRootId = Guid.NewGuid().ToString("N");

        _logger.LogDebug("Browser tab rotated: '{From}' → '{To}' (urlChanged={UrlChanged})",
            tw.LastTitle, newTitle, urlChanged);

        // Dedicated close (sets is_synced = 0): a bare upsert would NOT reset is_synced,
        // so if the tab row was already synced the server would never learn it closed.
        // closed_at = the new page's opened_at for clean record continuity.
        await _store.CloseAppItemAsync(tw.RootItemId, openedAt, ct);

        await _store.StoreAppItemsAsync(new[]
        {
            new AppItem
            {
                Id = newRootId,
                AppSessionId = tw.SessionId,
                ItemType = "browser_tab",
                Title = newTitle,
                Identifier = newRootId,
                Url = url,
                Domain = BrowserAccessibilityHelpers.ExtractDomain(url),
                OpenedAt = openedAt,
                ProcessId = tw.ProcessId,
                ObjectType = "Tab",
                Action = "open",
                JourneyId = tw.JourneyId,
                Sequence = 1,
                CurrentPath = url,
                WindowId = BrowserAccessibilityHelpers.StableInt32(tw.SessionId),
                MetadataJson = BuildMetadata(snap, newRootId),
            }
        }, ct);

        // Record a navigation event when the address-bar URL changed.
        if (urlChanged && !string.IsNullOrEmpty(url) && !string.IsNullOrWhiteSpace(tw.LastUrl))
        {
            int sequence = 1;
            try { sequence = await _store.GetNextSequenceAsync(tw.JourneyId, ct); } catch { }

            await _store.StoreAppItemsAsync(new[]
            {
                new AppItem
                {
                    AppSessionId = tw.SessionId,
                    ParentItemId = newRootId,
                    ItemType = "browser_navigation",
                    Title = snap.WindowTitle,
                    Identifier = url,
                    Url = url,
                    Domain = BrowserAccessibilityHelpers.ExtractDomain(url),
                    OpenedAt = now,
                    ProcessId = tw.ProcessId,
                    ObjectType = "Page",
                    Action = "navigate",
                    JourneyId = tw.JourneyId,
                    Sequence = sequence,
                    PreviousPath = tw.LastUrl ?? string.Empty,
                    CurrentPath = url,
                    WindowId = BrowserAccessibilityHelpers.StableInt32(tw.SessionId),
                    MetadataJson = BuildMetadata(snap, newRootId),
                }
            }, ct);

            _logger.LogDebug("Browser navigation: {From} → {To}", tw.LastUrl, url);
        }

        tw.RootItemId = newRootId;
        tw.PendingTitle = null;
        tw.PendingSinceUtc = now;
        tw.LastUrl = url;
        tw.LastTitle = snap.WindowTitle;
        tw.LastActivity = now;
    }

    private async Task CloseWindowAsync(string key, TrackedWindow tw, DateTime closedAt, CancellationToken ct)
    {
        // Persist the final focus totals before closing so the row carries them (the
        // close itself only sets ended_at; totals ride on the pre-close flush).
        try { await FlushBrowserFocusAsync(ct); } catch { }
        await _store.CloseSessionsAndAppItemsAsync(
            new[] { new AppSession { Id = tw.SessionId, ProcessName = string.Empty, EndedAt = closedAt } },
            closedAt, ct);
        _tracked.Remove(key);
    }

    /// <summary>
    /// Persist accumulated foreground/background focus totals for all open browser
    /// sessions and re-queue each row (is_synced = 0) so SyncService re-sends it and
    /// the server learns the growing values. On failure the counters are kept and
    /// retried next flush — nothing is lost.
    /// </summary>
    private async Task FlushBrowserFocusAsync(CancellationToken ct)
    {
        if (_sessionFocusSeconds.Count == 0) return;
        var updates = _sessionFocusSeconds
            .Select(kv => (kv.Key, kv.Value.Item1, kv.Value.Item2))
            .ToList();
        try
        {
            await _store.UpdateAppSessionFocusAsync(updates, ct);
            _sessionFocusSeconds.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to flush browser session focus durations (will retry next poll)");
        }
    }

    private async Task CloseAllAsync(DateTime closedAt, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var kv in _tracked.ToList())
                await CloseWindowAsync(kv.Key, kv.Value, closedAt, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Incognito URLs are dropped unless explicitly enabled (legal-safe default).</summary>
    private string ResolveStoredUrl(AccessibilitySnapshot snap)
    {
        if (snap.IsIncognito && !_config.BrowserCaptureIncognito)
            return string.Empty;
        return snap.Url ?? string.Empty;
    }

    /// <summary>
    /// Hybrid URL recovery: when the OS accessibility tree cannot expose the omnibox URL
    /// (Linux Chrome 136+ built without --force-renderer-accessibility; snap Firefox is
    /// AppArmor-blocked), fall back to the browser's OWN profile history database — read
    /// while the browser is running, no restart needed. Windows/macOS readers usually
    /// already return the URL, so the history path only fills the empty gaps.
    /// </summary>
    private async Task<IReadOnlyList<AccessibilitySnapshot>> EnrichUrlsFromHistoryAsync(
        IReadOnlyList<AccessibilitySnapshot> snapshots, CancellationToken ct)
    {
        if (_history is null || !_config.BrowserHistoryEnabled)
            return snapshots;

        // Fast path: every snapshot already has a URL (Windows/macOS readers) — nothing to do.
        if (snapshots.All(s => !string.IsNullOrWhiteSpace(s.Url)))
            return snapshots;

        _history.Refresh();

        var result = new List<AccessibilitySnapshot>(snapshots.Count);
        foreach (var snap in snapshots)
        {
            if (!string.IsNullOrWhiteSpace(snap.Url))
            {
                result.Add(snap);
                continue;
            }

            try
            {
                var visit = _history.TryResolveUrl(snap.ProcessName, snap.WindowTitle, snap.CapturedAt);
                if (visit is null || string.IsNullOrWhiteSpace(visit.Url))
                {
                    result.Add(snap);
                    continue;
                }

                var url = BrowserAccessibilityHelpers.NormalizeUrl(visit.Url);
                if (string.IsNullOrWhiteSpace(url))
                {
                    result.Add(snap);
                    continue;
                }

                result.Add(new AccessibilitySnapshot
                {
                    WindowKey = snap.WindowKey,
                    ProcessId = snap.ProcessId,
                    ProcessName = snap.ProcessName,
                    WindowTitle = snap.WindowTitle,
                    Url = url,
                    UrlSource = "history",
                    IsIncognito = snap.IsIncognito,
                    IsActive = snap.IsActive,
                    CapturedAt = snap.CapturedAt,
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "History URL enrichment failed for {Title}", snap.WindowTitle);
                result.Add(snap);
            }
        }
        return result;
    }

    private string BuildMetadata(AccessibilitySnapshot snap, string windowKey) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            // Embedded webview windows (Electron apps) are tagged "webview" so the web
            // dashboard can show the HOST APP as the source (VS Code, Slack, …). Real
            // browsers keep their platform source (accessibility/history/sessionstore).
            ["source"] = snap.IsWebview ? "webview" : snap.UrlSource,
            ["windowKey"] = windowKey,
            ["incognito"] = snap.IsIncognito,
            ["processName"] = snap.ProcessName,
            ["capturedAt"] = snap.CapturedAt.ToString("O"),
        });

    private async Task RefreshEmployeeInfoAsync(CancellationToken ct)
    {
        try
        {
            var info = await _store.GetEmployeeInfoAsync(ct);
            _employeeId = info?.EmployeeId;
            _employeeName = info?.Name;
            _lastEmployeeRefresh = DateTime.UtcNow;
        }
        catch
        {
            _employeeId = null;
            _employeeName = null;
        }
    }

    private int intervalSeconds => Math.Max(2, _config.BrowserAccessibilityPollSec);

    // ─── Downloads watcher (part of the journey: files saved from the browser) ───

    private void StartDownloadWatchers()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = new[]
            {
                Path.Combine(home, "Downloads"),
                Path.Combine(home, "downloads"),
            };
            foreach (var p in paths.Distinct())
            {
                try
                {
                    if (!Directory.Exists(p)) continue;
                    var fsw = new FileSystemWatcher(p)
                    {
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
                    };
                    fsw.Created += OnDownloadFileCreated;
                    _downloadWatchers.Add(fsw);
                    _logger.LogDebug("Download watcher registered for {Path}", p);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to start download watcher for {Path}", p);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to initialize download watchers");
        }
    }

    private async void OnDownloadFileCreated(object? sender, FileSystemEventArgs e)
    {
        try
        {
            if (_stopping) return;

            // Skip temp/partial files (Chrome uses .crdownload, browsers use .part).
            var ext = Path.GetExtension(e.FullPath);
            if (ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                return;

            await Task.Delay(800);
            var fi = new FileInfo(e.FullPath);
            if (!fi.Exists) return;

            TrackedWindow? target = null;
            await _gate.WaitAsync();
            try
            {
                target = _tracked.Values
                    .OrderByDescending(t => t.LastActivity)
                    .FirstOrDefault();
            }
            finally
            {
                _gate.Release();
            }
            if (target == null)
            {
                _logger.LogDebug("Download recorded without an active browser session (skipped): {File}", e.FullPath);
                return;
            }

            int sequence = 1;
            try { sequence = await _store.GetNextSequenceAsync(target.JourneyId, CancellationToken.None); } catch { }

            var item = new AppItem
            {
                AppSessionId = target.SessionId,
                ParentItemId = target.RootItemId,
                ItemType = "browser_download",
                Title = fi.Name,
                Identifier = e.FullPath,
                CurrentPath = e.FullPath,
                OpenedAt = DateTime.UtcNow,
                ProcessId = target.ProcessId,
                ObjectType = "Download",
                Action = "download",
                JourneyId = target.JourneyId,
                Sequence = sequence,
                WindowId = BrowserAccessibilityHelpers.StableInt32(target.SessionId),
                MetadataJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["source"] = "downloads-watcher",
                    ["path"] = e.FullPath,
                }),
            };
            await _store.StoreAppItemsAsync(new[] { item }, CancellationToken.None);
            _logger.LogDebug("Recorded browser download: {File}", fi.Name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Download watcher handler failed for {File}", e.FullPath);
        }
    }
}
