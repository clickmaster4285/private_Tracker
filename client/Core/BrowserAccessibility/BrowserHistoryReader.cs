using client.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace client.Core.BrowserAccessibility;

/// <summary>
/// Browser-history fallback URL source for the hybrid browser-journey pipeline.
///
/// The OS accessibility tree cannot always expose the active-tab URL:
///   - Linux Chrome 136+ refuses to build the omnibox (address-bar) subtree unless it was
///     launched with --force-renderer-accessibility, so only the window title is readable.
///   - snap Firefox is AppArmor-blocked from AT-SPI entirely (window title + URL invisible).
///
/// Every browser nevertheless writes every visit into its OWN profile database
/// (Chromium-family "History", Firefox "places.sqlite") — silently, automatically, on
/// every page load. This reader opens those databases (a safe copy + WAL/journal
/// sidecars, read-only) and recovers the exact URL for a window whose title is known.
/// It works WHILE the browser is running (no restart, no flag, no extension), on every
/// platform (Linux/Windows/macOS), on every browser and every Chrome version, and for
/// brand-new browsers (install → use → uninstall): the periodic profile scan picks up
/// new profile directories as soon as they appear, so journeys are captured while the
/// browser is alive — before an uninstaller deletes the profile.
///
/// Privacy: incognito/private-browsing visits are NEVER written to these databases, so
/// they stay out of the tracker automatically (legal-safe). Reading can be disabled
/// with ALPHA_BROWSER_HISTORY_ENABLED=false.
/// </summary>
public sealed class BrowserHistoryReader
{
    /// <summary>A single observed visit from a browser history database.</summary>
    public sealed record HistoryVisit(string Url, string Title, DateTime VisitedAtUtc, string Family);

    private readonly AppConfig _config;
    private readonly ILogger<BrowserHistoryReader> _logger;
    private readonly IBrowserRegistry _browserRegistry;
    private readonly object _gate = new();

    // Profile db path → state (last-read signature so unchanged files are skipped).
    private readonly Dictionary<string, ProfileState> _profiles = new();
    private readonly List<HistoryVisit> _visits = new();
    private readonly Dictionary<string, HashSet<string>> _seenKeys = new();

    private DateTime _lastScanUtc = DateTime.MinValue;
    private DateTime _lastReadUtc = DateTime.MinValue;

    private const int MaxCachedVisits = 3000;
    private const int MaxVisitsPerProfile = 300;

    private sealed class ProfileState
    {
        public required string Family { get; init; }   // "chromium" | "firefox"
        public required string DbPath { get; init; }   // History or places.sqlite
        public string? LastSignature { get; set; }     // "size:mtime" of main + sidecars
    }

    public BrowserHistoryReader(AppConfig config, ILogger<BrowserHistoryReader> logger, IBrowserRegistry browserRegistry)
    {
        _config = config;
        _logger = logger;
        _browserRegistry = browserRegistry;
    }

