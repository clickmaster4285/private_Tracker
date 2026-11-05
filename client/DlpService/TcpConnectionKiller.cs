using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AlphaAITracker.Service;

/// <summary>
/// Force-closes established IPv4 TCP connections to blocked remote IPs (MIB_TCP_STATE_DELETE_TCB).
/// Used after firewall apply so an already-open YouTube/HTTPS tab dies without winsock/NIC reset.
/// UDP/QUIC is covered by the outbound firewall rules (no equivalent delete-TCB for UDP).
/// LocalSystem / admin required for SetTcpEntry.
/// </summary>
internal static class TcpConnectionKiller
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5; // TCP_TABLE_OWNER_PID_ALL
    private const uint MibTcpStateDeleteTcb = 12;

    /// <summary>Returns how many connections were requested to delete.</summary>
    public static int KillToRemoteIps(IEnumerable<string> remoteIps)
    {
        if (!OperatingSystem.IsWindows())
            return 0;

        var targets = new HashSet<uint>();
        foreach (var raw in remoteIps)
        {
            if (!IPAddress.TryParse(raw, out var addr))
                continue;
            if (addr.AddressFamily != AddressFamily.InterNetwork)
                continue;
            if (IPAddress.IsLoopback(addr) || addr.Equals(IPAddress.Any))
                continue;
            targets.Add(IpToUInt(addr));
        }
        if (targets.Count == 0)
            return 0;

        try
        {
            var rows = GetTcpRows();
            var killed = 0;
            foreach (var row in rows)
            {
                if (!targets.Contains(row.dwRemoteAddr))
                    continue;
                // Skip listen / time-wait noise; focus on active remotes.
                if (row.dwState is 2 /* LISTEN */)
                    continue;
                var del = row;
                del.dwState = MibTcpStateDeleteTcb;
                var rc = SetTcpEntry(ref del);
                if (rc == 0)
                    killed++;
            }
            return killed;
        }
        catch
        {
            return 0;
        }
    }

    private static uint IpToUInt(IPAddress addr)
    {
        // Match MIB_TCPROW layout returned by GetExtendedTcpTable on Windows.
        var b = addr.GetAddressBytes();
        return BitConverter.ToUInt32(b, 0);
    }

    private static List<MibTcpRow> GetTcpRows()
    {
        var list = new List<MibTcpRow>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
            return list;

        var buf = Marshal.AllocHGlobal(size);
        try
        {
            var ret = GetExtendedTcpTable(buf, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
            if (ret != 0)
                return list;

            var numEntries = Marshal.ReadInt32(buf);
            // DWORD dwNumEntries then MIB_TCPROW_OWNER_PID[].
            var ownerRowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var ptr = IntPtr.Add(buf, 4);
            for (var i = 0; i < numEntries; i++)
            {
                var owner = Marshal.PtrToStructure<MibTcpRowOwnerPid>(ptr);
                list.Add(new MibTcpRow
                {
                    dwState = owner.dwState,
                    dwLocalAddr = owner.dwLocalAddr,
                    dwLocalPort = owner.dwLocalPort,
                    dwRemoteAddr = owner.dwRemoteAddr,
                    dwRemotePort = owner.dwRemotePort,
                });
                ptr = IntPtr.Add(ptr, ownerRowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tblClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int SetTcpEntry(ref MibTcpRow pTcpRow);
}
