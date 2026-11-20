using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace AlphaAITracker.Service;

/// <summary>
/// Shift-account PCs: exactly one employee Windows session at a time.
/// <list type="number">
/// <item>Optionally (<c>ALPHA_DLP_HIDE_SWITCH_USER=true</c>) hides "Switch user" (policy
/// <c>HideFastUserSwitching = 1</c>). Default is to leave it available: hiding it meant a
/// forgotten sign-out left only that user on the lock screen. The prior value is recorded and
/// put back when hiding is turned off and by <see cref="Clear"/>.</item>
/// <item>"Other user" on the lock screen can still start a second session while the first
/// stays signed in (disconnected). When a session is active and another user's session has
/// been disconnected longer than the grace period, that old session is signed out. Signing
/// out (not killing processes) lets the tracker close its sessions and keep its offline
/// queue in that user's profile for the next sign-in.</item>
/// </list>
/// Only DISCONNECTED sessions are ever signed out — never the active one, never a session
/// whose user is listed in <c>ALPHA_DLP_SINGLE_SESSION_EXEMPT_USERS</c>.
/// </summary>
internal static class SessionGuardEnforcer
{
    public sealed record Result(bool Changed, bool Applied, string Detail, IReadOnlyList<string> LoggedOff);

    internal sealed record SessionInfo(int Id, int State, string User, string Domain);

    private const string PolicyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string HideFusValue = "HideFastUserSwitching";

    // WTS_CONNECTSTATE_CLASS
    internal const int WtsActive = 0;
    internal const int WtsDisconnected = 4;

    private static readonly object Gate = new();
    /// <summary>sessionId → first time we saw it disconnected (grace timer).</summary>
    private static readonly Dictionary<int, DateTimeOffset> DisconnectedSince = new();

    private sealed class Marker
    {
        public bool Applied { get; set; }
        public int? PriorHideFus { get; set; } // null = value was absent
    }

    private static string MarkerPath => Path.Combine(DlpDataPaths.DlpRoot, "session_guard.json");

    public static Result Apply(TimeSpan grace, IReadOnlyCollection<string> exemptUsers, bool hideSwitchUser = false)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os", []);

        lock (Gate)
        {
            var changed = false;
            var errors = new List<string>();

            // 1. Switch user: hide it, or put back what was there before we hid it.
            if (!hideSwitchUser)
            {
                var restore = RestoreHideFusCore();
                if (restore.Changed) changed = true;
                if (!restore.Applied) errors.Add(restore.Detail);
                // Still hidden (e.g. the old ops setup script wrote 1 before the guard ran) → show it.
                try
                {
                    using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(PolicyPath, writable: true);
                    if (key?.GetValue(HideFusValue) as int? == 1)
                    {
                        key.DeleteValue(HideFusValue, throwOnMissingValue: false);
                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("show_fus:" + ex.GetType().Name);
                }
            }
            else try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .CreateSubKey(PolicyPath, writable: true);
                var current = key.GetValue(HideFusValue) as int?;
                if (current != 1)
                {
                    var marker = LoadMarker();
                    if (!marker.Applied)
                    {
                        marker.Applied = true;
                        marker.PriorHideFus = current;
                        SaveMarker(marker);
                    }
                    key.SetValue(HideFusValue, 1, RegistryValueKind.DWord);
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                errors.Add("hide_fus:" + ex.GetType().Name);
            }

            // 2. Sign out stale disconnected sessions of other users.
            var loggedOff = new List<string>();
            List<SessionInfo> sessions;
            try { sessions = Enumerate(); }
            catch (Exception ex)
            {
                errors.Add("enum:" + ex.GetType().Name);
                sessions = [];
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var s in DecideLogoffs(sessions, DisconnectedSince, now, grace, exemptUsers))
            {
                if (WTSLogoffSession(IntPtr.Zero, s.Id, false))
                {
                    loggedOff.Add($"{s.Domain}\\{s.User}#{s.Id}");
                    DisconnectedSince.Remove(s.Id);
                    changed = true;
                }
                else
                {
                    errors.Add($"logoff#{s.Id}:{Marshal.GetLastWin32Error()}");
                }
            }

            var detail = $"sessions={sessions.Count} active={sessions.Count(x => x.State == WtsActive && x.User.Length > 0)} " +
                         $"disconnected={sessions.Count(x => x.State == WtsDisconnected && x.User.Length > 0)} " +
                         $"signed_out={loggedOff.Count}";
            if (errors.Count > 0)
                detail += " err=" + string.Join(",", errors.Take(4));
            return new Result(changed, errors.Count == 0, detail, loggedOff);
        }
    }

