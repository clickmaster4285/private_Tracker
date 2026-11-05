using System.Diagnostics;
using Microsoft.Win32;

namespace AlphaAITracker.Service;

/// <summary>
/// Coarse USB mass-storage gate.
/// Windows: USBSTOR service Start (3=manual / 4=disabled).
/// Linux: udev rule authorizing interface class 08 (mass storage) to 0 + reload.
/// Does not block HID.
/// </summary>
internal static class UsbStorEnforcer
{
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\USBSTOR";
    public const int StartManual = 3;
    public const int StartDisabled = 4;

    /// <summary>Installed when policy wants USB mass-storage blocked.</summary>
    public static string LinuxUdevRulesPath =>
        $"/etc/udev/rules.d/99-{ServiceIdentity.PackageName}-usb-block.rules";

    public sealed record Result(bool Changed, int? PreviousStart, int DesiredStart, string Detail);

    public static Result Apply(bool block)
    {
        if (OperatingSystem.IsLinux())
            return ApplyLinux(block);
        if (OperatingSystem.IsWindows())
            return ApplyWindows(block);
        return new Result(false, null, block ? StartDisabled : StartManual, "unsupported_os");
    }

    public static int? ReadCurrentStart()
    {
        if (OperatingSystem.IsLinux())
            return File.Exists(LinuxUdevRulesPath) ? StartDisabled : StartManual;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: false);
            var v = key?.GetValue("Start");
            return v is int i ? i : v is long l ? (int)l : null;
        }
        catch
        {
            return null;
        }
    }

    private static Result ApplyLinux(bool block)
    {
        var desired = block ? StartDisabled : StartManual;
        var path = LinuxUdevRulesPath;
        var had = File.Exists(path);
        var prev = had ? StartDisabled : StartManual;

        if (block == had)
            return new Result(false, prev, desired, "already_applied");

        try
        {
            if (block)
            {
                var dir = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(dir);
                // Interface class 08 = Mass Storage. Keyboard/mouse (03) untouched.
                var body =
                    "# " + ServiceIdentity.DisplayName + " DLP — block USB mass storage\n" +
                    "ACTION==\"add\", SUBSYSTEM==\"usb\", ENV{DEVTYPE}==\"usb_interface\", " +
                    "ATTR{bInterfaceClass}==\"08\", ATTR{authorized}=\"0\"\n" +
                    "ACTION==\"add\", SUBSYSTEM==\"block\", ENV{ID_USB_DRIVER}==\"usb-storage\", " +
                    "ENV{UDISKS_IGNORE}=\"1\"\n";
                File.WriteAllText(path, body);
            }
            else
            {
                File.Delete(path);
            }

            ReloadUdev();
            DeauthorizeAttachedMassStorage(block);
            return new Result(true, prev, desired, block ? "udev_usb_block_on" : "udev_usb_block_off");
        }
        catch (UnauthorizedAccessException)
        {
            return new Result(false, prev, desired, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, prev, desired, "error:" + ex.Message);
        }
    }

    private static void ReloadUdev()
    {
        try
        {
            Run("udevadm", "control --reload-rules");
            Run("udevadm", "trigger --subsystem-match=usb --action=add");
        }
        catch
        {
            /* best-effort */
        }
    }

    /// <summary>
    /// Flip authorized=0/1 on already-enumerated USB mass-storage interfaces.
    /// </summary>
    private static void DeauthorizeAttachedMassStorage(bool block)
    {
        try
        {
            var usbRoot = "/sys/bus/usb/devices";
            if (!Directory.Exists(usbRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(usbRoot))
            {
                var ifaceClass = Path.Combine(dir, "bInterfaceClass");
                if (!File.Exists(ifaceClass)) continue;
                var cls = File.ReadAllText(ifaceClass).Trim();
                if (cls != "08") continue;
                var auth = Path.Combine(dir, "authorized");
                if (!File.Exists(auth)) continue;
                try
                {
                    File.WriteAllText(auth, block ? "0" : "1");
                }
                catch
                {
                    /* ignore per-device failures */
                }
            }
        }
        catch
        {
            /* ignore */
        }
    }

    private static Result ApplyWindows(bool block)
    {
        var desired = block ? StartDisabled : StartManual;

        int? prev;
        try
        {
            using var readKey = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: false);
            if (readKey == null)
                return new Result(false, null, desired, "usbstor_key_missing");

            var prevObj = readKey.GetValue("Start");
            prev = prevObj is int i ? i : prevObj is long l ? (int)l : StartManual;
            if (prev == desired)
                return new Result(false, prev, desired, "already_applied");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new Result(false, null, desired, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, null, desired, "error:" + ex.Message);
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey, writable: true);
            if (key == null)
                return new Result(false, prev, desired, "usbstor_key_missing");

            key.SetValue("Start", desired, RegistryValueKind.DWord);
            return new Result(true, prev, desired, block ? "usbstor_disabled" : "usbstor_enabled");
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new Result(false, prev, desired, "access_denied_need_admin");
        }
        catch (Exception ex)
        {
            return new Result(false, prev, desired, "error:" + ex.Message);
        }
    }

    private static void Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        p?.WaitForExit(8_000);
    }

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is System.Security.SecurityException
        || ex.Message.Contains("access is not allowed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);
}
