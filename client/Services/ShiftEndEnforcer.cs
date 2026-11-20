using System.Runtime.InteropServices;
using System.Security.Principal;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Shift PCs (Windows): the employee is tracked for their whole shift — screen locked or not —
/// and signed out of Windows the moment it ends, which closes this tracker and every program in
/// the session and returns the PC to the sign-in screen for the next shift.
///
/// Sign-out, not just a screen lock: shift accounts have no password, so a locked screen could be
/// reopened with one click — and with the tracker gone, nothing would show the face lock again.
/// After sign-out, signing back in outside the shift is refused by the face lock
/// (<see cref="ShiftUnlockGate"/>) and signed out again after <see cref="OutsideShiftGrace"/>.
///
/// Rules: never acts without a cached shift (no schedule → never signs anyone out); never on an
/// Administrator account (IT / <c>team</c>); off with <c>ALPHA_SHIFT_END_SIGNOUT=false</c> or
/// <c>ALPHA_FACE_SHIFT_GATE=false</c>. Unsent tracking data stays in this user's local database
/// and syncs at their next sign-in.
/// </summary>
public sealed class ShiftEndEnforcer : BackgroundService
{
    /// <summary>Local SQLite read only — short so sign-out lands within seconds of the shift end.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    /// <summary>Signed in outside the shift: time to read the face-lock message before sign-out.</summary>
    internal static readonly TimeSpan OutsideShiftGrace = TimeSpan.FromSeconds(60);

    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly ILogger<ShiftEndEnforcer> _logger;

    public ShiftEndEnforcer(ILogStore store, AppConfig config, ILogger<ShiftEndEnforcer> logger)
    {
        _store = store;
        _config = config;
        _logger = logger;
    }

    /// <summary>What to do on one check (pure, unit-tested).</summary>
    internal enum Action { None, SignOutNow }

    /// <summary>
    /// <paramref name="wasInShift"/>: this session has been inside the shift at some point.
    /// <paramref name="outsideSince"/>: first check that found the session outside the shift.
    /// </summary>
    internal static Action Decide(
        ShiftUnlockGate.Decision decision, bool hasSchedule,
        ref bool wasInShift, ref DateTimeOffset? outsideSince, DateTimeOffset now)
    {
        if (!hasSchedule || decision.Allowed)
        {
            if (hasSchedule) wasInShift = true;
            outsideSince = null;
            return Action.None;
        }
        if (wasInShift)
            return Action.SignOutNow; // shift just ended
        outsideSince ??= now;
        return now - outsideSince.Value >= OutsideShiftGrace ? Action.SignOutNow : Action.None;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !_config.ShiftEndSignOut || !_config.FaceShiftGate)
            return;
        if (IsAdministrator())
        {
            _logger.LogInformation("ShiftEndEnforcer: administrator account — end-of-shift sign-out off");
            return;
        }

        var wasInShift = false;
        DateTimeOffset? outsideSince = null;
        string? employeeId = null;
        var started = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var emp = await _store.GetEmployeeInfoAsync(ct);
                if (emp != null && !string.IsNullOrWhiteSpace(emp.EmployeeId))
                {
                    if (!string.Equals(employeeId, emp.EmployeeId, StringComparison.Ordinal))
                    {
                        employeeId = emp.EmployeeId; // new login in this session: start fresh
                        wasInShift = false;
                        outsideSince = null;
                    }

                    var schedule = ShiftScheduleReader.Load(emp.EmployeeId, _config);
                    var now = DateTimeOffset.UtcNow;
                    var decision = ShiftUnlockGate.Evaluate(now, schedule, _config.FaceShiftEarlyMinutes);
                    var hasSchedule = schedule != null && schedule.WeeklyPattern.Count > 0;

                    // Never sign anyone out on a stale copy: right after sign-in the tracker is
                    // downloading the current shift. Wait for that download (or 2 min if the
                    // server is unreachable, then trust the cached copy).
                    if (!wasInShift && !decision.Allowed && now - started < TimeSpan.FromMinutes(2)
                        && !await ScheduleFreshSinceAsync(started, ct))
                        hasSchedule = false;

                    if (Decide(decision, hasSchedule, ref wasInShift, ref outsideSince, now) == Action.SignOutNow)
                    {
                        _logger.LogWarning(
                            "ShiftEndEnforcer: outside shift for {Emp} ({Reason}) — signing out of Windows",
                            employeeId, wasInShift ? "shift ended" : "signed in outside shift");
                        SignOutOfWindows();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ShiftEndEnforcer check failed");
            }

            try { await Task.Delay(Poll, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Member of the local Administrators group — checked through the account database, not
    /// the token, because under UAC an admin's normal token marks Administrators deny-only
    /// (IsInRole would say false for <c>team</c>).
    /// </summary>
    /// <summary>True when ScheduleCacheService finished a server pull after <paramref name="since"/>.</summary>
    private async Task<bool> ScheduleFreshSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        var raw = await _store.GetStatusAsync("ta_last_schedule_pull_at", ct);
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
               && at >= since.AddSeconds(-5);
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var user = Environment.UserName;
            if (NetUserGetLocalGroups(null, user, 0, LG_INCLUDE_INDIRECT, out var buf,
                    MAX_PREFERRED_LENGTH, out var read, out _) != 0)
                return false;
            try
            {
                var adminName = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                    .Translate(typeof(NTAccount)).Value.Split('\\').Last();
                for (var i = 0; i < read; i++)
                {
                    var namePtr = Marshal.ReadIntPtr(buf, i * IntPtr.Size);
                    if (string.Equals(Marshal.PtrToStringUni(namePtr), adminName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            finally
            {
                NetApiBufferFree(buf);
            }
        }
        catch
        {
            return false;
        }
    }

    private const int LG_INCLUDE_INDIRECT = 1;
    private const int MAX_PREFERRED_LENGTH = -1;

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetLocalGroups(string? server, string user, int level, int flags,
        out IntPtr buffer, int prefMaxLen, out int entriesRead, out int totalEntries);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    private const uint EWX_LOGOFF = 0x00000000;
    private const uint EWX_FORCE = 0x00000004;
    private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    private void SignOutOfWindows()
    {
        // Lock first so nothing stays on screen while programs close; then force sign-out so an
        // unsaved document cannot hold the session open past the shift.
        try { LockWorkStation(); } catch { /* best-effort */ }
        if (!ExitWindowsEx(EWX_LOGOFF | EWX_FORCE, SHTDN_REASON_FLAG_PLANNED))
            _logger.LogWarning("ShiftEndEnforcer: ExitWindowsEx failed ({Err})", Marshal.GetLastWin32Error());
    }
}
