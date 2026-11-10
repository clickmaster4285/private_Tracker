using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace AlphaAITracker.Service;

/// <summary>
/// Hardware/port enforcer (Windows). Applies the published deviceControl set:
///   - instance block → Disable-PnpDevice, verified against live PnP status
///   - class block     → DenyDeviceClasses (+ retroactive) using setup-class GUIDs,
///                        plus immediate Disable-PnpDevice on present devices of that class
/// Clear re-enables every instance we disabled, removes our class denies, and rescans PnP.
/// Alert-only classes (Display/Monitor) are never hard-blocked here.
/// </summary>
internal static class PortDeviceEnforcer
{
    private const string RestrictionsKey =
        @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions";
    private const string DenyClassesSub = "DenyDeviceClasses";
    private const string AllowIdsSub = "AllowDeviceIDs";
    /// <summary>Tracks which DenyDeviceClasses value-names (GUIDs) we own so Clear/GPO coexist.</summary>
    private const string OwnedDenyKey =
        @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions\AlphaAI-DLP-OwnedDenyClasses";
    private const string OwnedAllowKey =
        @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions\AlphaAI-DLP-OwnedAllowIds";

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, byte> DisabledInstances =
        new(StringComparer.OrdinalIgnoreCase);

    // Desired block set from the last Apply, read by the fast guard (GuardTick).
    // Replaced wholesale (never mutated) so GuardTick can snapshot without the Gate.
    private static volatile string[] _guardInstances = Array.Empty<string>();
    private static volatile string[] _guardClasses = Array.Empty<string>();
    private static volatile HashSet<string> _guardAllow = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, DateTimeOffset> GuardBackoff =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One device the fast guard had to disable again.</summary>
    public sealed record GuardHit(string InstanceId, string Outcome);

    /// <summary>True when there is anything for the fast guard to watch.</summary>
    public static bool HasGuardTargets => _guardInstances.Length > 0 || _guardClasses.Length > 0;

    /// <summary>
    /// Fast re-check (native calls only, no PowerShell): every blocked instance and every
    /// present device of a blocked class that is back to Enabled is disabled again.
    /// Returns the devices it had to act on. Skips the tick if a full Apply/Clear holds the
    /// gate — that pass is already reconciling.
    /// </summary>
    public static IReadOnlyList<GuardHit> GuardTick()
    {
        var hits = new List<GuardHit>();
        if (!OperatingSystem.IsWindows() || !HasGuardTargets)
            return hits;
        if (!Monitor.TryEnter(Gate))
            return hits;
        try
        {
            var allow = _guardAllow;
            var targets = new HashSet<string>(_guardInstances, StringComparer.OrdinalIgnoreCase);
            foreach (var g in _guardClasses)
                foreach (var id in CfgMgrDevices.ListPresentByClass(g))
                    targets.Add(id);

            foreach (var id in targets)
            {
                if (allow.Contains(id)) continue;
                if (GuardBackoff.TryGetValue(id, out var until) && DateTimeOffset.UtcNow < until) continue;
                var s = CfgMgrDevices.Query(id);
                if (s.State != CfgMgrDevices.DevState.Enabled) continue; // missing / already off
                // Native only: a vetoed device (in-use keyboard) must not spawn PowerShell
                // every 2 s. The 15 s full Apply still tries the PowerShell fallback.
                var r = SetPnpEnabled(id, enable: false, allowFallback: false);
                if (r == PnpOutcome.Changed) { DisabledInstances[id] = 0; GuardBackoff.TryRemove(id, out _); }
                else if (r is PnpOutcome.Vetoed or PnpOutcome.Failed)
                    GuardBackoff[id] = DateTimeOffset.UtcNow.AddSeconds(60); // Windows refused — back off
                hits.Add(new GuardHit(id, r.ToString().ToLowerInvariant()));
            }
        }
        finally
        {
            Monitor.Exit(Gate);
        }
        return hits;
    }

    /// <summary>
    /// Applied = desired state achieved for every present target (retry not needed for those).
    /// Pending = at least one instance id is not plugged in yet — caller must retry next tick.
    /// </summary>
    public sealed record Result(bool Applied, bool Pending, bool Changed, string Detail);

