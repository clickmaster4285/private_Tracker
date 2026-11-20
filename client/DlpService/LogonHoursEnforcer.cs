using System.Runtime.InteropServices;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Reads / writes a local account's Windows logon hours (NetUserGetInfo level 2 /
/// NetUserSetInfo level 1020). Outside its hours Windows itself refuses the sign-in
/// ("Your account has time restrictions…") — independent of the face lock, the camera and
/// the tracker. The value found before our first write is kept in
/// <c>dlp\logon_hours.json</c> and put back by <see cref="RestoreAll"/>.
/// </summary>
internal static class LogonHoursEnforcer
{
    private static readonly object Gate = new();
    private static string MarkerPath => Path.Combine(DlpDataPaths.DlpRoot, "logon_hours.json");

    /// <summary>Set <paramref name="user"/>'s hours; returns true when Windows' value changed.</summary>
    public static bool Apply(string user, byte[] mask, out string error)
    {
        error = "";
        if (!OperatingSystem.IsWindows()) { error = "unsupported_os"; return false; }
        lock (Gate)
        {
            var current = Read(user);
            if (current == null) { error = "read_failed"; return false; }
            if (current.AsSpan().SequenceEqual(mask)) return false;

            var marker = LoadMarker();
            if (!marker.ContainsKey(user))
            {
                marker[user] = Convert.ToHexString(current);
                SaveMarker(marker);
            }
            var rc = Write(user, mask);
            if (rc != 0) { error = "set_failed:" + rc; return false; }
            return true;
        }
    }

    /// <summary>Restore one account to the value it had before we first changed it.</summary>
    public static bool Restore(string user)
    {
        if (!OperatingSystem.IsWindows()) return false;
        lock (Gate)
        {
            var marker = LoadMarker();
            if (!marker.TryGetValue(user, out var hex)) return false;
            var ok = Write(user, Convert.FromHexString(hex)) == 0;
            if (ok) { marker.Remove(user); SaveMarker(marker); }
            return ok;
        }
    }

    /// <summary>Pause / park / protected uninstall: put every account we touched back.</summary>
    public static (bool Changed, string Detail) RestoreAll()
    {
        if (!OperatingSystem.IsWindows()) return (false, "unsupported_os");
        var users = LoadMarker().Keys.ToList();
        var restored = users.Count(Restore);
        return (restored > 0, $"restored={restored}/{users.Count}");
    }

    public static IReadOnlyCollection<string> ManagedUsers() => LoadMarker().Keys.ToList();

    // ── NetUser ─────────────────────────────────────────────────────────────

    private static byte[]? Read(string user)
    {
        if (NetUserGetInfo(null, user, 2, out var buf) != 0 || buf == IntPtr.Zero) return null;
        try
        {
            var info = Marshal.PtrToStructure<USER_INFO_2>(buf);
            var hours = new byte[LogonHoursPlan.Bytes];
            if (info.usri2_logon_hours == IntPtr.Zero)
                return LogonHoursPlan.AllHours(); // NULL = no restriction
            Marshal.Copy(info.usri2_logon_hours, hours, 0, hours.Length);
            return hours;
        }
        finally
        {
            NetApiBufferFree(buf);
        }
    }

    private static int Write(string user, byte[] mask)
    {
        var hours = Marshal.AllocHGlobal(LogonHoursPlan.Bytes);
        try
        {
            Marshal.Copy(mask, 0, hours, LogonHoursPlan.Bytes);
            var info = new USER_INFO_1020 { usri1020_units_per_week = 168, usri1020_logon_hours = hours };
            return NetUserSetInfo(null, user, 1020, ref info, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(hours);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_2
    {
        public string usri2_name;
        public string usri2_password;
        public uint usri2_password_age;
        public uint usri2_priv;
        public string usri2_home_dir;
        public string usri2_comment;
        public uint usri2_flags;
        public string usri2_script_path;
        public uint usri2_auth_flags;
        public string usri2_full_name;
        public string usri2_usr_comment;
        public string usri2_parms;
        public string usri2_workstations;
        public uint usri2_last_logon;
        public uint usri2_last_logoff;
        public uint usri2_acct_expires;
        public uint usri2_max_storage;
        public uint usri2_units_per_week;
        public IntPtr usri2_logon_hours;
        public uint usri2_bad_pw_count;
        public uint usri2_num_logons;
        public string usri2_logon_server;
        public uint usri2_country_code;
        public uint usri2_code_page;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USER_INFO_1020
    {
        public uint usri1020_units_per_week;
        public IntPtr usri1020_logon_hours;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buf);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(string? server, string user, int level, ref USER_INFO_1020 info, out int parmErr);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buf);

    // ── marker ──────────────────────────────────────────────────────────────

    private static Dictionary<string, string> LoadMarker()
    {
        try
        {
            if (File.Exists(MarkerPath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(MarkerPath))
                       is { } d
                    ? new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
        }
        catch { /* corrupt → nothing recorded */ }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveMarker(Dictionary<string, string> m)
    {
        try
        {
            Directory.CreateDirectory(DlpDataPaths.DlpRoot);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(m));
        }
        catch { /* best-effort */ }
    }
}
