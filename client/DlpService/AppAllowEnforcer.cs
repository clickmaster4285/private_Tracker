using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace AlphaAITracker.Service;

/// <summary>
/// P3 application allowlist via Windows AppLocker (Windows only).
///
/// When lockdown is ON:
/// - <b>Exe</b> collection Enforce = default-deny Win32 (Calculator Win32, Taskmgr, Chrome, …)
/// - <b>Appx</b> collection Enforce = default-deny Store/UWP (modern Calculator, Photos, …)
/// - Allow only bootstrap A (minimal Start/shell / this product) + published allow.apps
/// - PowerShell is <b>SYSTEM-only</b> (S-1-5-18) so this service can still Set-AppLockerPolicy;
///   interactive users cannot launch powershell / ISE / pwsh until published on allow.apps
///
/// Launch is blocked by the OS (Windows blocked-by-organization dialog). Process kill is a
/// separate sweeper for anything already running.
/// </summary>
internal static class AppAllowEnforcer
{
    private static readonly object Gate = new();
    private static long _generation;
    private static string _lastFingerprint = "";
    private static bool _policyMaybePresent = true;
    private static bool _lastVerifyEnforcing;

    /// <summary>LOCAL SYSTEM — service Set-AppLockerPolicy / AppID helpers.</summary>
    private const string LocalSystemSid = "S-1-5-18";
    private const string EveryoneSid = "S-1-1-0";

    /// <summary>
    /// Microsoft Store / Appx package family names required for lock screen / Start chrome.
    /// Intentionally omits Settings / ImmersiveControlPanel — those stay default-deny until
    /// published on allow.apps.
    /// </summary>
    /// <summary>
    /// Shell / lock / logon Appx only. Intentionally omits Store, Calculator, Settings,
    /// Desktop App Installer — those stay default-deny until published on allow.apps.
    /// </summary>
    private static readonly string[] BootstrapAppxProducts =
    [
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.Search",
        "Microsoft.Windows.CloudExperienceHost",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
        "Microsoft.LockApp",
        "Microsoft.Windows.AppResolverUX",
        "Microsoft.Windows.CallingShellApp",
        "Microsoft.Win32WebViewHost",
        "Microsoft.UI.Xaml.CBS",
        // Client.CBS covers some Start chrome; ImmersiveControlPanel/Settings stay
        // denied unless path-published (no broad WindowsApps\* path rules).
        "MicrosoftWindows.Client.CBS",
    ];

    /// <summary>Exposed for <see cref="AppProcessGuard"/> WindowsApps spare-list.</summary>
    internal static IReadOnlyList<string> BootstrapAppxProductNames => BootstrapAppxProducts;

    public sealed record Result(bool Changed, bool Applied, string Detail);

    public static long CurrentGeneration => Interlocked.Read(ref _generation);

    public static Result Apply(IReadOnlyList<AllowAppEntry> allowApps)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os");

        var gen = Interlocked.Increment(ref _generation);
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var publishers = new List<(string Publisher, string Product)>();
        var appxProducts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileHashes = new List<(string HashHex, string FileName, long Length)>();

        foreach (var p in BootstrapPaths())
            paths.Add(NormalizeAppLockerPath(p));
        foreach (var pkg in BootstrapAppxProducts)
            appxProducts.Add(pkg);

        foreach (var a in allowApps)
        {
            if (!string.IsNullOrWhiteSpace(a.Path))
            {
                var norm = NormalizeAppLockerPath(a.Path);
                // Never allow user-writable paths (Desktop / Downloads / Temp / AppData) —
                // those are the classic AppLocker bypass drop locations.
                if (IsUnsafeUserWritableAllow(norm) || IsUnsafeUserWritableAllow(a.Path))
                    continue;
                paths.Add(norm);
                // Store apps under WindowsApps: also allow the package product for Appx enforce.
                TryAddAppxFromPath(norm, appxProducts);
            }
            if (!string.IsNullOrWhiteSpace(a.Publisher) && string.IsNullOrWhiteSpace(a.Path)
                && !IsBroadMicrosoftPublisher(a.Publisher))
            {
                publishers.Add((a.Publisher.Trim(), (a.Product ?? "").Trim()));
            }
            if (!string.IsNullOrWhiteSpace(a.Product) && LooksLikePackageFamily(a.Product))
                appxProducts.Add(a.Product.Trim());

            // Published sha256 → AppLocker FileHash (needs on-disk length when path resolves).
            TryAddFileHash(a, fileHashes);
        }