    // Setup-class GUIDs Windows Device Installation Restrictions understands.
    // Plain PnP class names from the collector are normalized through this map.
    private static readonly Dictionary<string, string> ClassNameToGuid =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["USB"] = "{36fc9e60-c465-11cf-8056-444553540000}",
            ["USBDevice"] = "{88bae032-5a81-49f0-bc3d-a4ff138216d6}",
            ["Keyboard"] = "{4d36e96b-e325-11ce-bfc1-08002be10318}",
            ["Mouse"] = "{4d36e96f-e325-11ce-bfc1-08002be10318}",
            ["HIDClass"] = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}",
            ["Camera"] = "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}",
            ["Image"] = "{6bdd1fc6-810f-11d0-bec7-08002be2092f}",
            ["Media"] = "{4d36e96c-e325-11ce-bfc1-08002be10318}",
            ["AudioEndpoint"] = "{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}",
            ["Printer"] = "{4d36e979-e325-11ce-bfc1-08002be10318}",
            ["WPD"] = "{eec5ad98-8080-425f-922a-dabf3de3f69a}",
            ["PortableDevice"] = "{eec5ad98-8080-425f-922a-dabf3de3f69a}",
            ["SmartCardReader"] = "{50dd5230-ba8a-11d1-bf5d-0000f805f530}",
            ["Ports"] = "{4d36e978-e325-11ce-bfc1-08002be10318}",
            ["Biometric"] = "{53d29ef7-377c-4d14-864b-eb3a85769359}",
            ["SCMDisk"] = "{2accfe60-c130-11d2-b082-00a0c91fef85}",
            ["DiskDrive"] = "{4d36e967-e325-11ce-bfc1-08002be10318}",
            ["SCSIAdapter"] = "{4d36e97b-e325-11ce-bfc1-08002be10318}",
            ["HDC"] = "{4d36e96a-e325-11ce-bfc1-08002be10318}",
            ["CDROM"] = "{4d36e965-e325-11ce-bfc1-08002be10318}",
            ["Net"] = "{4d36e972-e325-11ce-bfc1-08002be10318}",
            ["Bluetooth"] = "{e0cbf06c-cd8b-4647-bb8b-3630e76ba2dc}",
            ["Modem"] = "{4d36e96d-e325-11ce-bfc1-08002be10318}",
        };

    private static readonly HashSet<string> AlertOnlyGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "{4d36e968-e325-11ce-bfc1-08002be10318}", // Display (GPU)
        "{4d36e96e-e325-11ce-bfc1-08002be10318}", // Monitor
        // Net adapters: disabling them takes the PC fully offline so Unblock/policy
        // cannot arrive. Block internet via LockdownNetworkEnforcer instead.
        "{4d36e972-e325-11ce-bfc1-08002be10318}", // Net
    };

    internal const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    public static Result Apply(
        IReadOnlyList<string> blockInstances,
        IReadOnlyList<string> blockClasses,
        IReadOnlyList<string>? allowInstances = null)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, false, "unsupported_os");

        var allow = new HashSet<string>(
            allowInstances ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var guids = NormalizeClassGuids(blockClasses)
            .Where(g => !AlertOnlyGuids.Contains(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var instances = blockInstances
            .Where(id => !string.IsNullOrWhiteSpace(id) && !allow.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Never hard-disable a present NIC — even if a stale instance rule slipped through.
        if (instances.Count > 0)
        {
            var netPresent = new HashSet<string>(
                ListPresentInstanceIdsByClassGuids(new[] { NetClassGuid }),
                StringComparer.OrdinalIgnoreCase);
            if (netPresent.Count > 0)
                instances = instances.Where(id => !netPresent.Contains(id)).ToList();
        }

        var changed = false;
        var pending = false;
        var failed = false;
        var sb = new StringBuilder();

        try
        {
            lock (Gate)
            {
                // Publish the desired set for the fast guard (PortGuardWorker) first, so a
                // device the employee re-enables is caught even while this pass is running.
                _guardInstances = instances.ToArray();
                _guardClasses = guids.ToArray();
                _guardAllow = new HashSet<string>(allow, StringComparer.OrdinalIgnoreCase);

                var cls = ApplyClassRestrictions(guids);
                changed |= cls.Changed;
                sb.Append(cls.Detail);

                var allowReg = ApplyAllowDeviceIds(allow.ToList());
                changed |= allowReg.Changed;
                if (allowReg.Detail.Length > 0) sb.Append(';').Append(allowReg.Detail);

                // Immediate disable for every explicit instance.
                foreach (var id in instances)
                {
                    var r = SetPnpEnabled(id, enable: false);
                    if (r == PnpOutcome.Missing)
                    {
                        pending = true;
                        sb.Append(";pending:").Append(Short(id));
                    }
                    else if (r == PnpOutcome.Vetoed)
                    {
                        // Windows refused (device in use, e.g. the active keyboard) — not applied
                        // yet; the guard keeps retrying, and it usually takes effect after a restart.
                        pending = true;
                        sb.Append(";vetoed:").Append(Short(id));
                    }
                    else if (r == PnpOutcome.Failed)
                    {
                        failed = true;
                        sb.Append(";fail:").Append(Short(id));
                    }
                    else if (r == PnpOutcome.Changed)
                    {
                        changed = true;
                        DisabledInstances[id] = 0;
                        sb.Append(";disabled:").Append(Short(id));
                    }
                    else
                    {
                        DisabledInstances[id] = 0; // already disabled
                    }
                }

                // Best-effort immediate disable for present devices of denied classes.
                // Windows often refuses Keyboard/Mouse/boot USB hubs ("cannot be disabled") —
                // that must NOT fail the whole apply: DenyDeviceClasses is the durable block.
                if (guids.Count > 0)
                {
                    foreach (var presentId in ListPresentInstanceIdsByClassGuids(guids))
                    {
                        if (allow.Contains(presentId)) continue;
                        var r = SetPnpEnabled(presentId, enable: false);
                        if (r is PnpOutcome.Failed or PnpOutcome.Vetoed)
                        {
                            sb.Append(";soft_fail:").Append(Short(presentId));
                        }
                        else if (r == PnpOutcome.Changed)
                        {
                            changed = true;
                            DisabledInstances[presentId] = 0;
                            sb.Append(";disabled:").Append(Short(presentId));
                        }
                        else if (r == PnpOutcome.Unchanged)
                        {
                            DisabledInstances[presentId] = 0;
                        }
                    }
                }

                // Re-enable instances we previously disabled that are no longer in the desired set
                // and are not covered by a class deny (unless explicitly allowed).
                var desired = new HashSet<string>(instances, StringComparer.OrdinalIgnoreCase);
                foreach (var id in DisabledInstances.Keys.ToList())
                {
                    if (desired.Contains(id)) continue;
                    if (!allow.Contains(id) && IsInstanceInDeniedClass(id, guids)) continue;
                    var r = SetPnpEnabled(id, enable: true);
                    if (r == PnpOutcome.Changed) { changed = true; sb.Append(";enabled:").Append(Short(id)); }
                    DisabledInstances.TryRemove(id, out _);
                }

                // Ensure allow-list instances stay enabled even under a class deny.
                foreach (var id in allow)
                {
                    var r = SetPnpEnabled(id, enable: true);
                    if (r == PnpOutcome.Changed) { changed = true; sb.Append(";allowed:").Append(Short(id)); }
                    DisabledInstances.TryRemove(id, out _);
                }
            }

            // Explicit instance hard-fail still means "not fully applied" (retry).
            // Class registry + soft-fails still count as Applied when no explicit instance failed
            // and no explicit instance is still pending.
            if (failed)
                return new Result(false, pending, changed, sb.Length == 0 ? "failed" : sb.ToString());

            var applied = !pending;
            return new Result(applied, pending, changed, sb.Length == 0 ? "no_change" : sb.ToString());
        }
        catch (UnauthorizedAccessException)
        {
            return new Result(false, false, false, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, pending, changed, "error:" + ex.Message);
        }
    }

    /// <summary>Remove every port block this service applied and re-enable disabled devices.</summary>
    public static Result Clear()
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, false, "unsupported_os");
        try
        {
            var changed = false;
            var sb = new StringBuilder();
            lock (Gate)
            {
                // Stop the fast guard from re-disabling anything we are about to restore.
                _guardInstances = Array.Empty<string>();
                _guardClasses = Array.Empty<string>();
                _guardAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var id in DisabledInstances.Keys.ToList())
                {
                    var r = SetPnpEnabled(id, enable: true);
                    if (r == PnpOutcome.Changed)
                    {
                        changed = true;
                        sb.Append(";enabled:").Append(Short(id));
                    }
                    DisabledInstances.TryRemove(id, out _);
                }

                if (ClearClassRestrictions()) { changed = true; sb.Append(";classes_cleared"); }
                if (ClearAllowDeviceIds()) { changed = true; sb.Append(";allows_cleared"); }
            }

            // Bring back devices uninstalled by DenyDeviceClassesRetroactive.
            if (RunPowerShell("pnputil /scan-devices", out _))
                sb.Append(";rescanned");

            return new Result(true, false, changed, changed ? sb.ToString().TrimStart(';') : "nothing_to_clear");
        }
        catch (UnauthorizedAccessException)
        {
            return new Result(false, false, false, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, false, false, "error:" + ex.Message);
        }
    }

    // ── Class GUID normalization ────────────────────────────────────────────

    internal static IReadOnlyList<string> NormalizeClassGuids(IReadOnlyList<string> classes)
    {
        var list = new List<string>();
        foreach (var raw in classes)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();
            if (IsGuid(s))
            {
                list.Add(s);
                continue;
            }
            if (ClassNameToGuid.TryGetValue(s, out var g))
                list.Add(g);
        }
        return list;
    }

    // ── Device Installation Restrictions (class) ────────────────────────────

    private static (bool Changed, string Detail) ApplyClassRestrictions(IReadOnlyList<string> guids)
    {
        using var root = Registry.LocalMachine.CreateSubKey(RestrictionsKey, writable: true);
        if (root == null) return (false, "restrictions_key_missing");

        if (guids.Count == 0)
            return (ClearClassRestrictions(), "no_class_guids");

        var changed = false;
        // Windows Device Install Restrictions: the GUID must be the VALUE NAME
        // (empty REG_SZ data). Marker-named values are ignored by the OS policy engine.
        if ((root.GetValue("DenyDeviceClasses") as int?) != 1)
        {
            root.SetValue("DenyDeviceClasses", 1, RegistryValueKind.DWord);
            changed = true;
        }
        if ((root.GetValue("DenyDeviceClassesRetroactive") as int?) != 1)
        {
            root.SetValue("DenyDeviceClassesRetroactive", 1, RegistryValueKind.DWord);
            changed = true;
        }

        using var deny = root.CreateSubKey(DenyClassesSub, writable: true);
        if (deny == null) return (changed, "deny_sub_missing");

        var desired = new HashSet<string>(guids, StringComparer.OrdinalIgnoreCase);
        var owned = ReadOwnedNames(OwnedDenyKey);

        // Remove GUIDs we previously owned that are no longer desired (leave foreign GPO entries).
        foreach (var old in owned)
        {
            if (desired.Contains(old)) continue;
            deny.DeleteValue(old, throwOnMissingValue: false);
            // Also delete legacy AlphaAI-DLP* value-name mistakes from earlier builds.
            changed = true;
        }
        foreach (var name in deny.GetValueNames())
        {
            if (!name.StartsWith("AlphaAI-DLP", StringComparison.Ordinal)) continue;
            deny.DeleteValue(name, throwOnMissingValue: false);
            changed = true;
        }

        foreach (var g in desired)
        {
            // Value name = class GUID, data empty — format Windows actually enforces.
            var existing = deny.GetValue(g);
            if (existing == null)
            {
                deny.SetValue(g, "", RegistryValueKind.String);
                changed = true;
            }
        }

        WriteOwnedNames(OwnedDenyKey, desired);
        return (changed, $"deny_classes={guids.Count}");
    }

    private static bool ClearClassRestrictions()
    {
        using var root = Registry.LocalMachine.OpenSubKey(RestrictionsKey, writable: true);
        if (root == null) return false;
        var changed = false;
        var owned = ReadOwnedNames(OwnedDenyKey);
        using (var deny = root.OpenSubKey(DenyClassesSub, writable: true))
        {
            if (deny != null)
            {
                foreach (var g in owned)
                {
                    if (deny.GetValue(g) == null) continue;
                    deny.DeleteValue(g, throwOnMissingValue: false);
                    changed = true;
                }
                // Scrub legacy wrong-format markers from earlier builds.
                foreach (var name in deny.GetValueNames())
                {
                    if (!name.StartsWith("AlphaAI-DLP", StringComparison.Ordinal)) continue;
                    deny.DeleteValue(name, throwOnMissingValue: false);
                    changed = true;
                }
                if (deny.GetValueNames().Length == 0 && (root.GetValue("DenyDeviceClasses") as int?) == 1)
                {
                    root.SetValue("DenyDeviceClasses", 0, RegistryValueKind.DWord);
                    if ((root.GetValue("DenyDeviceClassesRetroactive") as int?) == 1)
                        root.SetValue("DenyDeviceClassesRetroactive", 0, RegistryValueKind.DWord);
                    changed = true;
                }
            }
        }
        if (DeleteOwnedKey(OwnedDenyKey)) changed = true;
        return changed;
    }

    private static (bool Changed, string Detail) ApplyAllowDeviceIds(IReadOnlyList<string> allowIds)
    {
        using var root = Registry.LocalMachine.CreateSubKey(RestrictionsKey, writable: true);
        if (root == null) return (false, "");
        using var allow = root.CreateSubKey(AllowIdsSub, writable: true);
        if (allow == null) return (false, "");

        var changed = false;
        var desired = new HashSet<string>(allowIds, StringComparer.OrdinalIgnoreCase);
        var owned = ReadOwnedNames(OwnedAllowKey);

        foreach (var old in owned)
        {
            if (desired.Contains(old)) continue;
            allow.DeleteValue(old, throwOnMissingValue: false);
            changed = true;
        }
        foreach (var name in allow.GetValueNames())
        {
            if (!name.StartsWith("AlphaAI-DLP", StringComparison.Ordinal)) continue;
            allow.DeleteValue(name, throwOnMissingValue: false);
            changed = true;
        }

        if (desired.Count == 0)
        {
            WriteOwnedNames(OwnedAllowKey, desired);
            if ((root.GetValue("AllowDeviceIDs") as int?) == 1 && allow.GetValueNames().Length == 0)
            {
                root.SetValue("AllowDeviceIDs", 0, RegistryValueKind.DWord);
                changed = true;
            }
            return (changed, changed ? "allows=0" : "");
        }

        if ((root.GetValue("AllowDeviceIDs") as int?) != 1)
        {
            root.SetValue("AllowDeviceIDs", 1, RegistryValueKind.DWord);
            changed = true;
        }
        foreach (var id in desired)
        {
            if (allow.GetValue(id) == null)
            {
                allow.SetValue(id, "", RegistryValueKind.String);
                changed = true;
            }
        }
        WriteOwnedNames(OwnedAllowKey, desired);
        return (changed, $"allows={allowIds.Count}");
    }

    private static bool ClearAllowDeviceIds()
    {
        using var root = Registry.LocalMachine.OpenSubKey(RestrictionsKey, writable: true);
        if (root == null) return false;
        var changed = false;
        var owned = ReadOwnedNames(OwnedAllowKey);
        using (var allow = root.OpenSubKey(AllowIdsSub, writable: true))
        {
            if (allow != null)
            {
                foreach (var id in owned)
                {
                    if (allow.GetValue(id) == null) continue;
                    allow.DeleteValue(id, throwOnMissingValue: false);
                    changed = true;
                }
                foreach (var name in allow.GetValueNames())
                {
                    if (!name.StartsWith("AlphaAI-DLP", StringComparison.Ordinal)) continue;
                    allow.DeleteValue(name, throwOnMissingValue: false);
                    changed = true;
                }
                if (allow.GetValueNames().Length == 0 && (root.GetValue("AllowDeviceIDs") as int?) == 1)
                {
                    root.SetValue("AllowDeviceIDs", 0, RegistryValueKind.DWord);
                    changed = true;
                }
            }
        }
        if (DeleteOwnedKey(OwnedAllowKey)) changed = true;
        return changed;
    }

    private static HashSet<string> ReadOwnedNames(string keyPath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
            if (key == null) return set;
            foreach (var name in key.GetValueNames())
            {
                if (!string.IsNullOrWhiteSpace(name))
                    set.Add(name);
            }
        }
        catch { /* ignore */ }
        return set;
    }

    private static void WriteOwnedNames(string keyPath, HashSet<string> names)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            if (names.Count == 0) return;
            using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
            if (key == null) return;
            foreach (var n in names)
                key.SetValue(n, "", RegistryValueKind.String);
        }
        catch { /* ignore */ }
    }

    private static bool DeleteOwnedKey(string keyPath)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── PnP enable / disable ────────────────────────────────────────────────

    private enum PnpOutcome { Missing, Unchanged, Changed, Vetoed, Failed }

    /// <summary>
    /// Enable / disable one device and VERIFY the result from the devnode problem code.
    /// Native CfgMgr32 first; Enable/Disable-PnpDevice only as a fallback when the native
    /// call fails for a reason other than "Windows refused" (veto) — never in the fast guard.
    /// </summary>
    private static PnpOutcome SetPnpEnabled(string instanceId, bool enable, bool allowFallback = true)
    {
        var before = CfgMgrDevices.Query(instanceId);
        if (before.State == CfgMgrDevices.DevState.Missing)
            return PnpOutcome.Missing;

        if (enable && before.State == CfgMgrDevices.DevState.Enabled)
            return PnpOutcome.Unchanged;
        if (!enable && before.State == CfgMgrDevices.DevState.Disabled)
            return PnpOutcome.Unchanged;

        var native = enable ? CfgMgrDevices.Enable(instanceId) : CfgMgrDevices.Disable(instanceId);
        if (native == CfgMgrDevices.ChangeResult.Missing)
            return PnpOutcome.Missing;
        if (native == CfgMgrDevices.ChangeResult.Vetoed)
            return PnpOutcome.Vetoed;

        if (Reached(instanceId, enable))
            return PnpOutcome.Changed;

        if (!allowFallback)
            return PnpOutcome.Failed;

        var escaped = EscapePs(instanceId);
        RunPowerShell(
            (enable ? "Enable-PnpDevice" : "Disable-PnpDevice") +
            $" -InstanceId '{escaped}' -Confirm:$false -ErrorAction Stop",
            out _);
        return Reached(instanceId, enable) ? PnpOutcome.Changed : PnpOutcome.Failed;
    }

    private static bool Reached(string instanceId, bool enable)
    {
        var s = CfgMgrDevices.Query(instanceId).State;
        return enable ? s == CfgMgrDevices.DevState.Enabled : s == CfgMgrDevices.DevState.Disabled;
    }

    private static List<string> ListPresentInstanceIdsByClassGuids(IReadOnlyList<string> guids) =>
        guids.SelectMany(CfgMgrDevices.ListPresentByClass)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsInstanceInDeniedClass(string instanceId, IReadOnlyList<string> guids)
    {
        if (guids.Count == 0) return false;
        var g = CfgMgrDevices.ClassGuidOf(instanceId);
        return g.Length > 0 && guids.Contains(g, StringComparer.OrdinalIgnoreCase);
    }

    private static bool RunPowerShell(string command, out string stdout)
    {
        stdout = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                            QuoteForCmd(command),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return false;
            stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteForCmd(string command) =>
        "\"" + command.Replace("\"", "\\\"") + "\"";

    private static string EscapePs(string s) => s.Replace("'", "''");

    private static bool IsGuid(string s) =>
        s.Length >= 38 && s.StartsWith("{", StringComparison.Ordinal) && s.EndsWith("}", StringComparison.Ordinal);

    private static string Short(string id) => id.Length <= 40 ? id : id[..40];
}