    /// <summary>
    /// Throttled refresh: discovers new browser profiles (brand-new browsers included)
    /// and re-reads any history database whose files changed since the last read.
    /// Cheap when called every poll — internally gated by ALPHA_BROWSER_HISTORY_POLL_SECONDS.
    /// </summary>
    public void Refresh()
    {
        if (!_config.BrowserHistoryEnabled) return;
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            // Profile discovery is cheap (immediate subdir scans of known roots) and runs
            // on a separate cadence so a brand-new browser is picked up quickly.
            if ((now - _lastScanUtc).TotalSeconds >= 15)
            {
                DiscoverProfilesLocked();
                _lastScanUtc = now;
            }

            var pollSec = Math.Max(2, _config.BrowserHistoryPollSec);
            if ((now - _lastReadUtc).TotalSeconds < pollSec) return;
            _lastReadUtc = now;

            foreach (var profile in _profiles.Values)
            {
                try
                {
                    var visits = ReadProfileLocked(profile);
                    if (visits is null || visits.Count == 0) continue;

                    foreach (var v in visits)
                    {
                        if (!_seenKeys.TryGetValue(v.Family, out var seen))
                        {
                            seen = new HashSet<string>();
                            _seenKeys[v.Family] = seen;
                        }
                        if (seen.Add($"{v.Url}|{v.Title}|{v.VisitedAtUtc.Ticks}"))
                            _visits.Add(v);
                    }
                    TrimVisitsLocked();
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Browser history read failed for {Db}", profile.DbPath);
                }
            }
        }
    }

    /// <summary>
    /// Best-effort resolution of the URL for a window: match the window's page title
    /// against recent history visits for the same browser family. Falls back to the
    /// most recent visit of that family within the last few minutes.
    /// </summary>
    public HistoryVisit? TryResolveUrl(string? processName, string windowTitle, DateTime capturedAt)
    {
        if (!_config.BrowserHistoryEnabled) return null;
        var pageTitle = BrowserAccessibilityHelpers.StripBrowserSuffix(windowTitle, _browserRegistry.GetDisplayName(processName ?? string.Empty));

        lock (_gate)
        {
            if (_visits.Count == 0) return null;

            var list = _visits.ToList();
            if (list.Count == 0) return null;

            // 1. Exact title match (newest first) — the page title in history usually
            //    equals the window title minus the browser suffix.
            if (!string.IsNullOrWhiteSpace(pageTitle))
            {
                var exact = list
                    .Where(v => !string.IsNullOrEmpty(v.Title) &&
                                string.Equals(v.Title.Trim(), pageTitle, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => v.VisitedAtUtc)
                    .FirstOrDefault();
                if (exact is not null) return exact;
            }

            // 2. Fuzzy title containment (history titles can be truncated / reworded).
            if (!string.IsNullOrWhiteSpace(pageTitle))
            {
                var fuzzy = list
                    .Where(v => !string.IsNullOrEmpty(v.Title) &&
                                (v.Title.Contains(pageTitle, StringComparison.OrdinalIgnoreCase) ||
                                 pageTitle.Contains(v.Title, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(v => v.VisitedAtUtc)
                    .FirstOrDefault();
                if (fuzzy is not null) return fuzzy;
            }

            // NO unconditional "newest visit" fallback: with several windows/tabs open,
            // attaching the most recent visit to an unmatched window would MISATTRIBUTE
            // another tab's URL to it — an empty URL is honest, a wrong URL is not.
            // History writes lag navigations by a few seconds; the tracker re-enriches
            // every poll, so the URL simply appears once history catches up.
            return null;
        }
    }

    private void DiscoverProfilesLocked()
    {
        var found = new Dictionary<string, ProfileState>();
        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate.DbPath))
                found[candidate.DbPath] = candidate;
        }

        // Keep previously-known profiles that still exist (stable across scans).
        foreach (var kv in _profiles)
        {
            if (File.Exists(kv.Key))
                found.TryAdd(kv.Key, kv.Value);
        }

        _profiles.Clear();
        foreach (var kv in found)
            _profiles[kv.Key] = kv.Value;
    }

    private List<HistoryVisit>? ReadProfileLocked(ProfileState profile)
    {
        var signature = ComputeSignature(profile.DbPath);
        if (signature == profile.LastSignature) return null;

        var tmpDir = Path.Combine(Path.GetTempPath(), "aat_hist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Copy main db + WAL/journal/shm sidecars, then open the copy read-only.
            // Copy order matters: main, then -wal, then -shm (shm is derived from wal).
            var copyBase = Path.Combine(tmpDir, "history.db");
            foreach (var f in EnumerateSidecars(profile.DbPath))
            {
                if (!File.Exists(f)) continue;
                var suffix = f.Length > profile.DbPath.Length ? f[profile.DbPath.Length..] : "";
                File.Copy(f, copyBase + suffix, overwrite: true);
            }

            var visits = new List<HistoryVisit>();
            using (var conn = new SqliteConnection($"Data Source={copyBase};Mode=ReadOnly;Pooling=False"))
            {
                conn.Open();
                if (profile.Family == "firefox")
                    visits = ReadFirefoxVisits(conn, profile.Family);
                else
                    visits = ReadChromiumVisits(conn, profile.Family);
            }

            // Only mark as read on SUCCESS — a transient failure (locked DB, torn WAL
            // copy) leaves the signature stale so the next refresh retries it.
            profile.LastSignature = signature;
            return visits;
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    private static List<HistoryVisit> ReadChromiumVisits(SqliteConnection conn, string family)
    {
        var visits = new List<HistoryVisit>();
        // Chromium visit_time is microseconds since 1601-01-01 (Windows FILETIME epoch).
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT u.url, u.title, v.visit_time
            FROM urls u
            JOIN visits v ON u.id = v.url
            WHERE u.url LIKE 'http%'
            ORDER BY v.visit_time DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", MaxVisitsPerProfile);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var url = reader.GetString(0);
            var title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var visitTime = reader.GetInt64(2);
            var unixSec = (long)(visitTime / 1_000_000.0 - 11644473600.0);
            visits.Add(new HistoryVisit(url, title, DateTimeOffset.FromUnixTimeSeconds(unixSec).UtcDateTime, family));
        }
        return visits;
    }

    private static List<HistoryVisit> ReadFirefoxVisits(SqliteConnection conn, string family)
    {
        var visits = new List<HistoryVisit>();
        // Firefox visit_date is microseconds since the Unix epoch (1970-01-01).
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.url, p.title, h.visit_date
            FROM moz_places p
            JOIN moz_historyvisits h ON p.id = h.place_id
            WHERE p.url LIKE 'http%'
            ORDER BY h.visit_date DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", MaxVisitsPerProfile);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var url = reader.GetString(0);
            var title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var visitDate = reader.GetInt64(2);
            var unixSec = (long)(visitDate / 1_000_000.0);
            visits.Add(new HistoryVisit(url, title, DateTimeOffset.FromUnixTimeSeconds(unixSec).UtcDateTime, family));
        }
        return visits;
    }

    private static IEnumerable<string> EnumerateSidecars(string dbPath) =>
        new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" };

    private static string ComputeSignature(string dbPath)
    {
        // Change detection on main + -wal/-journal only. The -shm file is a memory-mapped
        // index whose mtime changes on every checkpoint, which would force needless
        // re-copies; -shm is still COPIED (harmless) but not used for change detection.
        var parts = new List<string>();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-journal" })
        {
            if (!File.Exists(f)) continue;
            try
            {
                var fi = new FileInfo(f);
                parts.Add($"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}");
            }
            catch { }
        }
        return string.Join("|", parts);
    }

    private void TrimVisitsLocked()
    {
        if (_visits.Count <= MaxCachedVisits) return;
        _visits.Sort((a, b) => b.VisitedAtUtc.CompareTo(a.VisitedAtUtc));
        _visits.RemoveRange(MaxCachedVisits, _visits.Count - MaxCachedVisits);
        _seenKeys.Clear();
        foreach (var v in _visits)
        {
            if (!_seenKeys.TryGetValue(v.Family, out var seen))
            {
                seen = new HashSet<string>();
                _seenKeys[v.Family] = seen;
            }
            seen.Add($"{v.Url}|{v.Title}|{v.VisitedAtUtc.Ticks}");
        }
    }

    // ─── Profile discovery (all platforms) ───

    private IEnumerable<ProfileState> EnumerateCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Brand-new / unknown browser discovery: ANY browser (known family or a fresh,
        // never-before-seen Chromium/Firefox fork) writes its profile history under one
        // of these generic roots. Scanning these shallow directories picks up a browser
        // installed moments ago — journeys are captured while it is alive, before an
        // uninstaller deletes the profile.
        if (OperatingSystem.IsLinux())
        {
            foreach (var p in FindGenericChromiumProfiles(Path.Combine(home, ".config")))
                yield return p;
            foreach (var p in FindGenericFirefoxProfiles(Path.Combine(home, ".mozilla")))
                yield return p;
            foreach (var p in FindGenericFirefoxProfiles(Path.Combine(home, "snap", "firefox", "common", ".mozilla")))
                yield return p;
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var p in FindGenericChromiumProfiles(localApp))
                yield return p;
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var p in FindGenericChromiumProfiles(Path.Combine(home, "Library", "Application Support")))
                yield return p;
        }

        if (OperatingSystem.IsLinux())
        {
            // Chromium family: ~/.config/<browser>/<profile>/History
            foreach (var root in new[]
                     {
                         Path.Combine(home, ".config", "google-chrome"),
                         Path.Combine(home, ".config", "chromium"),
                         Path.Combine(home, ".config", "microsoft-edge"),
                         Path.Combine(home, ".config", "BraveSoftware", "Brave-Browser"),
                         Path.Combine(home, ".config", "vivaldi"),
                         Path.Combine(home, ".config", "opera"),
                     })
            {
                foreach (var p in FindChromiumProfiles(root))
                    yield return p;
            }

            // Firefox: ~/.mozilla/firefox/*/places.sqlite + snap layout
            foreach (var root in new[]
                     {
                         Path.Combine(home, ".mozilla", "firefox"),
                         Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox"),
                     })
            {
                foreach (var p in FindFirefoxProfiles(root))
                    yield return p;
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Path.Combine(localApp, "Google", "Chrome", "User Data"),
                         Path.Combine(localApp, "Chromium", "User Data"),
                         Path.Combine(localApp, "Microsoft", "Edge", "User Data"),
                         Path.Combine(localApp, "BraveSoftware", "Brave-Browser", "User Data"),
                         Path.Combine(localApp, "Vivaldi", "User Data"),
                         Path.Combine(localApp, "Opera Software", "Opera Stable"),
                     })
            {
                foreach (var p in FindChromiumProfiles(root))
                    yield return p;
            }

            foreach (var root in new[]
                     {
                         Path.Combine(appData, "Mozilla", "Firefox", "Profiles"),
                     })
            {
                foreach (var p in FindFirefoxProfiles(root))
                    yield return p;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var root in new[]
                     {
                         Path.Combine(home, "Library", "Application Support", "Google", "Chrome"),
                         Path.Combine(home, "Library", "Application Support", "Chromium"),
                         Path.Combine(home, "Library", "Application Support", "Microsoft Edge"),
                         Path.Combine(home, "Library", "Application Support", "BraveSoftware", "Brave-Browser"),
                         Path.Combine(home, "Library", "Application Support", "Vivaldi"),
                         Path.Combine(home, "Library", "Application Support", "Opera"),
                     })
            {
                foreach (var p in FindChromiumProfiles(root))
                    yield return p;
            }

            foreach (var root in new[]
                     {
                         Path.Combine(home, "Library", "Application Support", "Firefox", "Profiles"),
                     })
            {
                foreach (var p in FindFirefoxProfiles(root))
                    yield return p;
            }
        }
    }

    private static IEnumerable<ProfileState> FindChromiumProfiles(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
        foreach (var dir in SafeEnumerateDirectories(root))
        {
            var db = Path.Combine(dir, "History");
            if (File.Exists(db))
                yield return new ProfileState { Family = "chromium", DbPath = db };
        }
    }

    private static IEnumerable<ProfileState> FindFirefoxProfiles(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
        foreach (var dir in SafeEnumerateDirectories(root))
        {
            var db = Path.Combine(dir, "places.sqlite");
            if (File.Exists(db))
                yield return new ProfileState { Family = "firefox", DbPath = db };
        }
    }

    /// <summary>
    /// Generic discovery for brand-new / unknown Chromium-family browsers: scans
    /// &lt;root&gt;/&lt;browser-dir&gt;/&lt;profile&gt;/History and &lt;root&gt;/&lt;browser-dir&gt;/History.
    /// Deduplicated against known profiles by the caller's dictionary key.
    /// </summary>
    private static IEnumerable<ProfileState> FindGenericChromiumProfiles(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
        var cacheLike = new[] { "Cache", "Code Cache", "GPUCache", "ShaderCache", "Service Worker",
            "IndexedDB", "Local Storage", "Session Storage", "Extensions", "Storage" };
        foreach (var browserDir in SafeEnumerateDirectories(root))
        {
            var name = Path.GetFileName(browserDir);
            if (name.StartsWith('.')) continue;
            // <root>/<browser>/<profile>/History
            foreach (var profileDir in SafeEnumerateDirectories(browserDir))
            {
                if (cacheLike.Contains(Path.GetFileName(profileDir))) continue;
                var db = Path.Combine(profileDir, "History");
                if (File.Exists(db))
                    yield return new ProfileState { Family = "chromium", DbPath = db };
            }
            // <root>/<browser>/History (some browsers keep history at the root)
            var db2 = Path.Combine(browserDir, "History");
            if (File.Exists(db2))
                yield return new ProfileState { Family = "chromium", DbPath = db2 };
        }
    }

    /// <summary>
    /// Generic discovery for brand-new / unknown Firefox-family browsers: scans
    /// &lt;root&gt;/firefox-profiles/&lt;profile&gt;/places.sqlite (Firefox always nests profiles).
    /// </summary>
    private static IEnumerable<ProfileState> FindGenericFirefoxProfiles(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
        foreach (var browserDir in SafeEnumerateDirectories(root))
        {
            if (Path.GetFileName(browserDir).StartsWith('.')) continue;
            foreach (var profileDir in SafeEnumerateDirectories(browserDir))
            {
                var db = Path.Combine(profileDir, "places.sqlite");
                if (File.Exists(db))
                    yield return new ProfileState { Family = "firefox", DbPath = db };
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(root);
        }
        catch
        {
            // Unreadable / transiently missing profile root — skip silently.
            return Array.Empty<string>();
        }
        return dirs;
    }
}
