using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace client.Core.BrowserAccessibility;

/// <summary>
/// Linux implementation of <see cref="IAccessibilityBrowserReader"/>.
///
/// ONE embedded python3 probe produces every window that is visible to the OS, from
/// three independent sources merged into a single snapshot list:
///
///   A) AT-SPI accessibility tree (D-Bus) — the source for a11y-visible browsers
///      (Chrome, Chromium, Brave, Edge, … on Wayland AND X11). Yields the exact
///      omnibox URL when the browser exposes it (older Chrome; Firefox on X11)
///      plus the page title and an incognito flag.
///   B) Firefox sessionstore files (recovery.jsonlz4 / sessionstore.jsonlz4) —
///      the ONLY source that survives snap Firefox's AppArmor sandbox (it blocks
///      every inbound D-Bus call, so AT-SPI cannot see the window). Firefox writes
///      its open windows/tabs (title + exact URL, including the active tab) into
///      its own profile every ~15 s; the file is plain-readable. Decompressed
///      with an embedded pure-python LZ4 block decoder (no external module).
///   C) The window-manager window list (GNOME Shell Introspect where permitted,
///      otherwise xprop _NET_CLIENT_LIST) — gives every window a STABLE id
///      (X11/XWayland windows; Shell ids on supported desktops), which fixes the
///      AT-SPI registry-path churn that fragmented journeys, and surfaces
///      X11-only browsers that AT-SPI cannot see.
///
/// Windows that only the WM sees (title-only) are emitted anyway — the
/// <see cref="BrowserHistoryReader"/> fallback resolves their URL later. The
/// tracker never sees an a11y-invisible browser disappear again.
/// </summary>
public sealed class LinuxAtSpiBrowserReader : IAccessibilityBrowserReader
{
    private readonly ILogger<LinuxAtSpiBrowserReader> _logger;
    private readonly IBrowserRegistry _browserRegistry;
    private bool? _checked;
    private int _pollCount;

    // Embedded-webview cache: the expensive non-browser tree walk runs every 5th poll;
    // between scans the reader re-emits the last-known webview windows so the tracker
    // sees them EVERY poll (stable sessions, focus accounting, no false missing-window
    // close). Entries expire ~60s without a successful rescan.
    private sealed record CachedWebview(int Pid, string ProcessName, string Title, string Url, bool Active);
    private readonly Dictionary<string, (CachedWebview Wv, DateTime LastSeenUtc)> _webviewCache = new();
    // PID->resolved-name cache populated from AT-SPI probe results each poll.
    // Used by ReadComm() for WM-only windows where /proc/comm may give a
    // Flatpak/snap proxy name (e.g. xdg-dbus-proxy) instead of the real app.
    private readonly Dictionary<int, string> _pidNameCache = new();

    public string Platform => "Linux";
    public bool IsAvailable => OperatingSystem.IsLinux();

    public LinuxAtSpiBrowserReader(ILogger<LinuxAtSpiBrowserReader> logger, IBrowserRegistry browserRegistry)
    {
        _logger = logger;
        _browserRegistry = browserRegistry;
    }

