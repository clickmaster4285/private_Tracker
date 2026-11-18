namespace AlphaAITracker.Service;

/// <summary>
/// Normalizes <c>ALPHA_SERVER_URL</c> for DeviceAuth / policy pull / lockdown carve-out.
/// Preserves <c>https://</c> (nginx TLS) and <c>http://</c>; does not silently downgrade HTTPS.
/// </summary>
internal static class ServerUrlNormalizer
{
    /// <summary>
    /// Returns a trimmed, trailing-slash-stripped http(s) URL, or empty when blank /
    /// unsupported scheme. Bare host → <c>https://</c> (nginx fleet default).
    /// </summary>
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(s))
            return "";

        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "https://" + s["https://".Length..].TrimEnd('/');

        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "http://" + s["http://".Length..].TrimEnd('/');

        if (s.Contains("://", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[DlpService] Rejected unsupported ALPHA_SERVER_URL scheme: {s}");
            return "";
        }

        // Bare host:port → https://host:port (nginx TLS front).
        return "https://" + s;
    }

    /// <summary>
    /// True when a stored value must be healed (blank / non-http(s) scheme).
    /// HTTPS is valid and must not be rewritten to HTTP.
    /// </summary>
    public static bool NeedsRewrite(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return true;
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return false;
        // Bare host or unsupported scheme — normalize / heal.
        return true;
    }
}
