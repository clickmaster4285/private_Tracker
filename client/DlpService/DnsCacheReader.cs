using System.Runtime.InteropServices;

namespace AlphaAITracker.Service;

/// <summary>
/// Reads the Windows DNS client cache (dnsapi DnsGetCacheDataTable).
/// Used to discover live CDN hostnames (e.g. rr*---sn-*.googlevideo.com) that hosts
/// cannot wildcard-block, so we can pin their IPs into the firewall without a browser restart.
/// </summary>
internal static class DnsCacheReader
{
    public static IReadOnlyList<string> ListHostNames()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!DnsGetCacheDataTable(out var entryPtr) || entryPtr == IntPtr.Zero)
                return Array.Empty<string>();

            var ptr = entryPtr;
            while (ptr != IntPtr.Zero)
            {
                var entry = Marshal.PtrToStructure<DnsCacheEntry>(ptr);
                if (entry.pszName != IntPtr.Zero)
                {
                    var name = Marshal.PtrToStringUni(entry.pszName);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        name = name.Trim().TrimEnd('.').ToLowerInvariant();
                        if (name.Length > 0 && name.Contains('.'))
                            names.Add(name);
                    }
                }
                ptr = entry.pNext;
            }
        }
        catch
        {
            return Array.Empty<string>();
        }

        return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsCacheEntry
    {
        public IntPtr pNext;
        public IntPtr pszName;
        public ushort wType;
        public ushort wDataLength;
        public uint flags;
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTable", CharSet = CharSet.Unicode)]
    private static extern bool DnsGetCacheDataTable(out IntPtr pEntryList);
}