    public async Task<IReadOnlyList<AccessibilitySnapshot>> ReadAsync(CancellationToken ct)
    {
        var result = new List<AccessibilitySnapshot>();
        if (!OperatingSystem.IsLinux() || ct.IsCancellationRequested)
            return result;

        var now = DateTime.UtcNow;

        // ── Source C: WM window list (fast; stable ids; also covers X11-only browsers) ──
        var wmWindows = EnumerateWmWindows();

        // ── Sources A + B: one python3 probe (AT-SPI + Firefox sessionstore) ──
        List<ProbeWindow> probe = new();
        try
        {
            probe = await RunCombinedProbeAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Combined browser probe failed");
        }

        var wmIndexed = IndexWmWindows(wmWindows);

        // Populate PID->name cache from AT-SPI probe results so ReadComm() can
        // resolve Flatpak/snap proxy PIDs to the real app name for WM-only windows.
        foreach (var w in probe)
        {
            if (w.Pid > 0 && !string.IsNullOrEmpty(w.ProcessName))
                _pidNameCache[w.Pid] = w.ProcessName;
        }

        // 1. Emit AT-SPI windows first — they carry URL + incognito. Their identity is
        //    upgraded to the WM id when a WM window matches (stable across navigation);
        //    otherwise the a11y key is kept (best effort — the tracker re-keys on churn).
        foreach (var w in probe)
        {
            if (w.Source != "a11y")
                continue;
            if (string.IsNullOrWhiteSpace(w.Title) && string.IsNullOrWhiteSpace(w.Url))
                continue;

            var wm = wmIndexed.FirstOrDefault(x => x.Pid == w.Pid && TitlesOverlap(x.Title, w.Title));
            var key = wm is not null ? "wm:" + wm.Id : "a11y:" + w.Key;

            result.Add(new AccessibilitySnapshot
            {
                WindowKey = key,
                ProcessId = w.Pid,
                ProcessName = w.ProcessName,
                WindowTitle = w.Title,
                Url = string.IsNullOrWhiteSpace(w.Url) ? null : BrowserAccessibilityHelpers.NormalizeUrl(w.Url),
                UrlSource = w.Webview ? "webview" : "accessibility",
                IsIncognito = w.Incognito,
                IsActive = w.Active,
                IsWebview = w.Webview,
                CapturedAt = now,
            });

            if (w.Webview && !string.IsNullOrWhiteSpace(w.Url))
                _webviewCache[key] = (new CachedWebview(w.Pid, w.ProcessName, w.Title, w.Url, w.Active), now);
        }

        // ── Re-emit cached webview windows between the throttled scans ──
        // Expire first: a webview that is no longer produced by the fresh walk (or whose
        // app closed) drops out here, and the tracker's missing-window close path ends
        // its session a few polls later.
        foreach (var stale in _webviewCache
                     .Where(kv => (now - kv.Value.LastSeenUtc).TotalSeconds > 60)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            _webviewCache.Remove(stale);
        }
        var emittedKeys = new HashSet<string>(result.Select(s => s.WindowKey), StringComparer.Ordinal);
        foreach (var (key, (wv, _)) in _webviewCache)
        {
            if (emittedKeys.Contains(key)) continue;
            result.Add(new AccessibilitySnapshot
            {
                WindowKey = key,
                ProcessId = wv.Pid,
                ProcessName = wv.ProcessName,
                WindowTitle = wv.Title,
                Url = BrowserAccessibilityHelpers.NormalizeUrl(wv.Url),
                UrlSource = "webview",
                IsWebview = true,
                IsActive = wv.Active,
                CapturedAt = now,
            });
            emittedKeys.Add(key);
        }

        // 2. WM windows that are browsers but invisible to AT-SPI (X11-only browsers,
        //    a11y-less setups) → title-only snapshots; history fills the URL later.
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in result)
            seenTitles.Add($"{s.ProcessId}|{BrowserAccessibilityHelpers.StripBrowserSuffix(s.WindowTitle, _browserRegistry.GetDisplayName(s.ProcessName))}");

        foreach (var wm in wmWindows)
        {
            if (string.IsNullOrWhiteSpace(wm.Title)) continue;
            var processName = ReadComm(wm.Pid);
            if (!_browserRegistry.IsBrowser(processName))
                continue;

            var displayName = _browserRegistry.GetDisplayName(processName) ?? processName;
            var t = BrowserAccessibilityHelpers.StripBrowserSuffix(wm.Title, displayName);
            if (seenTitles.Contains($"{wm.Pid}|{t}")) continue;

            result.Add(new AccessibilitySnapshot
            {
                WindowKey = "wm:" + wm.Id,
                ProcessId = wm.Pid,
                ProcessName = processName,
                WindowTitle = wm.Title,
                Url = null,
                UrlSource = "history",
                IsIncognito = BrowserAccessibilityHelpers.TitleSuggestsIncognito(wm.Title),
                CapturedAt = now,
            });
            seenTitles.Add($"{wm.Pid}|{t}");
        }

        // 3. Firefox sessionstore windows — exact URL. Reuse an existing snapshot for the
        //    same window (prefer its stable key) and just fill the URL, else add fresh.
        foreach (var w in probe)
        {
            if (w.Source != "ff")
                continue;
            if (string.IsNullOrWhiteSpace(w.Title) && string.IsNullOrWhiteSpace(w.Url))
                continue;

            var existing = result.FirstOrDefault(s =>
                s.ProcessId == w.Pid && TitlesOverlap(s.WindowTitle, w.Title));
            if (existing is not null)
            {
                if (string.IsNullOrEmpty(existing.Url) && !string.IsNullOrWhiteSpace(w.Url))
                {
                    var idx = result.IndexOf(existing);
                    result[idx] = new AccessibilitySnapshot
                    {
                        WindowKey = existing.WindowKey,
                        ProcessId = existing.ProcessId,
                        ProcessName = existing.ProcessName,
                        WindowTitle = existing.WindowTitle,
                        Url = BrowserAccessibilityHelpers.NormalizeUrl(w.Url),
                        UrlSource = "sessionstore",
                        IsIncognito = existing.IsIncognito,
                        CapturedAt = now,
                    };
                }
                continue;
            }

            result.Add(new AccessibilitySnapshot
            {
                WindowKey = "ff:" + w.Key,
                ProcessId = w.Pid,
                ProcessName = w.ProcessName,
                WindowTitle = w.Title,
                Url = BrowserAccessibilityHelpers.NormalizeUrl(w.Url),
                UrlSource = "sessionstore",
                IsIncognito = w.Incognito,
                CapturedAt = now,
            });
        }