    /// <summary>
    /// Pure decision (unit tested): which sessions to sign out now. Updates the grace map:
    /// a session's timer starts the first tick it is seen disconnected and resets when it
    /// reconnects or disappears.
    /// </summary>
    internal static List<SessionInfo> DecideLogoffs(
        IReadOnlyList<SessionInfo> sessions, Dictionary<int, DateTimeOffset> disconnectedSince,
        DateTimeOffset now, TimeSpan grace, IReadOnlyCollection<string> exemptUsers)
    {
        var result = new List<SessionInfo>();
        var withUser = sessions.Where(s => s.User.Length > 0).ToList();
        var active = withUser.Where(s => s.State == WtsActive).ToList();

        // Forget timers of sessions that reconnected or are gone.
        foreach (var id in disconnectedSince.Keys.ToList())
        {
            if (!withUser.Any(s => s.Id == id && s.State == WtsDisconnected))
                disconnectedSince.Remove(id);
        }

        if (active.Count == 0)
            return result; // nobody is working at the PC: never sign anyone out

        foreach (var s in withUser.Where(s => s.State == WtsDisconnected))
        {
            if (!disconnectedSince.TryGetValue(s.Id, out var since))
            {
                disconnectedSince[s.Id] = now;
                since = now;
                if (grace > TimeSpan.Zero)
                    continue; // first sighting starts the timer
            }
            // The active user's own older session (same account) is a reconnect race; skip.
            if (active.Any(a => SameUser(a, s)))
                continue;
            if (exemptUsers.Any(u => u.Equals(s.User, StringComparison.OrdinalIgnoreCase) ||
                                     u.Equals(s.Domain + "\\" + s.User, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (now - since >= grace)
                result.Add(s);
        }
        return result;
    }

    /// <summary>Session ids that have a signed-in user and are active right now.</summary>
    public static HashSet<int> ActiveUserSessionIds()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            return Enumerate().Where(s => s.State == WtsActive && s.User.Length > 0).Select(s => s.Id).ToHashSet();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Set by EnforceWorker each tick: guard on (switch on, machine not paused).</summary>
    public static volatile bool Armed;

    /// <summary>
    /// Fast path (SessionSwitchWorker, every 2 s): sign out another user's session left
    /// disconnected once someone else is active. Returns the signed-out sessions.
    /// </summary>
    public static List<SessionInfo> SignOutStale(TimeSpan grace, IReadOnlyCollection<string> exemptUsers)
    {
        var done = new List<SessionInfo>();
        if (!OperatingSystem.IsWindows()) return done;
        lock (Gate)
        {
            List<SessionInfo> sessions;
            try { sessions = Enumerate(); }
            catch { return done; }
            foreach (var s in DecideLogoffs(sessions, DisconnectedSince, DateTimeOffset.UtcNow, grace, exemptUsers))
            {
                if (WTSLogoffSession(IntPtr.Zero, s.Id, false))
                {
                    DisconnectedSince.Remove(s.Id);
                    done.Add(s);
                }
            }
        }
        return done;
    }

    private static bool SameUser(SessionInfo a, SessionInfo b) =>
        a.User.Equals(b.User, StringComparison.OrdinalIgnoreCase) &&
        a.Domain.Equals(b.Domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>Restore Switch user to its prior state (pause / park / uninstall).</summary>
    public static Result Clear()
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os", []);
        lock (Gate)
        {
            DisconnectedSince.Clear();
            return RestoreHideFusCore();
        }
    }

    /// <summary>Put HideFastUserSwitching back to its pre-guard value. Caller holds <see cref="Gate"/>.</summary>
    private static Result RestoreHideFusCore()
    {
        var marker = LoadMarker();
        if (!marker.Applied)
            return new Result(false, true, "absent", []);
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .CreateSubKey(PolicyPath, writable: true);
            if (marker.PriorHideFus is { } prior)
                key.SetValue(HideFusValue, prior, RegistryValueKind.DWord);
            else
                key.DeleteValue(HideFusValue, throwOnMissingValue: false);
            try { File.Delete(MarkerPath); } catch { /* ignore */ }
            return new Result(true, true, "restored", []);
        }
        catch (Exception ex)
        {
            return new Result(false, false, "restore_failed:" + ex.GetType().Name, []);
        }
    }

    // ── WTS ────────────────────────────────────────────────────────────────

    private static List<SessionInfo> Enumerate()
    {
        var list = new List<SessionInfo>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new InvalidOperationException("WTSEnumerateSessions failed: " + Marshal.GetLastWin32Error());
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                if (info.SessionId == 0) continue; // services session
                list.Add(new SessionInfo(info.SessionId, info.State,
                    QueryString(info.SessionId, WtsUserName), QueryString(info.SessionId, WtsDomainName)));
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
        return list;
    }

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    private static string QueryString(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buf, out _))
            return "";
        try { return Marshal.PtrToStringUni(buf) ?? ""; }
        finally { WTSFreeMemory(buf); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version,
        out IntPtr ppSessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int infoClass,
        out IntPtr ppBuffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr hServer, int sessionId, bool wait);

    // ── marker ─────────────────────────────────────────────────────────────

    private static Marker LoadMarker()
    {
        try
        {
            if (File.Exists(MarkerPath))
                return JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath)) ?? new Marker();
        }
        catch { /* corrupt → treat as not applied */ }
        return new Marker();
    }

    private static void SaveMarker(Marker m)
    {
        try
        {
            Directory.CreateDirectory(DlpDataPaths.DlpRoot);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(m));
        }
        catch { /* best-effort */ }
    }
}
