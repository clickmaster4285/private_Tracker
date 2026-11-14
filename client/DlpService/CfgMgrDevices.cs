using System.Runtime.InteropServices;
using System.Text;

namespace AlphaAITracker.Service;

/// <summary>
/// Thin wrapper over the Windows Configuration Manager API (cfgmgr32.dll) — the same
/// device tree Device Manager shows. Replaces per-device PowerShell calls: every
/// call here is an in-process native call (microseconds), so the port guard can
/// re-check every blocked device every couple of seconds without spawning processes.
///
/// "Disabled" is decided from the devnode problem code (CM_PROB_DISABLED = 22),
/// never from a status string — Get-PnpDevice reports a disabled device as
/// Status "Error", which is why the old string check never saw "Disabled".
/// </summary>
internal static class CfgMgrDevices
{
    public enum DevState
    {
        /// <summary>No present devnode with this instance id (unplugged / never seen).</summary>
        Missing,
        /// <summary>Present and running (no problem code).</summary>
        Enabled,
        /// <summary>Present and disabled (problem code 22).</summary>
        Disabled,
        /// <summary>Present with some other problem code (driver error etc.).</summary>
        Problem,
        /// <summary>The query itself failed (unexpected CONFIGRET).</summary>
        Unknown,
    }

    public readonly record struct DevStatus(DevState State, uint ProblemCode);

    public enum ChangeResult
    {
        Ok,
        Missing,
        /// <summary>Windows refused (device in use / not disableable / remove vetoed).</summary>
        Vetoed,
        Failed,
    }

    private const uint CR_SUCCESS = 0x00;
    private const uint CR_NO_SUCH_DEVNODE = 0x0D;
    private const uint CR_REMOVE_VETOED = 0x17;
    private const uint CR_BUFFER_SMALL = 0x1A;
    private const uint CR_NOT_DISABLEABLE = 0x21;

    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x0;
    private const uint DN_HAS_PROBLEM = 0x00000400;
    private const uint CM_PROB_DISABLED = 22;

    private const uint CM_DISABLE_UI_NOT_OK = 0x4;
    private const uint CM_DISABLE_PERSIST = 0x8;

    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    private const uint CM_GETIDLIST_FILTER_CLASS = 0x200;

    private const uint CM_DRP_CLASSGUID = 0x09;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_Registry_PropertyW(
        uint dnDevInst, uint ulProperty, out uint pulRegDataType, [Out] byte[]? buffer, ref uint pulLength, uint ulFlags);

    /// <summary>Live state of one device instance.</summary>
    public static DevStatus Query(string instanceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(instanceId))
            return new DevStatus(DevState.Unknown, 0);

        var cr = CM_Locate_DevNodeW(out var inst, instanceId, CM_LOCATE_DEVNODE_NORMAL);
        if (cr == CR_NO_SUCH_DEVNODE)
            return new DevStatus(DevState.Missing, 0);
        if (cr != CR_SUCCESS)
            return new DevStatus(DevState.Unknown, 0);

        cr = CM_Get_DevNode_Status(out var status, out var problem, inst, 0);
        if (cr == CR_NO_SUCH_DEVNODE)
            return new DevStatus(DevState.Missing, 0);
        if (cr != CR_SUCCESS)
            return new DevStatus(DevState.Unknown, 0);

        if ((status & DN_HAS_PROBLEM) != 0)
        {
            return problem == CM_PROB_DISABLED
                ? new DevStatus(DevState.Disabled, problem)
                : new DevStatus(DevState.Problem, problem);
        }
        return new DevStatus(DevState.Enabled, 0);
    }

    /// <summary>Disable persistently (survives reboot), no reboot UI.</summary>
    public static ChangeResult Disable(string instanceId) => Change(instanceId, enable: false);

    /// <summary>Enable (clears our disable).</summary>
    public static ChangeResult Enable(string instanceId) => Change(instanceId, enable: true);

    private static ChangeResult Change(string instanceId, bool enable)
    {
        if (!OperatingSystem.IsWindows())
            return ChangeResult.Failed;
        var cr = CM_Locate_DevNodeW(out var inst, instanceId, CM_LOCATE_DEVNODE_NORMAL);
        if (cr == CR_NO_SUCH_DEVNODE) return ChangeResult.Missing;
        if (cr != CR_SUCCESS) return ChangeResult.Failed;

        cr = enable
            ? CM_Enable_DevNode(inst, 0)
            : CM_Disable_DevNode(inst, CM_DISABLE_UI_NOT_OK | CM_DISABLE_PERSIST);

        return cr switch
        {
            CR_SUCCESS => ChangeResult.Ok,
            CR_NO_SUCH_DEVNODE => ChangeResult.Missing,
            CR_REMOVE_VETOED or CR_NOT_DISABLEABLE => ChangeResult.Vetoed,
            _ => ChangeResult.Failed,
        };
    }

    /// <summary>Instance ids of PRESENT devices whose setup class is <paramref name="classGuid"/>.</summary>
    public static List<string> ListPresentByClass(string classGuid)
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(classGuid))
            return result;

        const uint flags = CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var len, classGuid, flags) != CR_SUCCESS || len == 0)
                return result;
            var buf = new char[len];
            var cr = CM_Get_Device_ID_ListW(classGuid, buf, len, flags);
            if (cr == CR_BUFFER_SMALL) continue; // a device arrived between the two calls
            if (cr != CR_SUCCESS) return result;

            var start = 0;
            for (var i = 0; i < buf.Length; i++)
            {
                if (buf[i] != '\0') continue;
                if (i > start) result.Add(new string(buf, start, i - start));
                else break; // double NUL = end of multi-sz
                start = i + 1;
            }
            return result;
        }
        return result;
    }

    /// <summary>Setup-class GUID ("{…}") of a present device, or "" when unknown.</summary>
    public static string ClassGuidOf(string instanceId)
    {
        if (!OperatingSystem.IsWindows()) return "";
        if (CM_Locate_DevNodeW(out var inst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
            return "";
        uint len = 0;
        var cr = CM_Get_DevNode_Registry_PropertyW(inst, CM_DRP_CLASSGUID, out _, null, ref len, 0);
        if ((cr != CR_BUFFER_SMALL && cr != CR_SUCCESS) || len == 0)
            return "";
        var buf = new byte[len];
        if (CM_Get_DevNode_Registry_PropertyW(inst, CM_DRP_CLASSGUID, out _, buf, ref len, 0) != CR_SUCCESS)
            return "";
        return Encoding.Unicode.GetString(buf, 0, (int)len).TrimEnd('\0').Trim();
    }
}