        // Drop exact key duplicates (an a11y window may also surface via the WM list).
        var byKey = new Dictionary<string, AccessibilitySnapshot>(StringComparer.Ordinal);
        foreach (var s in result)
        {
            if (!byKey.TryGetValue(s.WindowKey, out var prev) || !string.IsNullOrEmpty(s.Url) && string.IsNullOrEmpty(prev.Url))
                byKey[s.WindowKey] = s;
        }

        if (byKey.Count == 0)
        {
            if (_checked != true)
                LogUnavailable("no browser windows visible (AT-SPI, Firefox sessionstore, and the WM window list are all empty)");
            return Array.Empty<AccessibilitySnapshot>();
        }

        _checked = true;
        return byKey.Values.ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // WM window list (Source C)
    // ─────────────────────────────────────────────────────────────────────────────

    private sealed record WmWindow(string Id, int Pid, string Title);

    private List<WmWindow> EnumerateWmWindows()
    {
        var windows = new List<WmWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 1. GNOME Shell introspection — stable per-window ids, works on Wayland.
        //    (Denied on some setups — fall through to xprop.)
        try
        {
            var raw = Run("gdbus",
                "call --session --dest org.gnome.Shell --object-path /org/gnome/Shell/Introspect --method org.gnome.Shell.Introspect.GetWindows",
                5000);
            if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ShellIdRegex.Matches(raw);
                var pids = ShellPidRegex.Matches(raw);
                var titles = ShellTitleRegex.Matches(raw);
                var count = Math.Min(ids.Count, Math.Min(pids.Count, titles.Count));
                for (var i = 0; i < count; i++)
                {
                    var id = ids[i].Groups[1].Value.Trim('\'', '"');
                    if (!int.TryParse(pids[i].Groups[1].Value, out var pid) || pid <= 0) continue;
                    var title = titles[i].Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    if (seen.Add("wm:" + id))
                        windows.Add(new WmWindow(id, pid, title));
                }
                if (windows.Count > 0) return windows;
            }
        }
        catch { }

