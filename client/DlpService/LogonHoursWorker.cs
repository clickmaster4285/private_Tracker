using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace AlphaAITracker.Service;

/// <summary>
/// Shift PCs: each employee Windows account may only be signed in to during that employee's
/// shift — enforced by Windows logon hours, so it holds even with the face lock off.
///
/// For every local profile (ProfileList S-1-5-21-*) the worker reads which employee is logged
/// into that profile's tracker (its SQLite <c>employee_info</c>), downloads that employee's
/// shift from <c>GET /schedules/me</c> with the profile's own device token (so a shift edit
/// arrives even while the person is signed out; the profile's cached
/// <c>employee_schedule</c> is the offline fallback), and writes the account's logon hours.
///
/// Never touches: local Administrators members, <c>ALPHA_DLP_SINGLE_SESSION_EXEMPT_USERS</c>,
/// accounts with no tracker login or no shift (their earlier value is restored). Windows does
/// not sign out a session when its hours end — the tracker's ShiftEndEnforcer does that.
/// Switch <c>ALPHA_DLP_LOGON_HOURS</c> (default true); pause / park / teardown restore.
/// </summary>
internal sealed class LogonHoursWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    /// <summary>Re-download each account's shift at least this often (nobody needs to be signed in).</summary>
    private static readonly TimeSpan ShiftRefresh = TimeSpan.FromMinutes(1);

    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<LogonHoursWorker> _log;
    private readonly Dictionary<string, (DateTimeOffset At, LogonHoursPlan.Shift? Shift)> _shiftCache =
        new(StringComparer.OrdinalIgnoreCase);
    private string _lastSummary = "";

    public LogonHoursWorker(DlpConfig cfg, PolicyStore store, IHttpClientFactory http, ILogger<LogonHoursWorker> log)
    {
        _cfg = cfg;
        _store = store;
        _http = http;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var forceRefresh = false;
            try
            {
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Logon hours pass failed");
            }

            try { forceRefresh = await DlpSignals.WaitLogonHoursAsync(Tick, ct); }
            catch (OperationCanceledException) { return; }
            if (forceRefresh) _shiftCache.Clear(); // shift edited on the web: re-download now
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var pausedUntil = await _store.GetLockdownPausedUntilAsync(ct);
        var paused = pausedUntil is { } p && p > DateTimeOffset.UtcNow;
        if (!_cfg.LogonHours || paused)
        {
            if (LogonHoursEnforcer.ManagedUsers().Count > 0)
            {
                var (_, detail) = LogonHoursEnforcer.RestoreAll();
                _log.LogInformation("Logon hours restored ({Why}): {Detail}", paused ? "paused" : "disabled_local", detail);
            }
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var notes = new List<string>();
        foreach (var (user, profileDir) in EnumerateProfiles())
        {
            if (IsExempt(user) || IsLocalAdministrator(user)) continue;

            var login = ReadTrackerLogin(profileDir);
            if (login == null)
            {
                if (LogonHoursEnforcer.Restore(user)) notes.Add($"{user}:restored(no_login)");
                continue;
            }

            var shift = await GetShiftAsync(login.Value, ct);
            if (shift == null || shift.WeeklyPattern.Count == 0)
            {
                if (LogonHoursEnforcer.Restore(user)) notes.Add($"{user}:restored(no_shift)");
                continue;
            }

            var mask = LogonHoursPlan.Compute(now, shift);
            if (LogonHoursEnforcer.Apply(user, mask, out var err))
                notes.Add($"{user}={login.Value.EmployeeId}:set");
            else if (err.Length > 0)
                notes.Add($"{user}:{err}");
        }

        var summary = string.Join(" ", notes);
        if (notes.Count > 0 && summary != _lastSummary)
        {
            _log.LogInformation("Logon hours: {Summary}", summary);
            await _store.AuditAsync("logon_hours_apply", summary, ct);
        }
        _lastSummary = summary;
    }

    private bool IsExempt(string user) =>
        _cfg.SingleSessionExemptUsers.Any(u =>
            u.Equals(user, StringComparison.OrdinalIgnoreCase) ||
            u.EndsWith("\\" + user, StringComparison.OrdinalIgnoreCase));

    // ── shift source ────────────────────────────────────────────────────────

    private readonly record struct TrackerLogin(string EmployeeId, string DeviceToken, LogonHoursPlan.Shift? Cached);

    private async Task<LogonHoursPlan.Shift?> GetShiftAsync(TrackerLogin login, CancellationToken ct)
    {
        if (_shiftCache.TryGetValue(login.EmployeeId, out var hit) && DateTimeOffset.UtcNow - hit.At < ShiftRefresh)
            return hit.Shift ?? login.Cached;

        LogonHoursPlan.Shift? fresh = null;
        var reached = false;
        if (!string.IsNullOrWhiteSpace(_cfg.ServerUrl) && !string.IsNullOrWhiteSpace(login.DeviceToken))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{_cfg.ServerUrl.TrimEnd('/')}/api/v1/schedules/me");
                req.Headers.Authorization = new AuthenticationHeaderValue("Device", login.DeviceToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var resp = await _http.CreateClient("dlp").SendAsync(req, timeout.Token);
                if (resp.IsSuccessStatusCode)
                {
                    reached = true;
                    fresh = ParseShift(await resp.Content.ReadAsStringAsync(timeout.Token));
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    reached = true; // server says: no shift assigned
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* offline → cached copy */ }
        }

        if (reached)
            _shiftCache[login.EmployeeId] = (DateTimeOffset.UtcNow, fresh);
        return reached ? fresh : login.Cached;
    }

    internal static LogonHoursPlan.Shift? ParseShift(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var tz = root.TryGetProperty("timezone", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var pattern = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("weeklyPattern", out var wp) && wp.ValueKind == JsonValueKind.Object)
                foreach (var day in wp.EnumerateObject())
                    if (day.Value.ValueKind == JsonValueKind.String && day.Name.Length >= 3)
                        pattern[day.Name[..3].ToLowerInvariant()] = day.Value.GetString() ?? "";
            var holidays = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("holidays", out var hs) && hs.ValueKind == JsonValueKind.Array)
                foreach (var h in hs.EnumerateArray())
                {
                    // Only approved holidays close the PC (pending / rejected do not).
                    if (h.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                        && st.GetString() is { Length: > 0 } status
                        && !status.Equals("approved", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (h.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        var date = (d.GetString() ?? "").Trim();
                        holidays.Add(date.Length > 10 ? date[..10] : date);
                    }
                }
            return new LogonHoursPlan.Shift(tz, pattern, holidays);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Employee + device token + cached shift from the profile's tracker database.</summary>
    private static TrackerLogin? ReadTrackerLogin(string profileDir)
    {
        foreach (var db in TrackerDbPaths(profileDir))
        {
            if (!File.Exists(db)) continue;
            try
            {
                using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Shared");
                conn.Open();
                string? emp = null, token = null;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT employee_id, COALESCE(device_token, '') FROM employee_info " +
                        "WHERE length(COALESCE(employee_id, '')) > 0 LIMIT 1";
                    using var r = cmd.ExecuteReader();
                    if (r.Read()) { emp = r.GetString(0); token = r.GetString(1); }
                }
                if (string.IsNullOrWhiteSpace(emp)) continue;

                LogonHoursPlan.Shift? cached = null;
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT timezone, weekly_pattern FROM employee_schedule WHERE employee_id = $id LIMIT 1";
                    cmd.Parameters.AddWithValue("$id", emp);
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        var json = r.IsDBNull(1) ? "{}" : r.GetString(1);
                        cached = ParseShift("{\"timezone\":" + JsonSerializer.Serialize(r.IsDBNull(0) ? "" : r.GetString(0)) +
                                            ",\"weeklyPattern\":" + (string.IsNullOrWhiteSpace(json) ? "{}" : json) + "}");
                    }
                }
                catch { /* no schedule table yet */ }
                return new TrackerLogin(emp!, token ?? "", cached);
            }
            catch { /* locked / corrupt → try the next file */ }
        }
        return null;
    }

    private static IEnumerable<string> TrackerDbPaths(string profileDir)
    {
        var names = new List<string> { "aat_verify.db", "alpha_tracker.db" };
        var configured = Environment.GetEnvironmentVariable("ALPHA_DB_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var n = Path.GetFileName(configured.Trim().Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(n) && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Insert(0, n);
        }
        foreach (var n in names)
            yield return Path.Combine(profileDir, "AppData", "Local", ServiceIdentity.DataFolderName, "data", n);
    }

    // ── accounts ────────────────────────────────────────────────────────────

    private static IEnumerable<(string User, string ProfileDir)> EnumerateProfiles()
    {
        var list = new List<(string, string)>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (root == null) return list;
            var machine = Environment.MachineName;
            foreach (var sid in root.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-", StringComparison.Ordinal)))
            {
                try
                {
                    using var k = root.OpenSubKey(sid);
                    var dir = Environment.ExpandEnvironmentVariables(k?.GetValue("ProfileImagePath") as string ?? "");
                    if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                    var account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; // PC\user
                    var slash = account.IndexOf('\\');
                    // Local accounts only — logon hours of a domain account are the DC's business.
                    if (slash < 0 || !account[..slash].Equals(machine, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add((account[(slash + 1)..], dir));
                }
                catch { /* deleted account / orphaned profile */ }
            }
        }
        catch { /* registry unavailable */ }
        return list;
    }

    private static bool IsLocalAdministrator(string user)
    {
        if (NetUserGetLocalGroups(null, user, 0, 1 /* LG_INCLUDE_INDIRECT */, out var buf, -1, out var read, out _) != 0)
            return true; // can't tell → never restrict
        try
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                .Translate(typeof(NTAccount)).Value.Split('\\').Last();
            for (var i = 0; i < read; i++)
                if (string.Equals(Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, i * IntPtr.Size)), admins,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        finally
        {
            NetApiBufferFree(buf);
        }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetLocalGroups(string? server, string user, int level, int flags,
        out IntPtr buffer, int prefMaxLen, out int entriesRead, out int totalEntries);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
