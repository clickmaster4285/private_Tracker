using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// NTFS ACL delete-block (Windows). On every Fixed/Removable volume root, Deny
/// Delete + DeleteSubdirectoriesAndFiles for Authenticated Users so employees can
/// create/edit but not delete. SYSTEM / Administrators are not denied.
///
/// Limitation: apps that Save via temp→delete→rename may fail (inherent to Deny Delete).
/// </summary>
internal static class DeleteBlockAclEnforcer
{
    private static readonly object Gate = new();
    private static readonly SecurityIdentifier AuthUsersSid = new("S-1-5-11");
    private const FileSystemRights DenyRights =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
    private const InheritanceFlags Inherit =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    private static string _lastRootsFingerprint = "\0";
    private static DateTimeOffset _lastOkApply = DateTimeOffset.MinValue;
    private static bool _lastApplyOk;
    private static string _lastClearFingerprint = "\0";
    private static bool _clearVerified;

    public sealed record Result(bool Changed, bool Applied, string Detail);

    public static Result Apply() => Apply(TimeSpan.FromSeconds(300));

    /// <summary>
    /// Apply Deny Delete ACEs. When the ready Fixed/Removable root set is unchanged and the
    /// last apply succeeded, skip ACL reads until <paramref name="recheckEvery"/> elapses
    /// (tamper recheck). Marker JSON is rewritten only when the marked set changes.
    /// </summary>
    public static Result Apply(TimeSpan recheckEvery)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os");

        lock (Gate)
        {
            var roots = EnumerateTargetRoots();
            var fp = string.Join("|", roots);
            var now = DateTimeOffset.UtcNow;
            if (_lastApplyOk &&
                fp == _lastRootsFingerprint &&
                now - _lastOkApply < recheckEvery)
            {
                return new Result(false, true, "skipped_stable");
            }

            var marked = LoadMarkedRoots();
            var markedBefore = string.Join("|", marked);
            var changed = 0;
            var ok = 0;
            var errors = new List<string>();

            foreach (var root in roots)
            {
                try
                {
                    if (EnsureDenyAce(root))
                        changed++;
                    ok++;
                    marked.Add(NormalizeRoot(root));
                }
                catch (Exception ex)
                {
                    errors.Add($"{root}:{ex.GetType().Name}");
                }
            }

            var markedAfter = string.Join("|", marked);
            if (markedAfter != markedBefore)
                SaveMarkedRoots(marked);

            var detail = $"roots={roots.Count} ok={ok} changed={changed}";
            if (errors.Count > 0)
                detail += " err=" + string.Join(",", errors.Take(4));

            var applied = ok > 0 || roots.Count == 0;
            if (applied && errors.Count == 0)
            {
                _lastRootsFingerprint = fp;
                _lastOkApply = now;
                _lastApplyOk = true;
                _clearVerified = false;
                _lastClearFingerprint = "\0";
            }
            else
            {
                _lastApplyOk = false;
            }

            return new Result(changed > 0, applied, detail);
        }
    }

    public static Result Clear()
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os");

        lock (Gate)
        {
            var roots = EnumerateTargetRoots();
            var rootsFp = string.Join("|", roots);
            var marked = LoadMarkedRoots();
            if (_clearVerified && marked.Count == 0 && rootsFp == _lastClearFingerprint)
                return new Result(false, true, "already_clear");

            var targets = new SortedSet<string>(marked, StringComparer.OrdinalIgnoreCase);
            foreach (var r in roots)
                targets.Add(NormalizeRoot(r));

            var changed = 0;
            var cleared = 0;
            var errors = new List<string>();

            foreach (var root in targets)
            {
                try
                {
                    if (!Directory.Exists(root))
                    {
                        marked.Remove(NormalizeRoot(root));
                        continue;
                    }

                    if (RemoveDenyAce(root))
                        changed++;
                    cleared++;
                    marked.Remove(NormalizeRoot(root));
                }
                catch (Exception ex)
                {
                    errors.Add($"{root}:{ex.GetType().Name}");
                }
            }

            SaveMarkedRoots(marked);
            _lastRootsFingerprint = "\0";
            _lastApplyOk = false;
            _lastOkApply = DateTimeOffset.MinValue;
            if (errors.Count == 0)
            {
                _clearVerified = true;
                _lastClearFingerprint = rootsFp;
            }
            var detail = $"cleared={cleared} changed={changed}";
            if (errors.Count > 0)
                detail += " err=" + string.Join(",", errors.Take(4));
            return new Result(changed > 0, true, detail);
        }
    }

    private static List<string> EnumerateTargetRoots()
    {
        var list = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                var root = d.RootDirectory.FullName;
                if (string.IsNullOrWhiteSpace(root)) continue;
                list.Add(NormalizeRoot(root));
            }
            catch
            {
                // Offline / BitLocker mid-unlock.
            }
        }
        return list;
    }

    private static string NormalizeRoot(string root)
    {
        var r = root.Trim();
        if (r.Length >= 2 && r[1] == ':' && !r.EndsWith('\\'))
            r += "\\";
        return r;
    }

    private static bool EnsureDenyAce(string root)
    {
        var di = new DirectoryInfo(root);
        var security = di.GetAccessControl();
        if (HasOurDenyAce(security))
            return false;

        var rule = new FileSystemAccessRule(
            AuthUsersSid,
            DenyRights,
            Inherit,
            PropagationFlags.None,
            AccessControlType.Deny);
        security.AddAccessRule(rule);
        di.SetAccessControl(security);
        return true;
    }

    private static bool RemoveDenyAce(string root)
    {
        var di = new DirectoryInfo(root);
        var security = di.GetAccessControl();
        var removed = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (!IsOurDenyAce(rule))
                continue;
            security.RemoveAccessRuleSpecific(rule);
            removed = true;
        }

        if (!removed)
            return false;

        di.SetAccessControl(security);
        return true;
    }

    private static bool HasOurDenyAce(DirectorySecurity security)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (IsOurDenyAce(rule))
                return true;
        }
        return false;
    }

    private static bool IsOurDenyAce(FileSystemAccessRule rule)
    {
        if (rule.AccessControlType != AccessControlType.Deny)
            return false;
        if (rule.IdentityReference is not SecurityIdentifier sid || !sid.Equals(AuthUsersSid))
            return false;
        // Match the rights we set (may appear with extra bits on some SKUs — require both).
        var rights = rule.FileSystemRights;
        if ((rights & FileSystemRights.Delete) == 0)
            return false;
        if ((rights & FileSystemRights.DeleteSubdirectoriesAndFiles) == 0)
            return false;
        return (rule.InheritanceFlags & Inherit) == Inherit;
    }

    private static string MarkerPath =>
        Path.Combine(DlpDataPaths.DlpRoot, "delete_block_roots.json");

    private static SortedSet<string> LoadMarkedRoots()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = MarkerPath;
            if (!File.Exists(path))
                return set;
            var json = File.ReadAllText(path);
            var arr = JsonSerializer.Deserialize<string[]>(json);
            if (arr is null) return set;
            foreach (var r in arr)
            {
                if (!string.IsNullOrWhiteSpace(r))
                    set.Add(NormalizeRoot(r));
            }
        }
        catch
        {
            // Corrupt marker — start fresh.
        }
        return set;
    }

    private static void SaveMarkedRoots(SortedSet<string> roots)
    {
        try
        {
            Directory.CreateDirectory(DlpDataPaths.DlpRoot);
            var path = MarkerPath;
            if (roots.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            var json = JsonSerializer.Serialize(roots.ToArray());
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort; next Apply will re-mark.
        }
    }
}