        // 2. X11/XWayland: xprop _NET_CLIENT_LIST → _NET_WM_PID + _NET_WM_NAME.
        try
        {
            var raw = Run("xprop", "-root -notype _NET_CLIENT_LIST", 2000);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (Match m in WindowIdRegex.Matches(raw))
                {
                    var wid = m.Value;
                    if (wid == "0x0") continue;

                    var pidRaw = Run("xprop", $"-id {wid} _NET_WM_PID", 2000);
                    var pidMatch = PidRegex.Match(pidRaw ?? string.Empty);
                    if (!pidMatch.Success || !int.TryParse(pidMatch.Groups[1].Value, out var pid) || pid <= 0)
                        continue;

                    var titleRaw = Run("xprop", $"-id {wid} _NET_WM_NAME", 2000);
                    var title = ExtractXpropString(titleRaw);
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    if (seen.Add("x11:" + wid))
                        windows.Add(new WmWindow(wid, pid, title));
                }
            }
        }
        catch { }

        return windows;
    }

    private List<WmWindow> IndexWmWindows(List<WmWindow> windows)
    {
        var dedup = new List<WmWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in windows)
        {
            var displayName = _browserRegistry.GetDisplayName(ReadComm(w.Pid)) ?? ReadComm(w.Pid);
            var t = BrowserAccessibilityHelpers.StripBrowserSuffix(w.Title, displayName);
            if (seen.Add($"{w.Pid}|{t}"))
                dedup.Add(w);
        }
        return dedup;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Combined python3 probe (Sources A + B)
    // ─────────────────────────────────────────────────────────────────────────────

    private sealed record ProbeWindow(string Source, string Key, int Pid, string ProcessName, string Title, string Url, bool Incognito, bool Active, bool Webview);

    private async Task<List<ProbeWindow>> RunCombinedProbeAsync(CancellationToken ct)
    {
        var result = new List<ProbeWindow>();
        if (!OperatingSystem.IsLinux() || ct.IsCancellationRequested)
            return result;

        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = "-",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc == null)
        {
            LogUnavailable("python3 could not be started");
            return result;
        }

        // Inject the poll counter so the probe can throttle the (expensive) non-browser
        // webview scan to every 5th poll instead of walking every window's tree on each
        // 3s poll. Real browsers are scanned every poll, unchanged.
        var script = ProbeScript.Replace("__AAT_POLL_N__", (_pollCount++).ToString());
        await proc.StandardInput.WriteAsync(script.AsMemory(), ct);
        proc.StandardInput.Close();

        var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errorTask = proc.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }

        var output = await outputTask;
        var err = await errorTask;

        if (string.IsNullOrWhiteSpace(output) || output.Trim() == "[]")
        {
            if (!string.IsNullOrWhiteSpace(err))
                _logger.LogTrace("Combined probe stderr: {Err}", err.Trim());
            return result;
        }

        using var doc = JsonDocument.Parse(output);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                result.Add(new ProbeWindow(
                    el.GetProperty("src").GetString() ?? string.Empty,
                    el.GetProperty("key").GetString() ?? string.Empty,
                    el.GetProperty("pid").GetInt32(),
                    el.GetProperty("proc").GetString() ?? string.Empty,
                    el.GetProperty("title").GetString() ?? string.Empty,
                    el.GetProperty("url").GetString() ?? string.Empty,
                    el.TryGetProperty("incognito", out var inc) && inc.GetBoolean(),
                    el.TryGetProperty("active", out var act) && act.GetBoolean(),
                    el.TryGetProperty("webview", out var wv) && wv.GetBoolean()));
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Malformed probe entry");
            }
        }
        return result;
    }

    private void LogUnavailable(string reason)
    {
        _checked = true;
        _logger.LogInformation(
            "Accessibility browser reader (Linux) unavailable: {Reason}. " +
            "Browser journeys will not be captured until a window source is reachable.",
            reason);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Embedded probe script. Prints a JSON array:
    //   [{"src":"a11y",key,pid,proc,title,url,incognito}, {"src":"ff",key,pid,proc,title,url,incognito}]
    // The Firefox branch reads sessionstore-backups/recovery.jsonlz4 (fresh, ~15s) or
    // sessionstore.jsonlz4, decompresses mozLz4 with a pure-python LZ4 block decoder,
    // and reports every open window's ACTIVE tab (title + exact URL). This is the only
    // window source that works for snap Firefox (AppArmor blocks AT-SPI D-Bus).
    // ─────────────────────────────────────────────────────────────────────────────

    private const string ProbeScript = """
        import os, sys, json, glob, struct
        try:
            import dbus
        except Exception:
            # python3 without the dbus module: the AT-SPI source is unavailable, but the
            # Firefox sessionstore source below must keep working (it needs no dbus).
            dbus = None

        # ── shared helpers ──
        def read_comm(pid):
            try:
                return open('/proc/%d/comm' % pid).read().strip().lower()
            except Exception:
                return ''

        def read_cmd(pid):
            try:
                return open('/proc/%d/cmdline' % pid).read().replace('\0', ' ').lower()
            except Exception:
                return ''

        def resolve_app_name(pid):
            # Resolve the real application name from a PID using OS metadata.
            # Flatpak apps may surface as xdg-dbus-proxy or bwrap at the AT-SPI layer,
            # so walk up the bwrap PPID chain to reach the real app process before
            # applying FLATPAK_ID / snap / comm resolution.
            real_pid = pid
            is_proxy = False
            try:
                _comm = read_comm(pid)
                if _comm in ('bwrap', 'xdg-dbus-proxy'):
                    is_proxy = True
                    seen = set()
                    while True:
                        seen.add(real_pid)
                        try:
                            with open('/proc/%d/stat' % real_pid, 'rb') as _sf:
                                _stat = _sf.read().split(b' ')
                                _ppid = int(_stat[3])
                        except Exception:
                            break
                        if _ppid == 0 or _ppid in seen:
                            break
                        _pcomm = read_comm(_ppid)
                        if _pcomm != 'bwrap':
                            real_pid = _ppid
                            break
                        real_pid = _ppid
            except Exception:
                real_pid = pid

            # Flatpak: FLATPAK_ID in /proc/<pid>/environ -> extract short app name
            try:
                with open('/proc/%d/environ' % real_pid, 'rb') as f:
                    for part in f.read().split(b'\0'):
                        if part.startswith(b'FLATPAK_ID='):
                            app_id = part.split(b'=', 1)[1].decode('utf-8', 'replace')
                            short = app_id.rsplit('.', 1)[-1]
                            if short:
                                return short.lower()
            except Exception:
                pass
            # Snap: exe path contains /snap/<name>/
            try:
                exe = os.readlink('/proc/%d/exe' % real_pid)
                idx = exe.find('/snap/')
                if idx >= 0:
                    snap_name = exe[idx + 6:].split('/')[0]
                    if snap_name:
                        return snap_name.lower()
            except Exception:
                pass
            
            # Fallback for Flatpak proxies whose bwrap chain doesn't lead to the app:
            # scan all running processes for FLATPAK_ID. This covers the case where
            # xdg-dbus-proxy's bwrap is a sibling of the app's bwrap (both children
            # of the same parent) rather than a child of it.
            if is_proxy:
                try:
                    for _p in os.listdir('/proc'):
                        if not _p.isdigit():
                            continue
                        try:
                            with open('/proc/%s/environ' % _p, 'rb') as f:
                                for part in f.read().split(b'\0'):
                                    if part.startswith(b'FLATPAK_ID='):
                                        app_id = part.split(b'=', 1)[1].decode('utf-8', 'replace')
                                        short = app_id.rsplit('.', 1)[-1]
                                        if short:
                                            return short.lower()
                        except Exception:
                            continue
                except Exception:
                    pass
            
            return read_comm(real_pid)

        # Structural browser detection: scan .desktop files for Categories=WebBrowser.
        # Cached on disk for 5 minutes to avoid rescanning every 3s poll.
        _browser_exes = set()
        import time as _time
        _cache_dir = os.path.join(os.path.expanduser('~'), '.cache', 'alpha-ai-tracker')
        _cache_file = os.path.join(_cache_dir, 'browser_exes.json')
        try:
            if os.path.exists(_cache_file) and (_time.time() - os.path.getmtime(_cache_file)) < 300:
                with open(_cache_file) as _cf:
                    _browser_exes = set(json.load(_cf))
        except Exception:
            pass
        if not _browser_exes:
            for _dd in ['/usr/share/applications', '/usr/local/share/applications',
                         os.path.expanduser('~/.local/share/applications'),
                         '/var/lib/flatpak/exports/share/applications',
                         os.path.expanduser('~/.local/share/flatpak/exports/share/applications')]:
                if not os.path.isdir(_dd):
                    continue
                for _df in os.listdir(_dd):
                    if not _df.endswith('.desktop'):
                        continue
                    try:
                        with open(os.path.join(_dd, _df)) as _f:
                            _cats = ''
                            _exec = ''
                            for _line in _f:
                                if _line.startswith('Categories='):
                                    _cats = _line.strip()
                                elif _line.startswith('Exec=') and not _exec:
                                    _exec = _line.split('=', 1)[1].strip().split()[0]
                            if 'WebBrowser' in _cats:
                                _exec_parts = _exec.split() if _exec else []
                                # Flatpak exec: 'flatpak run org.mozilla.Floorp' -> extract app ID
                                if len(_exec_parts) >= 3 and _exec_parts[0] == 'flatpak' and _exec_parts[1] == 'run':
                                    _fp_id = _exec_parts[2].replace('.desktop', '')
                                    _browser_exes.add(_fp_id.lower())
                                    _fp_short = _fp_id.rsplit('.', 1)[-1].lower()
                                    if _fp_short:
                                        _browser_exes.add(_fp_short)
                                elif _exec:
                                    _browser_exes.add(os.path.basename(_exec).lower())
                                _did = _df.replace('.desktop', '')
                                _browser_exes.add(_did.lower())
                                _short = _did.rsplit('.', 1)[-1].lower()
                                if _short:
                                    _browser_exes.add(_short)
                    except Exception:
                        continue
            try:
                os.makedirs(_cache_dir, exist_ok=True)
                with open(_cache_file, 'w') as _cf:
                    json.dump(list(_browser_exes), _cf)
            except Exception:
                pass

        results = []

        # ═══════════════════════════════════════════════════════════════════
        # SOURCE A — AT-SPI accessibility tree
        # ═══════════════════════════════════════════════════════════════════
        # Non-browser apps are scanned for embedded webviews on a THROTTLED cadence
        # (every 5th poll, ~15s) — walking every app's tree each 3s poll would cost
        # measurable CPU. Real browsers are scanned every poll.
        webview_scan = (int(__AAT_POLL_N__) % 5 == 0)
        try:
            raw = os.popen('gdbus call --session --dest org.a11y.Bus --object-path /org/a11y/bus --method org.a11y.Bus.GetAddress 2>/dev/null').read().strip()
            start = raw.find("'") + 1
            end = raw.rfind("'")
            addr = raw[start:end] if start > 0 and end > start else ''
            if addr:
                bus = dbus.bus.BusConnection(addr)

                A11Y = 'org.a11y.atspi.Accessible'
                PROPS = 'org.freedesktop.DBus.Properties'
                TEXT = 'org.a11y.atspi.Text'

                def getp(obj, name):
                    try:
                        return obj.Get(A11Y, name, dbus_interface=PROPS)
                    except Exception:
                        return None

                def get_text(obj):
                    try:
                        return str(obj.GetText(0, 500, dbus_interface=TEXT))
                    except Exception:
                        return ''

                def url_of(t):
                    t = (t or '').strip()
                    if not t:
                        return ''
                    low = t.lower()
                    if low.startswith(('http://', 'https://')):
                        return t
                    if '://' in low:
                        return t
                    if any(ch.isspace() for ch in t) or '.' not in t:
                        return ''
                    return 'https://' + t

                def find_address_bar(win):
                    addr = ''
                    fallback = ''
                    stack = [(win, 0)]
                    seen = set()
                    budget = 1200
                    while stack and budget > 0:
                        obj, depth = stack.pop()
                        budget -= 1
                        if depth > 10:
                            continue
                        try:
                            kids = obj.GetChildren(dbus_interface=A11Y)
                        except Exception:
                            continue
                        for kb, kp in kids:
                            pk = str(kb) + str(kp)
                            if pk in seen:
                                continue
                            seen.add(pk)
                            try:
                                k = bus.get_object(str(kb), str(kp))
                                krole = int(k.GetRole(dbus_interface=A11Y))
                                kname = str(getp(k, 'Name') or '')
                            except Exception:
                                continue
                            if krole in (77, 79):  # EDITBAR / ENTRY
                                txt = get_text(k).strip()
                                if txt:
                                    if 'address and search bar' in kname.lower() or 'search or enter address' in kname.lower():
                                        addr = url_of(txt)
                                        if addr:
                                            return addr
                                        continue
                                    if not fallback:
                                        fallback = url_of(txt)
                            stack.append((k, depth + 1))
                    return addr or fallback

                def find_doc_url(win, win_title, budget=800):
                    # AT-SPI Document interface: the DOCUMENT_WEB node (role 95) exposes
                    # the page's EXACT URL via the DocURL attribute — including in
                    # private/incognito windows, where no other source (omnibox,
                    # sessionstore, profile history) has the URL. Firefox builds this
                    # tree whenever AT-SPI is reachable (the AppArmor override makes
                    # snap Firefox reachable). Chrome needs --force-renderer-accessibility
                    # and returns nothing here when it does not build the tree. This is
                    # ALSO the structural webview signal: any app whose tree contains an
                    # http(s) DOCUMENT_WEB embeds real web content (Electron apps), with
                    # app chrome (vscode-webview://, file://, about:) excluded by scheme.
                    best = ''
                    page = (win_title or '').lower()
                    stack = [(win, 0)]
                    seen = set()
                    while stack and budget > 0:
                        obj, depth = stack.pop()
                        budget -= 1
                        if depth > 12:
                            continue
                        try:
                            kids = obj.GetChildren(dbus_interface=A11Y)
                        except Exception:
                            continue
                        for kb, kp in kids:
                            pk = str(kb) + str(kp)
                            if pk in seen:
                                continue
                            seen.add(pk)
                            try:
                                k = bus.get_object(str(kb), str(kp))
                                krole = int(k.GetRole(dbus_interface=A11Y))
                                kname = str(getp(k, 'Name') or '')
                            except Exception:
                                continue
                            if krole == 95:  # DOCUMENT_WEB
                                try:
                                    u = str(k.GetAttributeValue('DocURL', dbus_interface='org.a11y.atspi.Document') or '')
                                except Exception:
                                    u = ''
                                if u and u.startswith('http'):
                                    if not best:
                                        best = u
                                    # Prefer the doc matching this window's page title
                                    # (multi-document windows / iframes stay unconfused).
                                    if kname and page and (kname.lower() in page or page in kname.lower()):
                                        return u
                                continue
                            stack.append((k, depth + 1))
                    return best

                registry = bus.get_object('org.a11y.atspi.Registry', '/org/a11y/atspi/accessible/root')
                children = registry.GetChildren(dbus_interface=A11Y)
                dbus_obj = bus.get_object('org.freedesktop.DBus', '/org/freedesktop/DBus')
                for app_bus_name, _ in children:
                    name = str(app_bus_name)
                    try:
                        pid = int(dbus_obj.GetConnectionUnixProcessID(name, dbus_interface='org.freedesktop.DBus'))
                    except Exception:
                        continue
                    if pid <= 0:
                        continue
                    comm = resolve_app_name(pid)
                    cmd = read_cmd(pid)
                    if '--headless' in cmd:
                        continue
                    if ' --type=' in cmd:
                        # Chromium/Electron CHILD process (renderer/gpu/utility): its a11y
                        # frames are not real OS windows — skipping them structurally keeps
                        # Electron apps' internal panes out of the journey stream.
                        continue
                    is_browser = comm in _browser_exes
                    # Non-browser apps (Electron webviews etc.) are only scanned on the
                    # throttled cadence above.
                    if not is_browser and not webview_scan:
                        continue
                    try:
                        app_obj = bus.get_object(name, '/org/a11y/atspi/accessible/root')
                        wins = app_obj.GetChildren(dbus_interface=A11Y)
                    except Exception:
                        continue
                    for win_bus, win_path in wins:
                        try:
                            w = bus.get_object(str(win_bus), str(win_path))
                            wrole = int(w.GetRole(dbus_interface=A11Y))
                            wname = str(getp(w, 'Name') or '')
                        except Exception:
                            continue
                        if wrole not in (23, 69):  # FRAME / WINDOW
                            continue
                        if not wname.strip():
                            continue
                        # OS focus: the window whose state carries STATE_ACTIVE (1) /
                        # STATE_FOCUSED (12) is the focused one. at-spi2-core >= 2.50
                        # returns a PACKED bitmask ([lo32, hi32]); older returns a list
                        # of state ids — the focused window is marked in BOTH forms.
                        win_active = False
                        try:
                            st = list(w.GetState(dbus_interface=A11Y))
                            if st and max(st) > 0xFFFF:
                                smask = st[0] | (st[1] << 32 if len(st) > 1 else 0)
                                win_active = bool(smask & ((1 << 1) | (1 << 12)))
                            else:
                                win_active = (1 in st) or (12 in st)
                        except Exception:
                            pass
                        # Incognito is per-window: a single browser process hosts normal AND
                        # incognito windows, so one incognito window must never flag its
                        # siblings (that would strip their URLs when capture is gated off).
                        win_incognito = '--incognito' in cmd
                        if any(s in wname.lower() for s in ('incognito', 'inprivate', 'private browsing')):
                            win_incognito = True
                        if is_browser:
                            url = find_address_bar(w)
                            if not url:
                                url = find_doc_url(w, wname)
                        else:
                            # Embedded-webview proof: an http(s) DOCUMENT_WEB node. No URL
                            # found (or only app-chrome schemes) → this window is NOT web
                            # content, skip it — never emit a junk browser_tab for it.
                            url = find_doc_url(w, wname, 400)
                            if not url:
                                continue
                        results.append({
                            'src': 'a11y',
                            'key': name + '|' + str(win_path),
                            'pid': pid,
                            'proc': comm,
                            'title': wname,
                            'url': url,
                            'incognito': win_incognito,
                            'active': win_active,
                            'webview': (not is_browser),
                        })
        except Exception:
            pass

        # ═══════════════════════════════════════════════════════════════════
        # SOURCE B — Firefox sessionstore (survives snap AppArmor; exact URLs)
        # ═══════════════════════════════════════════════════════════════════
        def lz4_decompress(src):
            # Pure-python LZ4 block decompressor (mozLz4 payload).
            out = bytearray()
            pos = 0
            n = len(src)
            try:
                while pos < n:
                    token = src[pos]; pos += 1
                    lit_len = token >> 4
                    if lit_len == 15:
                        while True:
                            b = src[pos]; pos += 1
                            lit_len += b
                            if b != 255:
                                break
                    if pos + lit_len > n:
                        return None
                    out += src[pos:pos + lit_len]; pos += lit_len
                    if pos >= n:
                        break
                    if pos + 2 > n:
                        return None
                    offset = src[pos] | (src[pos + 1] << 8); pos += 2
                    if offset == 0 or offset > len(out):
                        return None
                    match_len = (token & 0x0F) + 4
                    if (token & 0x0F) == 15:
                        while True:
                            b = src[pos]; pos += 1
                            match_len += b
                            if b != 255:
                                break
                    start = len(out) - offset
                    for i in range(match_len):
                        out.append(out[start + i])
            except Exception:
                return None
            return bytes(out)

        def read_mozlz4(path):
            # mozLz4 layout: 8-byte "mozLz40\0" header + 4-byte LE uncompressed size
            # + one LZ4 block (mozilla::Compression::LZ4 writes the size prefix).
            try:
                with open(path, 'rb') as f:
                    data = f.read()
                if data[:8] != b'mozLz40\0':
                    return None
                size = struct.unpack('<I', data[8:12])[0]
                raw = lz4_decompress(data[12:])
                if raw is None or len(raw) != size:
                    return None
                return raw
            except Exception:
                return None

        def active_tab(w):
            try:
                tabs = w.get('tabs') or []
                if not tabs:
                    return None
                idx = int(w.get('selected', 1)) - 1
                if idx < 0 or idx >= len(tabs):
                    idx = 0
                tab = tabs[idx]
                entries = tab.get('entries') or []
                eidx = int(tab.get('index', 1)) - 1
                if eidx < 0 or eidx >= len(entries):
                    eidx = len(entries) - 1
                if eidx < 0:
                    return None
                e = entries[eidx]
                url = (e.get('url') or '').strip()
                title = (e.get('title') or '').strip() or title_from_url(url)
                return (url, title)
            except Exception:
                return None

        def title_from_url(url):
            try:
                from urllib.parse import urlparse
                host = urlparse(url).netloc
                return host or url
            except Exception:
                return url

        try:
            home = os.path.expanduser('~')
            ff_roots = [
                os.path.join(home, '.mozilla', 'firefox'),
                os.path.join(home, 'snap', 'firefox', 'common', '.mozilla', 'firefox'),
            ]
            # A running Firefox main process? (sessionstore may linger after exit)
            ff_pid = 0
            ff_proc = ''
            for pid in os.listdir('/proc'):
                if not pid.isdigit():
                    continue
                comm = read_comm(int(pid))
                if comm in _browser_exes:
                    ff_pid = int(pid)
                    ff_proc = comm
                    break

            if ff_pid > 0:
                seen_wins = set()
                for root in ff_roots:
                    if not os.path.isdir(root):
                        continue
                    for profile in sorted(os.listdir(root)):
                        pdir = os.path.join(root, profile)
                        if not os.path.isdir(pdir) or profile.startswith('.'):
                            continue
                        candidates = []
                        r = os.path.join(pdir, 'sessionstore-backups', 'recovery.jsonlz4')
                        if os.path.exists(r):
                            candidates.append(r)
                        s = os.path.join(pdir, 'sessionstore.jsonlz4')
                        if os.path.exists(s):
                            candidates.append(s)
                        for path in candidates:
                            try:
                                raw = read_mozlz4(path)
                            except Exception:
                                raw = None
                            if raw is None:
                                continue
                            try:
                                data = json.loads(raw.decode('utf-8', 'replace'))
                            except Exception:
                                continue
                            windows = data.get('windows') or []
                            for i, w in enumerate(windows):
                                # Private windows are normally excluded from sessionstore by
                                # Firefox itself, but when they ARE present (some configs /
                                # future versions) they must flow through: the C# tracker
                                # gates their URL on ALPHA_BROWSER_CAPTURE_INCOGNITO, so this
                                # source is never a silent privacy leak — it is config-driven.
                                ispriv = bool(w.get('isPrivate'))
                                tab = active_tab(w)
                                if not tab:
                                    continue
                                url, title = tab
                                if not url or url.startswith(('about:', 'chrome:', 'moz-extension:')):
                                    continue
                                win_key = profile + '|' + str(i)
                                if win_key in seen_wins:
                                    continue
                                seen_wins.add(win_key)
                                results.append({
                                    'src': 'ff',
                                    'key': win_key,
                                    'pid': ff_pid,
                                    'proc': ff_proc,
                                    'title': title,
                                    'url': url,
                                    'incognito': ispriv,
                                })
                            break  # first readable sessionstore wins
        except Exception:
            pass

        print(json.dumps(results))
        """;

    // ─────────────────────────────────────────────────────────────────────────────
    // Shell helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static string? Run(string file, string args, int timeoutMs = 3000)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(timeoutMs);
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private string ReadComm(int pid)
    {
        // Check probe-resolved cache first -- Flatpak/snap apps report the proxy
        // process name via /proc/comm (e.g. xdg-dbus-proxy) but the AT-SPI probe
        // resolves the real app name from FLATPAK_ID / snap path metadata.
        if (_pidNameCache.TryGetValue(pid, out var cached) && !string.IsNullOrEmpty(cached))
            return cached;
        try
        {
            return File.ReadAllText($"/proc/{pid}/comm").Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? ExtractXpropString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = XpropNameRegex.Match(raw);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>True when two window titles refer to the same page (suffixes differ by
    /// browser flavor, quotes, or the a11y tree exposes one without the suffix).</summary>
    private static bool TitlesOverlap(string a, string b)
    {
        var x = BrowserAccessibilityHelpers.StripBrowserSuffix(a, null).Trim();
        var y = BrowserAccessibilityHelpers.StripBrowserSuffix(b, null).Trim();
        if (x.Length == 0 || y.Length == 0) return false;
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;
        return x.Contains(y, StringComparison.OrdinalIgnoreCase) ||
               y.Contains(x, StringComparison.OrdinalIgnoreCase);
    }

    // Static regexes — NOT source-generated, so this compiles in every toolchain
    // (some IDEs/SDKs do not run the System.Text.RegularExpressions.Generator and
    // would otherwise fail with CS8795 on the partial declarations).
    private static readonly Regex ShellIdRegex = new(@"'id':\s*<\s*'?([^'>,]+)'?>");
    private static readonly Regex ShellPidRegex = new(@"wm-pid'?:\s*<int32\s+(\d+)");
    private static readonly Regex ShellTitleRegex = new(@"'title':\s*<\s*'([^']*)'");
    private static readonly Regex WindowIdRegex = new(@"0x[0-9a-f]+", RegexOptions.IgnoreCase);
    private static readonly Regex PidRegex = new(@"=\s*(\d+)");
    private static readonly Regex XpropNameRegex = new(@"=\s*""(.+?)""");
}
