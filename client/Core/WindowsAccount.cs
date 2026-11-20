namespace client.Core;

/// <summary>
/// The Windows account the tracker runs under (SID + DOMAIN\user). Sent with the employee
/// login so the server binds the employee to this account on shift-account PCs
/// (Shift-Morning / Shift-Evening / Shift-Night). Empty on Linux / macOS.
/// </summary>
public static class WindowsAccount
{
    public static (string Sid, string User) Current()
    {
        if (!OperatingSystem.IsWindows())
            return ("", "");
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return (id.User?.Value ?? "", id.Name ?? "");
        }
        catch
        {
            return ("", "");
        }
    }
}
