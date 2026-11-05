using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AlphaAITracker.Service;

/// <summary>
/// Linux IP block via nftables inet table/set (CDN/apex pin parity for Windows
/// AlphaAI-DLP-* firewall rules). Best-effort — hosts DNS sink remains primary.
/// </summary>
internal static class LinuxNftEnforcer
{
    private static string TableName => $"aat_{ServiceIdentity.PackageName.Replace('-', '_')}";
    private static string SetName => "blocked_v4";
    private static string SetName6 => "blocked_v6";

    public sealed record Result(bool Changed, string Detail);

    public static Result EnsureTableAndApply(IReadOnlyCollection<string> ipv4, IReadOnlyCollection<string> ipv6)
    {
        if (!OperatingSystem.IsLinux())
            return new Result(false, "not_linux");
        if (!HasNft())
            return new Result(false, "nft_missing");

        try
        {
            EnsureBase();
            FlushSet(SetName);
            FlushSet(SetName6);
            var added = 0;
            foreach (var ip in ipv4)
            {
                if (AddElement(SetName, ip)) added++;
            }
            foreach (var ip in ipv6)
            {
                if (AddElement(SetName6, ip)) added++;
            }
            return new Result(true, $"nft_applied v4={ipv4.Count} v6={ipv6.Count} added={added}");
        }
        catch (Exception ex)
        {
            return new Result(false, "nft_error:" + ex.Message);
        }
    }

    public static Result Clear()
    {
        if (!OperatingSystem.IsLinux())
            return new Result(false, "not_linux");
        if (!HasNft())
            return new Result(false, "nft_missing");

        try
        {
            // Delete whole table — removes sets + drop rules.
            RunNft($"delete table inet {TableName}");
            return new Result(true, "nft_table_deleted");
        }
        catch
        {
            return new Result(false, "nft_clear_best_effort");
        }
    }

    public static Result PinIps(IEnumerable<string> ips)
    {
        if (!OperatingSystem.IsLinux() || !HasNft())
            return new Result(false, "nft_unavailable");

        try
        {
            EnsureBase();
            var n = 0;
            foreach (var raw in ips)
            {
                if (!IPAddress.TryParse(raw, out var addr)) continue;
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    if (AddElement(SetName, addr.ToString())) n++;
                }
                else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    if (AddElement(SetName6, addr.ToString())) n++;
                }
            }
            return new Result(n > 0, $"nft_pin_added={n}");
        }
        catch (Exception ex)
        {
            return new Result(false, "nft_pin_error:" + ex.Message);
        }
    }

    public static bool HasOwnedArtifacts()
    {
        if (!OperatingSystem.IsLinux() || !HasNft()) return false;
        var (code, stdout, _) = RunNft($"list table inet {TableName}");
        return code == 0 && !string.IsNullOrWhiteSpace(stdout);
    }

    private static void EnsureBase()
    {
        RunNft($"add table inet {TableName}");
        RunNft($"add set inet {TableName} {SetName} '{{ type ipv4_addr; flags interval; }}'");
        RunNft($"add set inet {TableName} {SetName6} '{{ type ipv6_addr; flags interval; }}'");
        RunNft($"add chain inet {TableName} output '{{ type filter hook output priority 0; policy accept; }}'");
        RunNft($"add rule inet {TableName} output ip daddr @{SetName} drop");
        RunNft($"add rule inet {TableName} output ip6 daddr @{SetName6} drop");
    }

    private static void FlushSet(string set)
    {
        RunNft($"flush set inet {TableName} {set}");
    }

    private static bool AddElement(string set, string ip)
    {
        var (code, _, _) = RunNft($"add element inet {TableName} {set} '{{ {ip} }}'");
        return code == 0;
    }

    private static bool HasNft()
    {
        try
        {
            var (code, _, _) = RunNft("--version");
            return code == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (int Code, string Stdout, string Stderr) RunNft(string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "nft",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p == null) return (-1, "", "start_failed");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(10_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return (-1, stdout.ToString(), "timeout");
        }
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
