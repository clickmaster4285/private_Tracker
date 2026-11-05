using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace client.Core;

/// <summary>
/// OS-metadata executable classification — the replacement for hardcoded name lists.
///
/// A Windows PE's Subsystem field is the operating system's OWN statement of what a binary
/// is: IMAGE_SUBSYSTEM_WINDOWS_GUI (2) = it draws windows (a GUI application);
/// IMAGE_SUBSYSTEM_WINDOWS_CUI (3) = it is a console/CLI tool. This is genuine OS metadata
/// that works on every Windows 7/10/11, every language, every department's toolset — with
/// zero application names involved. node/dotnet/git/go/python are CUI; Code/chrome/putty
/// are GUI.
/// </summary>
public static class ExecutableMetadata
{
    public const ushort SubsystemWindowsGui = 2;
    public const ushort SubsystemWindowsCui = 3;

    /// <summary>
    /// Read the PE Subsystem field of an .exe. Returns 0 when the file is not a readable PE
    /// (or the platform is not Windows) — callers treat 0 as "unknown", never as "GUI".
    /// </summary>
    public static ushort GetSubsystem(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return 0;
        try
        {
            if (!File.Exists(path)) return 0;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40) return 0;
            fs.Position = 0x3C; // e_lfanew
            var peOffset = br.ReadInt32();
            if (peOffset < 0 || peOffset + 24 > fs.Length) return 0;
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return 0; // "PE\0\0"
            // The optional header begins AFTER the 20-byte COFF header (Machine,
            // NumberOfSections, TimeDateStamp, …), i.e. at peOffset + 24. Reading the magic
            // right after the signature returns the Machine field (0x8664 = AMD64) instead —
            // that bug made EVERY x64 exe read as "unknown" (0).
            var optStart = peOffset + 24;
            fs.Position = optStart;
            var magic = br.ReadUInt16(); // 0x10B = PE32, 0x20B = PE32+
            if (magic is not (0x10B or 0x20B)) return 0;
            // IMAGE_OPTIONAL_HEADER.Subsystem sits at offset 68 in BOTH PE32 (0x10B) and
            // PE32+ (0x20B) — the 32/64-bit layout only differs AFTER DllCharacteristics
            // (ImageBase size), so the same +68 works.
            var subsystemOffset = optStart + 68;
            if (subsystemOffset + 2 > fs.Length) return 0;
            fs.Position = subsystemOffset;
            return br.ReadUInt16();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// True when the path lives ANYWHERE inside the Windows directory (System32, SysWOW64,
    /// SystemApps, WinSxS, …). Everything under C:\Windows is OS-provided — shell components,
    /// daemons, and runtime internals — never a user-installed GUI application.
    /// </summary>
    public static bool IsWindowsSystemTree(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(win)) return false;
            return path.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve the best executable path for a registry Uninstall entry, without any product
    /// names: DisplayIcon first (handles the "C:\\Prog\\app.exe",0 icon-resource syntax),
    /// then InstallLocation's eponymous .exe, then InstallLocation as an exact .exe path.
    /// Returns null when no .exe file exists on disk.
    /// </summary>
    public static string? ResolveExePath(string? installLocation, string? displayIcon)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var iconPath = CleanIconPath(displayIcon);
        if (iconPath != null && File.Exists(iconPath)) return iconPath;

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            var loc = installLocation.Trim().Trim('"', '\'');
            if (loc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(loc))
                return loc;
            if (Directory.Exists(loc))
            {
                var dirName = Path.GetFileName(loc.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(dirName))
                {
                    var eponymous = Path.Combine(loc, dirName + ".exe");
                    if (File.Exists(eponymous)) return eponymous;
                }
                // Office-style installs: no per-product exe in the install dir, but the dir
                // contains real GUI executables (WINWORD.EXE…). A CLI-only dir (nodejs\, Git\
                // bin…) has none — structural, no names.
                return ResolveDirectoryGuiExe(loc);
            }
        }
        return null;
    }

    /// <summary>
    /// First top-level GUI-subsystem executable inside an install directory, if any.
    /// GUI app directories (Microsoft Office\root\Office16) contain GUI exes; CLI/runtime
    /// directories (nodejs, Git, Go\bin) contain only console exes. Returns null otherwise.
    /// </summary>
    public static string? ResolveDirectoryGuiExe(string? installLocation)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(installLocation)) return null;
        try
        {
            var loc = installLocation.Trim().Trim('"', '\'');
            if (loc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(loc))
                return null;
            foreach (var exe in Directory.GetFiles(loc, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (GetSubsystem(exe) == SubsystemWindowsGui) return exe;
            }
        }
        catch { return null; }
        return null;
    }

    /// <summary>Normalize a DisplayIcon value like "C:\\Prog\\app.exe",0 to a bare .exe path.</summary>
    private static string? CleanIconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var s = displayIcon.Trim().Trim('"', '\'');
        var comma = s.IndexOf(',');
        if (comma > 0) s = s[..comma].Trim();
        if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return s;
    }

    /// <summary>Convention: an installer helper (unins000.exe / uninstall.exe / uninstall-postgresql.exe).</summary>
    public static bool IsUninstallerFileName(string fileNameWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension)) return false;
        var lower = fileNameWithoutExtension.ToLowerInvariant();
        return lower == "uninstall" || lower.StartsWith("unins", StringComparison.Ordinal);
    }

    /// <summary>
    /// Convention: an installer executable (*Setup*, OneDriveSetup, …). Real GUI applications
    /// are not named "*setup"; these binaries install/update the software the registry row
    /// describes. Naming convention, not a product list.
    /// </summary>
    public static bool IsInstallerFileName(string fileNameWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension)) return false;
        var lower = fileNameWithoutExtension.ToLowerInvariant();
        return lower == "setup" || lower.EndsWith("setup", StringComparison.Ordinal) ||
               lower.EndsWith("installer", StringComparison.Ordinal) ||
               lower.StartsWith("setup_", StringComparison.Ordinal) ||
               lower.StartsWith("setup-", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the path lives in the MSI bootstrapper cache
    /// (C:\ProgramData\Package Cache) — the bundles installers stage there (VC_redist.x64,
    /// python-*-amd64.exe, dotnet-sdk-*-win-x64.exe…). GUI-subsystem, but they are
    /// INSTALLERS, not applications. Structural path, no product names.
    /// </summary>
    public static bool IsInstallerCachePath(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return false;
        return path.Contains(@"ProgramData\Package Cache", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Structural registry filter SHARED by both detectors (single source of truth, no drift):
    /// internal installer bookkeeping (SystemComponent / ParentKeyName / ReleaseType), Windows
    /// Update rows (KB prefix convention), and installer-helper entries (LocalServiceComponents
    /// / "… Uninstaller"). No product names.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool IsSystemOrUpdateRegistryRow(Microsoft.Win32.RegistryKey subKey, string displayName)
    {
        try
        {
            if (subKey.GetValue("SystemComponent") is int sc && sc == 1) return true;
            if (subKey.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent)) return true;
            if (subKey.GetValue("ReleaseType") is string rt &&
                (rt.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                 rt.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
                return true;

            // Windows Update rows (KB = Knowledge Base hotfix IDs — an OS naming convention).
            if (displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains("Security Update", StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains("Update for ", StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
                return true;

            // Installer helper rows that are not applications.
            if (displayName.Contains("LocalServiceComponents", StringComparison.OrdinalIgnoreCase) ||
                displayName.EndsWith(" Uninstaller", StringComparison.OrdinalIgnoreCase) ||
                displayName.EndsWith(" Uninstall", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { return false; }
        return false;
    }

    /// <summary>True when the entry registers http/https URL protocols (a browser application).</summary>
    [SupportedOSPlatform("windows")]
    public static bool HasUrlAssociations(Microsoft.Win32.RegistryKey subKey)
    {
        try
        {
            using var urlAssoc = subKey.OpenSubKey("URLAssociations");
            return urlAssoc != null && (urlAssoc.GetValue("http") != null || urlAssoc.GetValue("https") != null);
        }
        catch { return false; }
    }
}
