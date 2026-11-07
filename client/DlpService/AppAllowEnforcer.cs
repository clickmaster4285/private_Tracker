using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace AlphaAITracker.Service;

/// <summary>
/// P3 application allowlist via Windows AppLocker (Windows only).
///
/// When lockdown is ON:
/// - <b>Exe</b> collection Enforce = default-deny Win32 (Calculator Win32, Taskmgr, Chrome, …)
/// - <b>Appx</b> collection Enforce = default-deny Store/UWP (modern Calculator, Photos, …)
/// - Allow only bootstrap A (Explorer / Start / shell / this product) + published allow.apps
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

    /// <summary>Microsoft Store / Appx package family names required for desktop survival.</summary>
    private static readonly string[] BootstrapAppxProducts =
    [
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.Search",
        "MicrosoftWindows.Client.CBS",
        "Microsoft.Windows.CloudExperienceHost",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
        "Microsoft.LockApp",
        "Microsoft.Windows.AppResolverUX",
        "Microsoft.Windows.CallingShellApp",
        "Microsoft.Win32WebViewHost",
        "Microsoft.UI.Xaml.CBS",
        "MicrosoftWindows.Client.WebExperience",
    ];

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

        foreach (var p in BootstrapPaths())
            paths.Add(NormalizeAppLockerPath(p));
        foreach (var pkg in BootstrapAppxProducts)
            appxProducts.Add(pkg);

        foreach (var a in allowApps)
        {
            if (!string.IsNullOrWhiteSpace(a.Path))
            {
                var norm = NormalizeAppLockerPath(a.Path);
                paths.Add(norm);
                // Store apps under WindowsApps: also allow the package product for Appx enforce.
                TryAddAppxFromPath(norm, appxProducts);
            }
            // Skip SHA rules without file length — invalid FileHash rules can reject the whole policy.
            if (!string.IsNullOrWhiteSpace(a.Publisher) && string.IsNullOrWhiteSpace(a.Path)
                && !IsBroadMicrosoftPublisher(a.Publisher))
            {
                publishers.Add((a.Publisher.Trim(), (a.Product ?? "").Trim()));
            }
            if (!string.IsNullOrWhiteSpace(a.Product) && LooksLikePackageFamily(a.Product))
                appxProducts.Add(a.Product.Trim());
        }

        var fp = Fingerprint(paths, publishers, appxProducts);
        lock (Gate)
        {
            if (gen != Interlocked.Read(ref _generation))
                return new Result(false, false, "stale_generation");

            if (fp == _lastFingerprint)
                return new Result(false, true, "unchanged");

            EnsureAppIdService(restart: false);

            var xml = BuildPolicyXml(paths, publishers, appxProducts);
            var apply = SetAppLockerPolicy(xml);
            if (!apply.Ok)
                return new Result(false, false, apply.Detail);

            // Restart AppID so enforce mode binds immediately (otherwise rules can sit inert).
            EnsureAppIdService(restart: true);

            if (gen != Interlocked.Read(ref _generation))
                return new Result(false, false, "stale_generation");

            var verify = VerifyEnforced();
            var changed = fp != _lastFingerprint || _policyMaybePresent;
            _lastFingerprint = fp;
            _policyMaybePresent = true;
            var detail = apply.Detail + ";" + verify;
            return new Result(changed, true, detail);
        }
    }

    public static Result Clear()
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os");

        Interlocked.Increment(ref _generation);
        lock (Gate)
        {
            if (!_policyMaybePresent && _lastFingerprint == "")
                return new Result(false, true, "already_clear");

            var xml = BuildEmptyPolicyXml();
            var apply = SetAppLockerPolicy(xml);
            if (!apply.Ok)
                return new Result(false, false, apply.Detail);

            EnsureAppIdService(restart: true);
            var changed = _policyMaybePresent || _lastFingerprint != "";
            _lastFingerprint = "";
            _policyMaybePresent = false;
            return new Result(changed, true, "cleared");
        }
    }

    /// <summary>
    /// Bootstrap A — product + Explorer / Start / minimal shell. Omits Taskmgr, calc,
    /// notepad, Settings, browsers, Office.
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
            @"%SYSTEM32%\WidgetService.exe",
            @"%SYSTEM32%\SystemSettingsBroker.exe",
            @"%SYSTEM32%\consent.exe",
            @"%SYSTEM32%\smartscreen.exe",
            @"%SYSTEM32%\SecurityHealthService.exe",
            @"%SYSTEM32%\SecurityHealthSystray.exe",
            // Service must keep applying policy under Everyone SID rules.
            @"%SYSTEM32%\WindowsPowerShell\v1.0\powershell.exe",
            @"%SYSTEM32%\WindowsPowerShell\v1.0\powershell_ise.exe",
            @"%WINDIR%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe",
            @"%SYSTEM32%\wbem\WmiPrvSE.exe",
            @"%SYSTEM32%\msiexec.exe",
        ];
        foreach (var p in shell)
            yield return p;

        // Packaged shell hosts (Exe path fallback; Appx rules are primary for Store packages).
        yield return @"%PROGRAMFILES%\WindowsApps\Microsoft.Windows.StartMenuExperienceHost*\*";
        yield return @"%PROGRAMFILES%\WindowsApps\Microsoft.Windows.ShellExperienceHost*\*";
        yield return @"%PROGRAMFILES%\WindowsApps\Microsoft.Windows.Search*\*";
        yield return @"%PROGRAMFILES%\WindowsApps\MicrosoftWindows.Client.CBS*\*";

        var serviceDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(serviceDir))
            yield return Path.Combine(serviceDir, "*");

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
            yield return Path.Combine(root, folder, "*");
            yield return Path.Combine(root, ServiceIdentity.AppMutex, "*");
        }
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

    private static string NormalizeAppLockerPath(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith("%", StringComparison.Ordinal))
            return p;

        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(sys) && p.StartsWith(sys, StringComparison.OrdinalIgnoreCase))
            return "%SYSTEM32%" + p[sys.Length..];
        if (!string.IsNullOrEmpty(windir) && p.StartsWith(windir, StringComparison.OrdinalIgnoreCase))
            return "%WINDIR%" + p[windir.Length..];
        if (!string.IsNullOrEmpty(pf) && p.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
            return "%PROGRAMFILES%" + p[pf.Length..];
        if (!string.IsNullOrEmpty(pf86) && p.StartsWith(pf86, StringComparison.OrdinalIgnoreCase))
            return "%PROGRAMFILES(X86)%" + p[pf86.Length..];
        return p;
    }

    private static string Fingerprint(
        SortedSet<string> paths,
        List<(string Publisher, string Product)> publishers,
        SortedSet<string> appxProducts)
    {
        var sb = new StringBuilder();
        sb.Append("v2;");
        foreach (var p in paths) sb.Append("P:").Append(p).Append(';');
        foreach (var (pub, prod) in publishers.OrderBy(x => x.Publisher + "|" + x.Product, StringComparer.OrdinalIgnoreCase))
            sb.Append("U:").Append(pub).Append('|').Append(prod).Append(';');
        foreach (var a in appxProducts) sb.Append("X:").Append(a).Append(';');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    private static string BuildPolicyXml(
        SortedSet<string> paths,
        List<(string Publisher, string Product)> publishers,
        SortedSet<string> appxProducts)
    {
        var exeRules = new List<XElement>();
        foreach (var path in paths)
            exeRules.Add(PathRule(path));
        foreach (var (pub, prod) in publishers)
            exeRules.Add(PublisherRule(pub, prod));

        var appxRules = new List<XElement>();
        foreach (var product in appxProducts)
            appxRules.Add(AppxProductRule(product));

        // Exe + Appx enforced = Win32 and Store default-deny. MSI enforced empty = no installs.
        // Script/Dll stay NotConfigured (enabling DLL default-deny bricks Windows).
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
                    new XAttribute("EnforcementMode", "NotConfigured")),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Dll"),
                    new XAttribute("EnforcementMode", "NotConfigured")),
                new XElement("RuleCollection",
                    new XAttribute("Type", "Appx"),
                    new XAttribute("EnforcementMode", "Enabled"),
                    appxRules)));
        return doc.ToString(SaveOptions.DisableFormatting);
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

    private static XElement PathRule(string path)
    {
        var id = Guid.NewGuid().ToString();
        return new XElement("FilePathRule",
            new XAttribute("Id", id),
            new XAttribute("Name", "AlphaAI path " + Truncate(path, 80)),
            new XAttribute("Description", "AlphaAI-DLP-APP"),
            new XAttribute("UserOrGroupSid", "S-1-1-0"),
            new XAttribute("Action", "Allow"),
            new XElement("Conditions",
                new XElement("FilePathCondition",
                    new XAttribute("Path", path))));
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

    private static string VerifyEnforced()
    {
        // Probe Taskmgr — must be Denied under Everyone when Exe enforce works.
        var taskmgr = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe");
        var ps =
            "$ErrorActionPreference='Stop'; " +
            "$p = Get-AppLockerPolicy -Local; " +
            "$exe = @($p.RuleCollections | Where-Object { $_.RuleCollectionType -eq 'Exe' -or $_.GetType().Name -match 'Exe' }); " +
            "try { " +
            $"  $r = Test-AppLockerPolicy -PolicyObject $p -Path '{taskmgr.Replace("'", "''")}' -User Everyone -Filter Denied; " +
            "  if ($r) { 'verify=taskmgr_denied' } else { 'verify=taskmgr_allowed_FAIL' } " +
            "} catch { 'verify_err=' + $_.Exception.Message }";
        var (exit, stdout, stderr) = RunPowerShell(ps);
        var outp = (stdout + " " + stderr).Trim();
        if (exit != 0)
            return "verify_failed:" + Truncate(outp, 120);
        if (outp.Contains("taskmgr_allowed_FAIL", StringComparison.OrdinalIgnoreCase))
            return "verify=NOT_ENFORCING";
        if (outp.Contains("taskmgr_denied", StringComparison.OrdinalIgnoreCase))
            return "verify=enforcing";
        return "verify=" + Truncate(outp, 80);
    }

    private static void EnsureAppIdService(bool restart)
    {
        var ps = restart
            ? "Set-Service -Name AppIDSvc -StartupType Automatic -ErrorAction SilentlyContinue; " +
              "Stop-Service -Name AppIDSvc -Force -ErrorAction SilentlyContinue; " +
              "Start-Service -Name AppIDSvc -ErrorAction SilentlyContinue; 'ok'"
            : "Set-Service -Name AppIDSvc -StartupType Automatic -ErrorAction SilentlyContinue; " +
              "Start-Service -Name AppIDSvc -ErrorAction SilentlyContinue; 'ok'";
        try { RunPowerShell(ps); }
        catch { /* best-effort */ }
    }

    private static (int Exit, string StdOut, string StdErr) RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
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
