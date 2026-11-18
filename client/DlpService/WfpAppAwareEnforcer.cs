namespace AlphaAITracker.Service;

/// <summary>
/// Phase 2 (optional): Windows Filtering Platform callout for per-app egress
/// (tracker vs browser). Not enabled by default — firewall DefaultOutbound=Block
/// remains the Phase 0/1 authority.
/// Set <c>ALPHA_DLP_WFP_APP_AWARE=true</c> only after a signed callout driver is
/// shipped; until then this reports parked and does nothing.
/// </summary>
internal static class WfpAppAwareEnforcer
{
    public sealed record Result(bool Changed, bool Applied, string Detail);

    public static bool Enabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("ALPHA_DLP_WFP_APP_AWARE") ?? "false",
            "true",
            StringComparison.OrdinalIgnoreCase);

    public static Result Apply()
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os");
        if (!Enabled)
            return new Result(false, false, "wfp_parked;ALPHA_DLP_WFP_APP_AWARE=false");
        // Placeholder: real callout registration lands with a signed driver package.
        return new Result(false, false, "wfp_driver_not_shipped");
    }

    public static Result Clear()
    {
        if (!Enabled)
            return new Result(false, false, "wfp_parked");
        return new Result(false, false, "wfp_clear_noop");
    }
}
