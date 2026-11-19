using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AlphaAITracker.Service;

/// <summary>
/// Startup-app control (Windows). Every entry that would auto-start at logon — the
/// <c>Run</c> keys (HKLM, HKLM WOW6432Node, each loaded user hive), the Startup folders
/// (common + per user) and Store / MSIX app startup tasks — is turned OFF by default, the
/// same way Task Manager's Startup tab does it (a disabled marker under
/// <c>Explorer\StartupApproved\{Run|Run32|StartupFolder}</c>, or startup-task
/// <c>State = 1</c>). Nothing is deleted; the original Run value / shortcut stays in place.
///
/// An entry stays ON only when its target is:
/// <list type="bullet">
/// <item>this product (install dir / product data roots), or</item>
/// <item>Microsoft Defender (Windows Security tray / Defender platform folders).</item>
/// </list>
/// Apps on the admin allowlist may run, but they do not auto-start.
///
/// Every value we disable is recorded in <c>dlp\startup_disabled.json</c> with its prior
/// data, so allow / Clear / teardown restore exactly what was there (a user's own earlier
/// disable is never touched). Drift (someone re-enables an entry) is rewritten every tick.
/// Takes effect at the next sign-in — already-running apps are the AppLocker sweeper's job.
/// </summary>
internal static class StartupAppEnforcer
{
    private static readonly object Gate = new();

    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string ShellFoldersPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders";

