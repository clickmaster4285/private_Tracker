using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace AlphaAITracker.Service;

/// <summary>
/// Named-pipe caller checks for mutating tracking-control ops (P1).
/// Read-only ops (Health, GetTrackingState, GetUpdateStatus) stay open to AuthenticatedUsers.
/// </summary>
internal static class PipeCallerGuard
{
    /// <summary>
    /// Returns null when the caller looks like an interactive user; otherwise an error code.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? RejectIfNotInteractiveUser(NamedPipeServerStream server)
    {
        try
        {
            string? sidValue = null;
            var isSystem = false;
            var isService = false;

            server.RunAsClient(() =>
            {
                using var id = WindowsIdentity.GetCurrent();
                sidValue = id.User?.Value;
                isSystem = id.IsSystem;
                // LocalService / NetworkService well-known SIDs.
                var user = id.User;
                if (user is not null)
                {
                    isService =
                        user.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
                        user.IsWellKnown(WellKnownSidType.NetworkServiceSid) ||
                        user.IsWellKnown(WellKnownSidType.LocalSystemSid);
                }
            });

            if (isSystem || isService)
                return "caller_not_interactive";

            if (string.IsNullOrEmpty(sidValue))
                return "caller_unknown";

            return null;
        }
        catch (Exception)
        {
            return "caller_check_failed";
        }
    }
}