        var systemOnly = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in SystemOnlyBootstrapPaths())
            systemOnly.Add(NormalizeAppLockerPath(p));
        // winget / App Installer: LocalSystem + tracker-parent only (not interactive Everyone).
        foreach (var p in ProductDependencyPaths())
            systemOnly.Add(NormalizeAppLockerPath(p));

        var fp = Fingerprint(paths, publishers, appxProducts, systemOnly, fileHashes);
        lock (Gate)
        {
            if (gen != Interlocked.Read(ref _generation))
                return new Result(false, false, "stale_generation");

            if (fp == _lastFingerprint && _lastVerifyEnforcing)
                return new Result(false, true, "unchanged;verify=enforcing");

            // Re-apply on verify miss (or first Set). Order matters: AppID restart after a
            // DWORD write re-materializes SrpV2 and drops EnforcementMode — write AFTER
            // the single AppID restart, then verify.
            EnsureAppIdService(restart: false);

            // Leftover NotConfigured tree after uninstall/reinstall can layer under Set.
            if (!_lastVerifyEnforcing)
                TryDeleteLocalAppLockerStore();

            var xml = BuildPolicyXml(paths, publishers, appxProducts, systemOnly, fileHashes);
            var apply = SetAppLockerPolicy(xml);
            if (!apply.Ok)
                return new Result(false, false, apply.Detail);

            // Set-AppLockerPolicy writes the LOCAL policy; a Group Policy refresh is what
            // copies it into the enforced (effective) SrpV2 store. Without this the stored
            // policy shows the rules but the effective set stays empty (0 rules enforced),
            // so nothing is actually blocked. Proven on Win10 22H2: gpupdate took effective
            // Exe rules 0 -> 54. Bounded + best-effort; verify below reads -Effective.
            RefreshGroupPolicy();

            // Restart AppID once so rules bind, then heal DWORD and assert Enabled
            // *after* restart (restart can leave a stale NotConfigured view).
            EnsureAppIdService(restart: true);
            Thread.Sleep(1500);

            if (gen != Interlocked.Read(ref _generation))
                return new Result(false, false, "stale_generation");

            WriteEnforcementModeRegistry(enforcing: true);
            WriteEnforcementModeViaPowerShell(enforcing: true);

            var assert = AssertPolicyEnabled();
            if (!assert.Ok)
            {
                // Re-Set once without another AppID stop/start (avoids DWORD drop loop).
                apply = SetAppLockerPolicy(xml);
                if (!apply.Ok)
                    return new Result(false, false, apply.Detail);
                WriteEnforcementModeRegistry(enforcing: true);
                WriteEnforcementModeViaPowerShell(enforcing: true);
                assert = AssertPolicyEnabled();
                if (!assert.Ok)
                    return new Result(false, false, apply.Detail + ";" + assert.Detail);
            }

            // Gate passes when Exe Enabled + deny canary not Allowed (DWORD best-effort).
            var verify = VerifyEnforced();
            if (!verify.Contains("verify=enforcing", StringComparison.OrdinalIgnoreCase))
            {
                WriteEnforcementModeRegistry(enforcing: true);
                WriteEnforcementModeViaPowerShell(enforcing: true);
                verify = VerifyEnforced();
            }

            var changed = fp != _lastFingerprint || _policyMaybePresent || !_lastVerifyEnforcing;
            _lastFingerprint = fp;
            _policyMaybePresent = true;
            _lastVerifyEnforcing = verify.Contains("verify=enforcing", StringComparison.OrdinalIgnoreCase)
                && !verify.Contains("NOT_ENFORCING", StringComparison.OrdinalIgnoreCase);
            var detail = apply.Detail + ";" + assert.Detail + ";" + verify;
            return new Result(changed, _lastVerifyEnforcing, detail);
        }
    }

    public static Result Clear()
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os");

        lock (Gate)
        {
            // Already cleared in this process — skip PowerShell / AppID restart.
            if (!_policyMaybePresent && _lastFingerprint == "")
                return new Result(false, true, "already_clear");

            Interlocked.Increment(ref _generation);

            // Wipe the local AppLocker store — fingerprint short-circuit left Exe
            // Enforce in AppID memory after protected-uninstall when pipe unlock raced.
            TryDeleteLocalAppLockerStore();

            var xml = BuildEmptyPolicyXml();
            var apply = SetAppLockerPolicy(xml);
            if (!apply.Ok)
                return new Result(false, false, apply.Detail);

            EnsureAppIdService(restart: true);
            WriteEnforcementModeRegistry(enforcing: false);
            var changed = _policyMaybePresent || _lastFingerprint != "";
            _lastFingerprint = "";
            _policyMaybePresent = false;
            _lastVerifyEnforcing = false;
            return new Result(changed, true, "cleared");
        }
    }

    /// <summary>
    /// Delete <c>HKLM\SOFTWARE\Policies\Microsoft\Windows\SrpV2</c> so NotConfigured
    /// is not layered on top of a leftover Enforce tree.
    /// </summary>
    private static void TryDeleteLocalAppLockerStore()
    {
        try
        {
            Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(
                @"SOFTWARE\Policies\Microsoft\Windows\SrpV2",
                throwOnMissingSubKey: false);
        }
        catch
        {
            // Best-effort — Set-AppLockerPolicy still runs.
        }
    }

    /// <summary>
    /// Bootstrap A (Everyone) — product + Explorer / Start / minimal shell.
    /// Omits Settings, Control Panel, PowerShell/pwsh/ISE, Taskmgr, browsers, Office.
    /// Publish paths on /allowlists/apps to reopen anything else.
    /// </summary>
    internal static IEnumerable<string> BootstrapPaths()
    {
        // Prefer AppLocker macros so rules survive Windows folder localization.
        string[] shell =
        [
            @"%WINDIR%\explorer.exe",
            @"%SYSTEM32%\userinit.exe",
            @"%SYSTEM32%\sihost.exe",
            @"%SYSTEM32%\taskhostw.exe",
            @"%SYSTEM32%\RuntimeBroker.exe",
            @"%SYSTEM32%\ApplicationFrameHost.exe",
            @"%SYSTEM32%\ctfmon.exe",
            @"%SYSTEM32%\dwm.exe",
            @"%SYSTEM32%\fontdrvhost.exe",
            @"%SYSTEM32%\conhost.exe",
            @"%SYSTEM32%\svchost.exe",
            @"%SYSTEM32%\winlogon.exe",
            @"%SYSTEM32%\csrss.exe",
            @"%SYSTEM32%\smss.exe",
            @"%SYSTEM32%\services.exe",
            @"%SYSTEM32%\lsass.exe",
            @"%SYSTEM32%\wininit.exe",
            @"%SYSTEM32%\LogonUI.exe",
            @"%SYSTEM32%\dllhost.exe",
            @"%SYSTEM32%\backgroundTaskHost.exe",
            @"%SYSTEM32%\SearchHost.exe",
            @"%SYSTEM32%\ShellHost.exe",
            @"%SYSTEM32%\TextInputHost.exe",
            @"%SYSTEM32%\consent.exe",
            @"%SYSTEM32%\smartscreen.exe",
            @"%SYSTEM32%\SecurityHealthService.exe",
            @"%SYSTEM32%\SecurityHealthSystray.exe",
            @"%SYSTEM32%\wbem\WmiPrvSE.exe",
            // msiexec is LocalSystem-only (LOLBin) — see SystemOnlyBootstrapPaths.
        ];
        foreach (var p in shell)
            yield return p;

        // Appx product rules are primary for Start/Search; avoid broad WindowsApps\* path
        // wildcards (they can re-allow Settings binaries under Client.CBS).

        // Named product binaries only — never plant-under-product via folder "*".
        foreach (var p in ProductBinaryPaths())
            yield return p;
        // winget / choco / App Installer are NOT Everyone — see SystemOnlyBootstrapPaths
        // via ProductDependencyPaths (LocalSystem). AppProcessGuard keeps tracker-parent
        // inventory launches and kills Explorer/Start/Search opens.
    }

    /// <summary>
    /// Tracker / service EXEs under Program Files and the running service directory.
    /// Path wildcards on the whole install folder are intentionally not used.
    /// </summary>
    private static IEnumerable<string> ProductBinaryPaths()
    {
        string[] names =
        [
            "client.exe",
            ServiceIdentity.ServiceName + ".exe",
            "AlphaAITracker.Service.exe",
        ];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serviceDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(serviceDir))
        {
            foreach (var n in names)
            {
                var full = Path.Combine(serviceDir, n);
                if (seen.Add(full))
                    yield return full;
            }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var folder = ServiceIdentity.DisplayName;
            if (string.IsNullOrWhiteSpace(folder))
                folder = ServiceIdentity.AppMutex;
            foreach (var dir in new[]
                     {
                         Path.Combine(root, folder),
                         Path.Combine(root, ServiceIdentity.AppMutex),
                     })
            {
                foreach (var n in names)
                {
                    var full = Path.Combine(dir, n);
                    if (seen.Add(full))
                        yield return full;
                }
            }
        }
    }

    /// <summary>
    /// Inventory helpers for LocalSystem / tracker-parent only. Interactive users stay
    /// default-deny (Store / App Installer / winget blocked until allow.apps).
    /// </summary>
    private static IEnumerable<string> ProductDependencyPaths()
    {
        yield return @"%OSDRIVE%\Users\*\AppData\Local\Microsoft\WindowsApps\winget.exe";
        yield return @"%OSDRIVE%\Users\*\AppData\Local\Microsoft\WindowsApps\WindowsPackageManagerServer.exe";
        yield return @"%PROGRAMFILES%\WindowsApps\Microsoft.DesktopAppInstaller*\winget.exe";
        yield return @"%PROGRAMFILES%\WindowsApps\Microsoft.DesktopAppInstaller*\WindowsPackageManagerServer.exe";
        yield return @"%PROGRAMFILES%\WindowsApps\Microsoft.DesktopAppInstaller*\AppInstaller.exe";
        yield return @"%PROGRAMDATA%\chocolatey\bin\choco.exe";
        yield return @"%SYSTEM32%\wbem\WMIC.exe";
    }

    /// <summary>
    /// LOCAL SYSTEM only — PowerShell (Set-AppLockerPolicy) plus the System32
    /// maintenance tools teardown / silent-update helpers need. Interactive users
    /// stay default-deny until allow.apps publishes a path (AppLocker self-lockout
    /// for user-spawned sc/taskkill is intentional; SYSTEM helpers must still run).
    /// </summary>
    internal static IEnumerable<string> SystemOnlyBootstrapPaths()
    {
        yield return @"%SYSTEM32%\WindowsPowerShell\v1.0\powershell.exe";
        yield return @"%WINDIR%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe";
        yield return @"%SYSTEM32%\cmd.exe";
        yield return @"%SYSTEM32%\sc.exe";
        yield return @"%SYSTEM32%\reg.exe";
        yield return @"%SYSTEM32%\taskkill.exe";
        yield return @"%SYSTEM32%\schtasks.exe";
        yield return @"%SYSTEM32%\ipconfig.exe";
        yield return @"%SYSTEM32%\netsh.exe";
        // gpupdate materializes the just-Set local AppLocker policy into the enforced
        // (effective) SrpV2 store. Must be SYSTEM-allowed or the service's own refresh
        // is default-denied once Exe enforce is active.
        yield return @"%SYSTEM32%\gpupdate.exe";
        // msiexec is a classic LOLBin — LocalSystem only (installer / silent update).
        yield return @"%SYSTEM32%\msiexec.exe";
        // Silent updater stages Inno under ProgramData\<mutex>\updates\; without this
        // allow, Exe Enforce default-deny blocks Start-Process of the downloaded EXE.
        // SYSTEM-only so interactive users cannot run arbitrary EXEs from that folder.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ServiceIdentity.DataFolderName,
            "updates",
            "*");
    }

    private static bool IsBroadMicrosoftPublisher(string publisher)
    {
        var p = publisher.Trim();
        return p.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Microsoft Windows", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePackageFamily(string product) =>
        product.Contains('.', StringComparison.Ordinal) &&
        !product.Contains('\\', StringComparison.Ordinal) &&
        !product.Contains('/', StringComparison.Ordinal) &&
        product.Length < 128;

    private static void TryAddAppxFromPath(string path, SortedSet<string> appxProducts)
    {
        // ...\WindowsApps\Microsoft.WindowsCalculator_8wekyb3d8bbwe\...
        const string marker = @"\WindowsApps\";
        var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;
        var rest = path[(idx + marker.Length)..];
        var seg = rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(seg)) return;
        var under = seg.IndexOf('_');
        var family = under > 0 ? seg[..under] : seg.TrimEnd('*');
        if (!string.IsNullOrWhiteSpace(family))
            appxProducts.Add(family);
    }

    /// <summary>
    /// Map filesystem paths to AppLocker path variables. AppLocker only understands
    /// %WINDIR%/%SYSTEM32%/%OSDRIVE%/%PROGRAMFILES% (not %PROGRAMFILES(X86)%).
    /// Per MS docs, %PROGRAMFILES% covers both Program Files and Program Files (x86).
    /// Matching must use a directory boundary and check PF(x86) before PF — otherwise
    /// <c>C:\Program Files (x86)\…</c> becomes the invalid
    /// <c>%PROGRAMFILES% (x86)\…</c> (StartsWith "Program Files" eats the x86 root).
    /// </summary>
    internal static string NormalizeAppLockerPath(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        // Legacy invalid macros from older builds — rewrite so Apply re-emits valid XML.
        if (p.StartsWith("%PROGRAMFILES(X86)%", StringComparison.OrdinalIgnoreCase))
            p = "%PROGRAMFILES%" + p["%PROGRAMFILES(X86)%".Length..];
        // Broken form produced when PF matched before PF(x86): "%PROGRAMFILES% (x86)\…"
        if (p.StartsWith("%PROGRAMFILES% (x86)", StringComparison.OrdinalIgnoreCase))
            p = "%PROGRAMFILES%" + p["%PROGRAMFILES% (x86)".Length..];
        if (p.StartsWith("%", StringComparison.Ordinal))
            return p;

        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (StartsWithDir(p, sys))
            return "%SYSTEM32%" + p[sys.Length..];
        if (StartsWithDir(p, windir))
            return "%WINDIR%" + p[windir.Length..];
        // PF(x86) before PF — "C:\Program Files" is a prefix of "C:\Program Files (x86)".
        if (StartsWithDir(p, pf86))
            return "%PROGRAMFILES%" + p[pf86.Length..];
        if (StartsWithDir(p, pf))
            return "%PROGRAMFILES%" + p[pf.Length..];
        return p;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="dir"/> or <paramref name="dir"/> + '\'.
    /// Prevents <c>C:\Program Files</c> from matching <c>C:\Program Files (x86)\…</c>.
    /// </summary>
    private static bool StartsWithDir(string path, string? dir)
    {
        if (string.IsNullOrEmpty(dir))
            return false;
        if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Length == dir.Length || path[dir.Length] == '\\';
    }

    private static void TryAddFileHash(
        AllowAppEntry a,
        List<(string HashHex, string FileName, long Length)> fileHashes)
    {
        var sha = (a.Sha256 ?? "").Trim().ToLowerInvariant()
            .Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
            return;

        var fileName = "allow.bin";
        long length = 0;
        var path = (a.Path ?? "").Trim();
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(
                    path.Replace("%SYSTEM32%", Environment.GetFolderPath(Environment.SpecialFolder.System),
                            StringComparison.OrdinalIgnoreCase)
                        .Replace("%WINDIR%", Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                            StringComparison.OrdinalIgnoreCase)
                        .Replace("%PROGRAMFILES%",
                            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                            StringComparison.OrdinalIgnoreCase));
                if (File.Exists(expanded))
                {
                    length = new FileInfo(expanded).Length;
                    fileName = Path.GetFileName(expanded);
                }
                else
                {
                    fileName = Path.GetFileName(path.TrimEnd('*', '\\', '/'));
                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = "allow.bin";
                    // AppLocker rejects FileHash without a positive SourceFileLength.
                    return;
                }
            }
            catch
            {
                return;
            }
        }
        else
        {
            // Hash-only publish without path — cannot invent length; skip.
            return;
        }

        if (length <= 0)
            return;
        if (fileHashes.Any(h =>
                string.Equals(h.HashHex, sha, StringComparison.OrdinalIgnoreCase)))
            return;
        fileHashes.Add((sha, fileName, length));
    }

    private static string Fingerprint(
        SortedSet<string> paths,
        List<(string Publisher, string Product)> publishers,
        SortedSet<string> appxProducts,
        SortedSet<string> systemOnlyPaths,
        List<(string HashHex, string FileName, long Length)> fileHashes)
    {
        var sb = new StringBuilder();
        // v9: msiexec SYSTEM-only; named product binaries; FileHash; canary fail-closed.
        sb.Append("v9;");
        foreach (var p in paths) sb.Append("P:").Append(p).Append(';');
        foreach (var p in systemOnlyPaths) sb.Append("S:").Append(p).Append(';');
        foreach (var (pub, prod) in publishers.OrderBy(x => x.Publisher + "|" + x.Product, StringComparer.OrdinalIgnoreCase))
            sb.Append("U:").Append(pub).Append('|').Append(prod).Append(';');
        foreach (var a in appxProducts) sb.Append("X:").Append(a).Append(';');
        foreach (var h in fileHashes.OrderBy(x => x.HashHex, StringComparer.OrdinalIgnoreCase))
            sb.Append("H:").Append(h.HashHex).Append('|').Append(h.Length).Append(';');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    private static string BuildPolicyXml(
        SortedSet<string> paths,
        List<(string Publisher, string Product)> publishers,
        SortedSet<string> appxProducts,
        SortedSet<string> systemOnlyPaths,
        List<(string HashHex, string FileName, long Length)> fileHashes)
    {
        var exeRules = new List<XElement>();
        foreach (var path in paths)
            exeRules.Add(PathRule(path, EveryoneSid));
        foreach (var path in systemOnlyPaths)
            exeRules.Add(PathRule(path, LocalSystemSid));
        foreach (var (pub, prod) in publishers)
            exeRules.Add(PublisherRule(pub, prod));
        foreach (var h in fileHashes)
            exeRules.Add(FileHashRule(h.HashHex, h.FileName, h.Length));

        var appxRules = new List<XElement>();
        foreach (var product in appxProducts)
            appxRules.Add(AppxProductRule(product));

        // Script enforce with SYSTEM-only interpreter paths closes .ps1/.bat/.vbs/.cmd
        // interactive bypasses. DLL stays NotConfigured (DLL default-deny bricks Windows
        // without a full WDAC signed base); WdacEnforcer owns DLL/driver when CIP is signed.
        var scriptRules = new List<XElement>();
        foreach (var path in systemOnlyPaths)
        {
            if (path.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                || path.Contains("pwsh", StringComparison.OrdinalIgnoreCase)
                || path.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase)
                || path.Contains("cscript", StringComparison.OrdinalIgnoreCase)
                || path.Contains("wscript", StringComparison.OrdinalIgnoreCase))
            {
                scriptRules.Add(PathRule(path, LocalSystemSid));
            }
        }
        // Allow signed Windows script hosts under System32 for SYSTEM maintenance only.
        scriptRules.Add(PathRule(@"%SYSTEM32%\*.ps1", LocalSystemSid));
        scriptRules.Add(PathRule(@"%SYSTEM32%\*.bat", LocalSystemSid));
        scriptRules.Add(PathRule(@"%SYSTEM32%\*.cmd", LocalSystemSid));
        scriptRules.Add(PathRule(@"%SYSTEM32%\WindowsPowerShell\v1.0\*", LocalSystemSid));

        var doc = new XDocument(
            new XElement("AppLockerPolicy",
                new XAttribute("Version", "1"),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Exe"),
                    new XAttribute("EnforcementMode", "Enabled"),
                    exeRules),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Msi"),
                    new XAttribute("EnforcementMode", "Enabled")),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Script"),
                    new XAttribute("EnforcementMode", "Enabled"),
                    scriptRules),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Dll"),
                    new XAttribute("EnforcementMode", "NotConfigured")),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Appx"),
                    new XAttribute("EnforcementMode", "Enabled"),
                    appxRules)));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// True when an allow path points at a user-writable location (AppLocker bypass).
    /// </summary>
    public static bool IsUnsafeUserWritableAllow(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var p = path.Trim().ToLowerInvariant()
            .Replace('/', '\\');
        // Expand common macros for the check.
        p = p.Replace("%osdrive%", "c:")
            .Replace("%systemdrive%", "c:");
        string[] banned =
        [
            "\\users\\",
            "\\appdata\\",
            "\\temp\\",
            "\\tmp\\",
            "\\downloads\\",
            "\\desktop\\",
            "\\documents\\",
            "%userprofile%",
            "%localappdata%",
            "%appdata%",
            "%temp%",
            "%tmp%",
            "\\programdata\\microsoft\\windows\\start menu\\programs\\startup\\",
        ];
        foreach (var b in banned)
        {
            if (p.Contains(b, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string BuildEmptyPolicyXml()
    {
        var doc = new XDocument(
            new XElement("AppLockerPolicy",
                new XAttribute("Version", "1"),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Exe"),
                    new XAttribute("EnforcementMode", "NotConfigured")),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Msi"),
                    new XAttribute("EnforcementMode", "NotConfigured")),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Script"),
                    new XAttribute("EnforcementMode", "NotConfigured")),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Dll"),
                    new XAttribute("EnforcementMode", "NotConfigured")),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Appx"),
                    new XAttribute("EnforcementMode", "NotConfigured"))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement PathRule(string path, string userOrGroupSid)
    {
        var id = Guid.NewGuid().ToString();
        var sidTag = userOrGroupSid == LocalSystemSid ? "sys " : "";
        return new XElement("FilePathRule",
            new XAttribute("Id", id),
            new XAttribute("Name", "AlphaAI " + sidTag + "path " + Truncate(path, 80)),
            new XAttribute("Description", "AlphaAI-DLP-APP"),
            new XAttribute("UserOrGroupSid", userOrGroupSid),
            new XAttribute("Action", "Allow"),
            new XElement("Conditions",
                new XElement("FilePathCondition",
                    new XAttribute("Path", path))));
    }

    private static XElement FileHashRule(string sha256Hex, string fileName, long length)
    {
        var id = Guid.NewGuid().ToString();
        var data = "0x" + sha256Hex.ToUpperInvariant();
        return new XElement("FileHashRule",
            new XAttribute("Id", id),
            new XAttribute("Name", "AlphaAI hash " + Truncate(fileName, 60)),
            new XAttribute("Description", "AlphaAI-DLP-APP-HASH"),
            new XAttribute("UserOrGroupSid", EveryoneSid),
            new XAttribute("Action", "Allow"),
            new XElement("Conditions",
                new XElement("FileHashCondition",
                    new XElement("FileHash",
                        new XAttribute("Type", "SHA256"),
                        new XAttribute("Data", data),
                        new XAttribute("SourceFileName", fileName),
                        new XAttribute("SourceFileLength", length.ToString())))));
    }

    private static XElement PublisherRule(string publisher, string product)
    {
        var id = Guid.NewGuid().ToString();
        var productAttr = string.IsNullOrEmpty(product) ? "*" : product;
        return new XElement("FilePublisherRule",
            new XAttribute("Id", id),
            new XAttribute("Name", "AlphaAI publisher " + Truncate(publisher, 60)),
            new XAttribute("Description", "AlphaAI-DLP-APP"),
            new XAttribute("UserOrGroupSid", "S-1-1-0"),
            new XAttribute("Action", "Allow"),
            new XElement("Conditions",
                new XElement("FilePublisherCondition",
                    new XAttribute("PublisherName", publisher),
                    new XAttribute("ProductName", productAttr),
                    new XAttribute("BinaryName", "*"),
                    new XElement("BinaryVersionRange",
                        new XAttribute("LowSection", "*"),
                        new XAttribute("HighSection", "*")))));
    }

    private static XElement AppxProductRule(string productName)
    {
        var id = Guid.NewGuid().ToString();
        // PublisherName=* scopes to ProductName (package family) — blocks Calculator while
        // allowing StartMenuExperienceHost.
        return new XElement("FilePublisherRule",
            new XAttribute("Id", id),
            new XAttribute("Name", "AlphaAI appx " + productName),
            new XAttribute("Description", "AlphaAI-DLP-APP"),
            new XAttribute("UserOrGroupSid", "S-1-1-0"),
            new XAttribute("Action", "Allow"),
            new XElement("Conditions",
                new XElement("FilePublisherCondition",
                    new XAttribute("PublisherName", "*"),
                    new XAttribute("ProductName", productName),
                    new XAttribute("BinaryName", "*"),
                    new XElement("BinaryVersionRange",
                        new XAttribute("LowSection", "*"),
                        new XAttribute("HighSection", "*")))));
    }

    private static (bool Ok, string Detail) SetAppLockerPolicy(string xml)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ServiceIdentity.AppMutex,
            "dlp");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return (false, "policy_dir_failed:" + ex.Message);
        }

        var path = Path.Combine(dir, "applocker.policy.xml");
        try
        {
            // AppLocker expects Unicode (UTF-16 LE) XML on many builds.
            File.WriteAllText(path, xml, Encoding.Unicode);
        }
        catch (Exception ex)
        {
            return (false, "policy_write_failed:" + ex.Message);
        }

        var ps =
            "$ErrorActionPreference='Stop'; " +
            $"Set-AppLockerPolicy -XmlPolicy '{path.Replace("'", "''")}' -ErrorAction Stop; " +
            "'ok'";
        var (exit, stdout, stderr) = RunPowerShell(ps);
        if (exit != 0)
        {
            var detail = (stderr + " " + stdout).Trim();
            if (detail.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                return (false, "access_denied_need_admin");
            if (detail.Contains("AppLocker", StringComparison.OrdinalIgnoreCase)
                && (detail.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                    || detail.Contains("not available", StringComparison.OrdinalIgnoreCase)))
                return (false, "applocker_unavailable:" + Truncate(detail, 200));
            return (false, "set_policy_failed:" + Truncate(detail, 280));
        }

        return (true, "set_policy_ok");
    }

    /// <summary>
    /// Confirm Set-AppLockerPolicy left Exe + Appx Enabled with at least one rule.
    /// Exit 0 from Set alone is not enough — lab PCs can stay NotConfigured.
    /// </summary>
    private static (bool Ok, string Detail) AssertPolicyEnabled()
    {
        // Read -Effective (what AppLocker actually enforces), NOT -Local (the stored copy).
        // The stored policy can show rules while the enforced set is empty (0 rules) — that
        // was reporting the gate green while nothing was blocked.
        const string ps =
            "$ErrorActionPreference='Stop'; " +
            "$p = Get-AppLockerPolicy -Effective; " +
            "$exe = @($p.RuleCollections | Where-Object { $_.RuleCollectionType.ToString() -eq 'Exe' })[0]; " +
            "$appx = @($p.RuleCollections | Where-Object { $_.RuleCollectionType.ToString() -eq 'Appx' })[0]; " +
            "if ($null -eq $exe) { 'still_notconfigured no_exe'; exit 0 }; " +
            "if ([string]$exe.EnforcementMode -ne 'Enabled') { 'still_notconfigured mode=' + $exe.EnforcementMode; exit 0 }; " +
            "$exeRules = 0; try { $exeRules = [int]$exe.Count } catch { $exeRules = 0 }; " +
            "if ($exeRules -lt 1) { 'still_notconfigured effective_exe_rules=0'; exit 0 }; " +
            "if ($null -eq $appx -or [string]$appx.EnforcementMode -ne 'Enabled') { " +
            "  $am = if ($null -eq $appx) { 'missing' } else { [string]$appx.EnforcementMode }; " +
            "  'still_notconfigured appx_mode=' + $am; exit 0 }; " +
            "'assert_ok effective_exe_rules=' + $exeRules";
        var (exit, stdout, stderr) = RunPowerShell(ps);
        var outp = (stdout + " " + stderr).Trim();
        if (exit != 0)
            return (false, "set_policy_failed:assert_error " + Truncate(outp, 200));
        if (outp.Contains("still_notconfigured", StringComparison.OrdinalIgnoreCase))
            return (false, "set_policy_failed:still_notconfigured " + Truncate(outp, 160));
        if (!outp.Contains("assert_ok", StringComparison.OrdinalIgnoreCase))
            return (false, "set_policy_failed:still_notconfigured " + Truncate(outp, 160));
        return (true, Truncate(outp, 80));
    }

    /// <summary>
    /// Fixed path that is never in bootstrap or published allow.apps. Used as the
    /// AppLocker behavioral canary — Task Manager cannot be the probe because admins
    /// may publish it on /allowlists/apps (Allowed is then correct, not a failure).
    /// A tiny stub file is created when missing; <c>Test-AppLockerPolicy</c> often
    /// throws if the path does not exist on disk.
    /// </summary>
    internal const string DenyCanaryPath = @"C:\AlphaAI-DLP-DenyCanary.exe";

    /// <summary>
    /// Ensures a small non-runnable stub exists at <see cref="DenyCanaryPath"/> so
    /// <c>Test-AppLockerPolicy -Path</c> can evaluate it. Never allowlisted.
    /// </summary>
    internal static void EnsureDenyCanaryStub()
    {
        try
        {
            if (File.Exists(DenyCanaryPath) && new FileInfo(DenyCanaryPath).Length > 0)
                return;
            // Minimal DOS MZ header only — not a valid PE; probe is path-based.
            var stub = new byte[]
            {
                0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00,
                0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00,
            };
            File.WriteAllBytes(DenyCanaryPath, stub);
            try
            {
                File.SetAttributes(
                    DenyCanaryPath,
                    FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed);
            }
            catch
            {
                /* attributes are best-effort */
            }
        }
        catch
        {
            /* probe still runs; missing file may throw → canary_probe_failed */
        }
    }

    private static string VerifyEnforced()
    {
        // Primary proof: Exe collection Enabled + deny canary not Allowed.
        // Registry DWORD is healed best-effort — some builds leave it absent while
        // Get-AppLockerPolicy still reports Enabled and AppLocker actually denies.
        var reg = ReadExeEnforcementMode();
        if (reg != 1)
        {
            WriteEnforcementModeRegistry(enforcing: true);
            WriteEnforcementModeViaPowerShell(enforcing: true);
            reg = ReadExeEnforcementMode();
        }

        EnsureDenyCanaryStub();

        var canary = DenyCanaryPath.Replace("'", "''");
        // Never use $ErrorActionPreference=Stop for the whole script — Test-AppLockerPolicy
        // throws on some builds and was failing the gate with verify_failed even when
        // Get-AppLockerPolicy already reported Exe Enabled.
        var ps =
            "$ErrorActionPreference='Continue'; " +
            // -Effective = the enforced policy. -Local showed rules while 0 were enforced.
            "try { $p = Get-AppLockerPolicy -Effective } catch { 'verify_failed get_policy ' + $_.Exception.Message; exit 0 }; " +
            "$col = @($p.RuleCollections | Where-Object { $_.RuleCollectionType.ToString() -eq 'Exe' }); " +
            "if ($col.Count -eq 0) { 'verify=NOT_ENFORCING no_exe_collection'; exit 0 }; " +
            "$mode = [string]$col[0].EnforcementMode; " +
            "if ($mode -ne 'Enabled') { 'verify=NOT_ENFORCING mode=' + $mode; exit 0 }; " +
            // An Enabled Exe collection with zero enforced rules blocks nothing on some
            // builds — require at least one effective rule before trusting the canary.
            "$exeRules = 0; try { $exeRules = [int]$col[0].Count } catch { $exeRules = 0 }; " +
            "if ($exeRules -lt 1) { 'verify=NOT_ENFORCING effective_exe_rules=0'; exit 0 }; " +
            "$dec = ''; " +
            "try { " +
            $"  $r = @(Test-AppLockerPolicy -PolicyObject $p -Path '{canary}' -User Everyone -ErrorAction Stop); " +
            "  $dec = (@($r | ForEach-Object { [string]$_.PolicyDecision }) | Select-Object -First 4) -join ','; " +
            "} catch { " +
            // Fail-closed: never treat a probe exception as Enforcing.
            "  'verify=NOT_ENFORCING canary_probe_failed err=' + $_.Exception.Message; exit 0 " +
            "}; " +
            "if ($dec -match 'Allowed') { 'verify=NOT_ENFORCING canary_allowed decision=' + $dec } " +
            "else { 'verify=enforcing decision=' + $dec }";
        var (exit, stdout, stderr) = RunPowerShell(ps);
        var outp = (stdout + " " + stderr).Trim();
        // exit!=0 with empty/partial stdout: still parse stdout for verify= lines.
        if (outp.Contains("verify=enforcing", StringComparison.OrdinalIgnoreCase)
            && !outp.Contains("NOT_ENFORCING", StringComparison.OrdinalIgnoreCase))
        {
            return reg == 1
                ? "verify=enforcing registry=Enabled " + Truncate(outp, 120)
                : "verify=enforcing registry_heal_pending=" + reg + " " + Truncate(outp, 120);
        }
        if (exit != 0 && !outp.Contains("verify=", StringComparison.OrdinalIgnoreCase))
            return "verify_failed registry=" + reg + " " + Truncate(outp, 160);
        return "verify=NOT_ENFORCING registry=" + reg + " " + Truncate(outp, 160);
    }

    /// <summary>
    /// Second-path heal when managed RegistryKey write does not stick (WOW64 / policy refresh).
    /// </summary>
    private static void WriteEnforcementModeViaPowerShell(bool enforcing)
    {
        var value = enforcing ? 1 : 0;
        // CRITICAL: never `New-Item -Force` on an existing SrpV2\<collection> key — in
        // PowerShell that DELETES the key's child rule GUIDs that Set-AppLockerPolicy just
        // wrote, leaving an Enabled-but-empty collection (effective rules = 0 while the
        // stored/local policy still shows them). Create the key only when missing, then set
        // just the EnforcementMode value with New-ItemProperty (that replaces one value, not
        // the whole key).
        var ps =
            "$ErrorActionPreference='SilentlyContinue'; " +
            @"$paths = @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2\Exe'," +
            @"'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2\Appx'," +
            @"'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2\Msi'," +
            @"'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2\Script'); " +
            "foreach ($p in $paths) { " +
            "  if (-not (Test-Path -LiteralPath $p)) { New-Item -Path $p -Force | Out-Null }; " +
            $"  New-ItemProperty -LiteralPath $p -Name EnforcementMode -PropertyType DWord -Value {value} -Force | Out-Null; " +
            "}; 'ok'";
        try { RunPowerShell(ps); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// 1 = Enforcing, 0 = Not configured, 2 = Audit, -1 = value missing.
    /// </summary>
    private static int ReadExeEnforcementMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\SrpV2\Exe");
            var raw = key?.GetValue("EnforcementMode");
            if (raw is int i)
                return i;
            if (raw != null && int.TryParse(raw.ToString(), out var parsed))
                return parsed;
            return -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Set-AppLockerPolicy sometimes leaves <c>SrpV2\*\EnforcementMode</c> absent while
    /// writing AllowWindows. The policy gate requires DWORD 1 (Enforcing). Returns false
    /// if any collection write fails (do not swallow — gate must see registry_write_failed).
    /// </summary>
    private static bool WriteEnforcementModeRegistry(bool enforcing)
    {
        var value = enforcing ? 1 : 0;
        string[] collections = ["Exe", "Appx", "Msi", "Script"];
        var ok = true;
        foreach (var name in collections)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\SrpV2\" + name,
                    writable: true);
                if (key is null)
                {
                    ok = false;
                    continue;
                }
                key.SetValue("EnforcementMode", value, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch
            {
                ok = false;
            }
        }
        // Confirm Exe stuck — the gate keys off this collection.
        if (enforcing && ReadExeEnforcementMode() != 1)
            ok = false;
        return ok;
    }

    private static void EnsureAppIdService(bool restart)
    {
        // sc.exe runs as LocalSystem (session 0). PowerShell Start-Service was easy to
        // miss when the helper was slow, so AppIDSvc stayed stopped and verify never passed.
        TrySc("config AppIDSvc start= auto");
        if (restart)
            TrySc("stop AppIDSvc");
        TrySc("start AppIDSvc");
        var ps = restart
            ? "Set-Service -Name AppIDSvc -StartupType Automatic -ErrorAction SilentlyContinue; " +
              "Start-Service -Name AppIDSvc -ErrorAction SilentlyContinue; 'ok'"
            : "Set-Service -Name AppIDSvc -StartupType Automatic -ErrorAction SilentlyContinue; " +
              "Start-Service -Name AppIDSvc -ErrorAction SilentlyContinue; 'ok'";
        try { RunPowerShell(ps); }
        catch { /* sc.exe already attempted the start */ }
    }

    /// <summary>
    /// Force a computer Group Policy refresh so a just-Set local AppLocker policy becomes
    /// the enforced (effective) policy. Bounded and best-effort — verify reads -Effective.
    /// </summary>
    private static void RefreshGroupPolicy()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "gpupdate.exe"),
                Arguments = "/target:computer /force",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc is null)
                return;
            // Drain so a chatty gpupdate cannot fill its pipe and hang.
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
        }
        catch
        {
            // Best-effort — a refresh failure surfaces as verify=NOT_ENFORCING below.
        }
    }

    private static void TrySc(string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc?.WaitForExit(15_000);
        }
        catch
        {
            /* best-effort */
        }
    }

    private static (int Exit, string StdOut, string StdErr) RunPowerShell(string command)
    {
        // Prefer System32 path — AppLocker Script rules allow this for LocalSystem;
        // bare "powershell.exe" can resolve to a WindowsApps alias that is denied.
        var psExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(psExe))
            psExe = "powershell.exe";
        var psi = new ProcessStartInfo
        {
            FileName = psExe,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + QuoteArg(command),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null)
            return (-1, "", "process_start_failed");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(90_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return (-1, stdout, "timeout");
        }
        return (proc.ExitCode, stdout, stderr);
    }

    private static string QuoteArg(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