    /// <summary>OS script / launcher hosts: living under Windows\ does not make what they run a Windows component.</summary>
    private static readonly HashSet<string> LauncherHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe",
        "mshta.exe", "regsvr32.exe", "conhost.exe", "explorer.exe", "schtasks.exe", "reg.exe",
        "msiexec.exe", "start.exe",
    };

    private static readonly Regex ArgPathRx = new(
        @"[A-Za-z]:\\[^""<>|*?\r\n]+?\.(exe|dll|ps1|vbs|vbe|js|jse|bat|cmd|lnk|hta)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record Result(bool Changed, bool Applied, string Detail, IReadOnlyList<string> Written, int Tampered);

    internal sealed record Entry(
        RegistryHive Hive, string? Sid, string ApprovedSub, string Name, string Target, string Args);

    internal sealed class MarkerItem
    {
        public string Key { get; set; } = "";      // "HKLM" or "HKU\<sid>"
        public string Sub { get; set; } = "";      // StartupApproved\Run | Run32 | StartupFolder
        public string Name { get; set; } = "";
        public string? Prior { get; set; }         // base64 of the prior REG_BINARY, null = absent
        public string Target { get; set; } = "";
    }

    private static string MarkerPath => Path.Combine(DlpDataPaths.DlpRoot, "startup_disabled.json");

    public static Result Apply(IReadOnlyList<AllowAppEntry> allowApps)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os", Array.Empty<string>(), 0);

        lock (Gate)
        {
            var errors = new List<string>();
            var written = new List<string>();
            var tampered = 0;
            var kept = 0;
            var disabled = 0;

            var marker = LoadMarker();
            var product = ProductAvExclusions.BuildCached();
            // allowApps is intentionally not consulted: allowed to run ≠ allowed to auto-start.

            var entries = Enumerate(errors);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in entries)
            {
                var keyLabel = KeyLabel(e.Hive, e.Sid);
                var id = MarkerId(keyLabel, e.ApprovedSub, e.Name);
                seen.Add(id);
                try
                {
                    var allowed = IsAllowedTarget(e.Target, e.Args, product);
                    using var baseKey = OpenHive(e.Hive, e.Sid);
                    if (baseKey is null)
                        continue;
                    using var approved = baseKey.CreateSubKey(ApprovedRoot + "\\" + e.ApprovedSub, writable: true);
                    if (approved is null)
                    {
                        errors.Add(id + ":open_failed");
                        continue;
                    }

                    var current = approved.GetValue(e.Name) as byte[];
                    marker.TryGetValue(id, out var mine);

                    if (allowed)
                    {
                        kept++;
                        if (mine is not null)
                        {
                            Restore(approved, mine);
                            marker.Remove(id);
                            written.Add($"{ShortLabel(e)}=restored");
                        }
                        continue;
                    }

                    disabled++;
                    if (IsDisabled(current))
                    {
                        // Already off — ours (no-op) or the user's own earlier disable (leave alone).
                        continue;
                    }

                    if (mine is null)
                    {
                        marker[id] = new MarkerItem
                        {
                            Key = keyLabel,
                            Sub = e.ApprovedSub,
                            Name = e.Name,
                            Prior = current is null ? null : Convert.ToBase64String(current),
                            Target = e.Target,
                        };
                    }
                    else
                    {
                        // We had it off and someone turned it back on.
                        tampered++;
                    }

                    approved.SetValue(e.Name, DisabledValue(), RegistryValueKind.Binary);
                    written.Add($"{ShortLabel(e)}=disabled");
                }
                catch (Exception ex)
                {
                    errors.Add($"{id}:{ex.GetType().Name}");
                }
            }

            // Store / MSIX app startup tasks (Teams, Phone Link, Microsoft 365…) — the part of
            // Task Manager's Startup tab that is not a Run value or a Startup-folder shortcut.
            ApplyPackagedStartupTasks(marker, seen, written, errors, ref tampered, ref disabled);

            // Entries we disabled whose Run value / shortcut is gone (app uninstalled):
            // put the StartupApproved value back as it was and forget it.
            foreach (var stale in marker.Values.Where(m => !seen.Contains(MarkerId(m.Key, m.Sub, m.Name))).ToList())
            {
                if (TryRestoreItem(stale, errors))
                {
                    marker.Remove(MarkerId(stale.Key, stale.Sub, stale.Name));
                    written.Add($"{stale.Name}=restored_gone");
                }
            }

            SaveMarker(marker, errors);

            var detail = $"entries={entries.Count} off={disabled} on={kept} written={written.Count}" +
                         (tampered > 0 ? $" tampered={tampered}" : "");
            if (errors.Count > 0)
                detail += " err=" + string.Join(",", errors.Take(4));
            return new Result(written.Count > 0, errors.Count == 0, detail, written, tampered);
        }
    }

    /// <summary>Restore every entry we disabled (switch off / pause / park / teardown).</summary>
    public static Result Clear()
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os", Array.Empty<string>(), 0);

        lock (Gate)
        {
            var errors = new List<string>();
            var written = new List<string>();
            var marker = LoadMarker();
            foreach (var item in marker.Values.ToList())
            {
                if (TryRestoreItem(item, errors))
                {
                    marker.Remove(MarkerId(item.Key, item.Sub, item.Name));
                    written.Add(item.Name + "=restored");
                }
            }
            SaveMarker(marker, errors);
            // A user hive that is not loaded right now keeps its marker row for a later pass.
            var detail = $"restored={written.Count} pending={marker.Count}";
            if (errors.Count > 0)
                detail += " err=" + string.Join(",", errors.Take(4));
            return new Result(written.Count > 0, errors.Count == 0, detail, written, 0);
        }
    }

    // ── decision ───────────────────────────────────────────────────────────

    /// <summary>
    /// Only two things may auto-start: this product and Microsoft Defender (Windows Security
    /// tray). Everything else — third-party apps, apps on the admin allowlist (allowed to RUN
    /// is not allowed to AUTO-START), and other Windows helpers — is off by default.
    /// </summary>
    internal static bool IsAllowedTarget(string target, string args, ProductAvExclusions.Set product)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false; // unresolvable → fail-closed (off)

        if (ProductAvExclusions.IsProductPath(target, product))
            return true;
        if (IsDefenderStartup(target))
            return true;

        // A launcher host (rundll32 / powershell / wscript …) is judged by what it runs:
        // on only when it starts the product itself.
        if (LauncherHosts.Contains(Path.GetFileName(target)))
        {
            foreach (Match m in ArgPathRx.Matches(args ?? ""))
            {
                if (ProductAvExclusions.IsProductPath(m.Value.Trim(), product))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Microsoft Defender's own startup program, identified by its OS-owned locations:
    /// the Windows Security tray (<c>System32\SecurityHealthSystray.exe</c>), and the
    /// Defender platform folders (<c>Program Files\Windows Defender</c>,
    /// <c>ProgramData\Microsoft\Windows Defender\Platform</c>). Only SYSTEM / TrustedInstaller
    /// can write there, so a look-alike name elsewhere is never kept.
    /// </summary>
    internal static bool IsDefenderStartup(string target)
    {
        var p = ProductAvExclusions.NormalizePath(target);
        if (p.Length == 0) return false;

        bool Under(string? root) =>
            !string.IsNullOrEmpty(root) &&
            p.StartsWith(ProductAvExclusions.NormalizePath(root) + "\\", StringComparison.OrdinalIgnoreCase);

        var system = SafeFolder(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(system) &&
            p.Equals(ProductAvExclusions.NormalizePath(Path.Combine(system, "SecurityHealthSystray.exe")),
                StringComparison.OrdinalIgnoreCase))
            return true;

        var pf = SafeFolder(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf) && Under(Path.Combine(pf, "Windows Defender")))
            return true;
        var pd = SafeFolder(Environment.SpecialFolder.CommonApplicationData);
        return !string.IsNullOrEmpty(pd) && Under(Path.Combine(pd, "Microsoft", "Windows Defender", "Platform"));
    }

    /// <summary>StartupApproved: first byte odd (0x03 / 0x07) = disabled; even (0x02 / 0x06) or missing = enabled.</summary>
    internal static bool IsDisabled(byte[]? data) => data is { Length: > 0 } && (data[0] & 1) == 1;

    internal static byte[] DisabledValue()
    {
        var buf = new byte[12];
        buf[0] = 0x03;
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(buf, 4);
        return buf;
    }

    // ── packaged (Store / MSIX) startup tasks ──────────────────────────────

    internal const string AppTaskSub = "AppTask";
    private const string SystemAppDataPath =
        @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    /// <summary>
    /// windows.startupTask State: 0 Disabled, 1 DisabledByUser, 2 Enabled, 3 DisabledByPolicy,
    /// 4 EnabledByPolicy. Task Manager's "Disable" writes 1; we do the same.
    /// </summary>
    internal static bool IsAppTaskEnabled(int state) => state is 2 or 4;
    internal const int AppTaskDisabledByUser = 1;

    private static void ApplyPackagedStartupTasks(
        Dictionary<string, MarkerItem> marker, HashSet<string> seen, List<string> written,
        List<string> errors, ref int tampered, ref int disabled)
    {
        string[] sids;
        try { sids = Registry.Users.GetSubKeyNames(); }
        catch (Exception ex) { errors.Add("hku_tasks:" + ex.GetType().Name); return; }

        foreach (var sid in sids)
        {
            // Packaged-app state lives in the user's classes hive (S-1-5-21-…_Classes).
            if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) ||
                !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                continue;
            var keyLabel = "HKU\\" + sid;
            try
            {
                using var root = Registry.Users.OpenSubKey(sid + "\\" + SystemAppDataPath, writable: true);
                if (root is null) continue;
                foreach (var pkg in root.GetSubKeyNames())
                {
                    using var pkgKey = root.OpenSubKey(pkg, writable: true);
                    if (pkgKey is null) continue;
                    foreach (var task in pkgKey.GetSubKeyNames())
                    {
                        using var taskKey = pkgKey.OpenSubKey(task, writable: true);
                        if (taskKey?.GetValue("State") is not int state) continue; // not a startup task
                        var name = pkg + "\\" + task;
                        var id = MarkerId(keyLabel, AppTaskSub, name);
                        seen.Add(id);
                        if (!IsAppTaskEnabled(state)) continue; // already off (ours or the user's)

                        marker.TryGetValue(id, out var mine);
                        if (mine is null)
                        {
                            marker[id] = new MarkerItem
                            {
                                Key = keyLabel,
                                Sub = AppTaskSub,
                                Name = name,
                                Prior = Convert.ToBase64String(BitConverter.GetBytes(state)),
                                Target = pkg,
                            };
                        }
                        else
                        {
                            tampered++; // we had it off and it was switched back on
                        }
                        taskKey.SetValue("State", AppTaskDisabledByUser, RegistryValueKind.DWord);
                        disabled++;
                        written.Add($"HKU\\{AppTaskSub}\\{name}=disabled");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{keyLabel}\\{AppTaskSub}:{ex.GetType().Name}");
            }
        }
    }

    /// <summary>Put a packaged startup task back to its recorded State (only if still ours/off).</summary>
    private static bool TryRestoreAppTask(MarkerItem item, List<string> errors)
    {
        try
        {
            if (!item.Key.StartsWith("HKU\\", StringComparison.Ordinal))
                return true;
            using var root = Registry.Users.OpenSubKey(item.Key[4..] + "\\" + SystemAppDataPath, writable: true);
            if (root is null)
                return false; // classes hive not loaded (user signed out) — keep for later
            using var taskKey = root.OpenSubKey(item.Name, writable: true);
            if (taskKey is null)
                return true; // package removed
            if (taskKey.GetValue("State") is int cur && !IsAppTaskEnabled(cur) &&
                item.Prior is not null)
            {
                var prior = BitConverter.ToInt32(Convert.FromBase64String(item.Prior), 0);
                taskKey.SetValue("State", prior, RegistryValueKind.DWord);
            }
            return true;
        }
        catch (Exception ex)
        {
            errors.Add($"restore:{item.Name}:{ex.GetType().Name}");
            return false;
        }
    }

    // ── enumeration ────────────────────────────────────────────────────────

    private static List<Entry> Enumerate(List<string> errors)
    {
        var list = new List<Entry>();

        // Machine-wide.
        ReadRunKey(list, RegistryHive.LocalMachine, null, RunPath, "Run", null, errors);
        ReadRunKey(list, RegistryHive.LocalMachine, null, Run32Path, "Run32", null, errors);
        var commonStartup = SafeFolder(Environment.SpecialFolder.CommonStartup);
        if (!string.IsNullOrWhiteSpace(commonStartup))
            ReadStartupFolder(list, RegistryHive.LocalMachine, null, commonStartup, null, errors);

        // Every loaded interactive user hive (S-1-5-21-…, not the _Classes companion).
        try
        {
            foreach (var sid in Registry.Users.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) ||
                    sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                    continue;
                var profile = UserProfileDir(sid);
                ReadRunKey(list, RegistryHive.Users, sid, RunPath, "Run", profile, errors);
                var folder = UserStartupFolder(sid, profile);
                if (!string.IsNullOrWhiteSpace(folder))
                    ReadStartupFolder(list, RegistryHive.Users, sid, folder, profile, errors);
            }
        }
        catch (Exception ex)
        {
            errors.Add("hku:" + ex.GetType().Name);
        }

        return list;
    }

    private static void ReadRunKey(
        List<Entry> list, RegistryHive hive, string? sid, string runPath, string approvedSub,
        string? profile, List<string> errors)
    {
        try
        {
            using var baseKey = OpenHive(hive, sid);
            using var run = baseKey?.OpenSubKey(runPath);
            if (run is null)
                return;
            foreach (var name in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(name))
                    continue; // (Default) is not a startup entry
                var raw = run.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                var (exe, args) = ParseCommand(ExpandFor(raw, profile));
                list.Add(new Entry(hive, sid, approvedSub, name, exe, args));
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{KeyLabel(hive, sid)}\\{approvedSub}:{ex.GetType().Name}");
        }
    }

    private static void ReadStartupFolder(
        List<Entry> list, RegistryHive hive, string? sid, string folder, string? profile, List<string> errors)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;
                string target = file, args = "";
                if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var (t, a) = ResolveShortcut(file);
                    target = string.IsNullOrWhiteSpace(t) ? "" : ExpandFor(t, profile);
                    args = a;
                }
                list.Add(new Entry(hive, sid, "StartupFolder", name, target, args));
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{KeyLabel(hive, sid)}\\StartupFolder:{ex.GetType().Name}");
        }
    }

    /// <summary>Exe + remaining args of a Run command line (quoted, unquoted-with-spaces, or bare name).</summary>
    internal static (string Exe, string Args) ParseCommand(string cmd)
    {
        var c = (cmd ?? "").Trim();
        if (c.Length == 0)
            return ("", "");

        if (c[0] == '"')
        {
            var end = c.IndexOf('"', 1);
            if (end > 1)
                return (ResolveExe(c[1..end]), c[(end + 1)..].Trim());
            return (ResolveExe(c.Trim('"')), "");
        }

        // Unquoted path with spaces: take the shortest prefix that exists on disk.
        var parts = c.Split(' ');
        for (var i = 1; i <= parts.Length; i++)
        {
            var candidate = string.Join(' ', parts.Take(i));
            if (File.Exists(candidate) || File.Exists(candidate + ".exe"))
                return (ResolveExe(candidate), string.Join(' ', parts.Skip(i)).Trim());
        }
        return (ResolveExe(parts[0]), string.Join(' ', parts.Skip(1)).Trim());
    }

    private static string ResolveExe(string path)
    {
        var p = path.Trim();
        if (p.Length == 0)
            return "";
        if (!Path.HasExtension(p) && File.Exists(p + ".exe"))
            p += ".exe";
        if (!Path.IsPathRooted(p))
        {
            // Bare name (e.g. "ctfmon.exe") resolves through System32 / Windows like the shell does.
            foreach (var dir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.System),
                         Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var full = Path.Combine(dir, p);
                if (File.Exists(full)) return full;
                if (!Path.HasExtension(p) && File.Exists(full + ".exe")) return full + ".exe";
            }
            return p;
        }
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    /// <summary>
    /// Expand a user-hive value for THAT user. The service runs as SYSTEM, so %APPDATA% /
    /// %LOCALAPPDATA% / %USERPROFILE% must come from the profile, not from SYSTEM's env.
    /// </summary>
    private static string ExpandFor(string raw, string? profile)
    {
        var s = raw ?? "";
        if (!string.IsNullOrWhiteSpace(profile))
        {
            s = Regex.Replace(s, "%USERPROFILE%", profile.Replace("$", "$$"), RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "%LOCALAPPDATA%", Path.Combine(profile, "AppData", "Local").Replace("$", "$$"), RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "%APPDATA%", Path.Combine(profile, "AppData", "Roaming").Replace("$", "$$"), RegexOptions.IgnoreCase);
        }
        return Environment.ExpandEnvironmentVariables(s);
    }

    private static string? UserProfileDir(string sid)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
            var p = key?.GetValue("ProfileImagePath")?.ToString();
            return string.IsNullOrWhiteSpace(p) ? null : Environment.ExpandEnvironmentVariables(p);
        }
        catch
        {
            return null;
        }
    }

    private static string? UserStartupFolder(string sid, string? profile)
    {
        try
        {
            using var key = Registry.Users.OpenSubKey(sid + "\\" + ShellFoldersPath);
            var p = key?.GetValue("Startup")?.ToString();
            if (!string.IsNullOrWhiteSpace(p))
                return p;
        }
        catch { /* fall back to profile layout */ }
        return string.IsNullOrWhiteSpace(profile)
            ? null
            : Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
    }

    private static string? SafeFolder(Environment.SpecialFolder f)
    {
        try { return Environment.GetFolderPath(f); } catch { return null; }
    }

    /// <summary>Shortcut target via the shell's own LNK reader (WScript.Shell COM).</summary>
    private static (string Target, string Args) ResolveShortcut(string lnk)
    {
        object? shell = null, sc = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
                return ("", "");
            shell = Activator.CreateInstance(type);
            dynamic d = shell!;
            sc = d.CreateShortcut(lnk);
            dynamic s = sc!;
            return ((string?)s.TargetPath ?? "", (string?)s.Arguments ?? "");
        }
        catch
        {
            return ("", "");
        }
        finally
        {
            if (sc is not null && Marshal.IsComObject(sc)) Marshal.FinalReleaseComObject(sc);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    // ── registry / marker helpers ─────────────────────────────────────────

    private static RegistryKey? OpenHive(RegistryHive hive, string? sid)
    {
        if (hive == RegistryHive.LocalMachine)
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (string.IsNullOrWhiteSpace(sid))
            return null;
        return Registry.Users.OpenSubKey(sid, writable: true);
    }

    private static string KeyLabel(RegistryHive hive, string? sid) =>
        hive == RegistryHive.LocalMachine ? "HKLM" : "HKU\\" + sid;

    private static string MarkerId(string key, string sub, string name) => key + "|" + sub + "|" + name;

    private static string ShortLabel(Entry e) =>
        (e.Hive == RegistryHive.LocalMachine ? "HKLM" : "HKU") + "\\" + e.ApprovedSub + "\\" + e.Name;

    private static void Restore(RegistryKey approved, MarkerItem item)
    {
        if (item.Prior is null)
            approved.DeleteValue(item.Name, throwOnMissingValue: false);
        else
            approved.SetValue(item.Name, Convert.FromBase64String(item.Prior), RegistryValueKind.Binary);
    }

    /// <summary>False when the user hive is not loaded right now (keep the row for later).</summary>
    private static bool TryRestoreItem(MarkerItem item, List<string> errors)
    {
        if (item.Sub == AppTaskSub)
            return TryRestoreAppTask(item, errors);
        try
        {
            RegistryKey? baseKey = item.Key == "HKLM"
                ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                : item.Key.StartsWith("HKU\\", StringComparison.Ordinal)
                    ? Registry.Users.OpenSubKey(item.Key[4..], writable: true)
                    : null;
            if (baseKey is null)
                return false;
            using (baseKey)
            {
                using var approved = baseKey.OpenSubKey(ApprovedRoot + "\\" + item.Sub, writable: true);
                if (approved is null)
                    return true; // nothing left to restore
                // Only undo our own disable — if someone already re-enabled it, keep their value.
                if (IsDisabled(approved.GetValue(item.Name) as byte[]))
                    Restore(approved, item);
                return true;
            }
        }
        catch (Exception ex)
        {
            errors.Add($"restore:{item.Name}:{ex.GetType().Name}");
            return false;
        }
    }

    private static Dictionary<string, MarkerItem> LoadMarker()
    {
        var map = new Dictionary<string, MarkerItem>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(MarkerPath))
                return map;
            var items = JsonSerializer.Deserialize<List<MarkerItem>>(File.ReadAllText(MarkerPath));
            foreach (var i in items ?? new List<MarkerItem>())
                map[MarkerId(i.Key, i.Sub, i.Name)] = i;
        }
        catch
        {
            // Corrupt marker: start fresh. Entries already off stay off (we can no longer
            // prove they were ours); new drift is recorded again from this pass on.
        }
        return map;
    }

    private static void SaveMarker(Dictionary<string, MarkerItem> marker, List<string> errors)
    {
        try
        {
            Directory.CreateDirectory(DlpDataPaths.DlpRoot);
            if (marker.Count == 0)
            {
                if (File.Exists(MarkerPath))
                    File.Delete(MarkerPath);
                return;
            }
            var tmp = MarkerPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(marker.Values.ToList()));
            File.Move(tmp, MarkerPath, overwrite: true);
        }
        catch (Exception ex)
        {
            errors.Add("marker:" + ex.GetType().Name);
        }
    }
}
