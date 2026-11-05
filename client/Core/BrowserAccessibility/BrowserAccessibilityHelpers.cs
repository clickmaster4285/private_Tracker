namespace client.Core.BrowserAccessibility;

/// <summary>
/// Shared helpers for the accessibility-based browser journey readers.
/// </summary>
public static class BrowserAccessibilityHelpers
{
    /// <summary>
    /// Strip the browser suffix from a window title ("YouTube - Google Chrome" → "YouTube").
    /// Uses the dynamic app display name from the browser registry — no hardcoded list.
    /// </summary>
    public static string StripBrowserSuffix(string title, string? appDisplayName)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        if (string.IsNullOrEmpty(appDisplayName)) return title.Trim();

        var suffix1 = $" - {appDisplayName}";
        var suffix2 = $" — {appDisplayName}";

        var idx1 = title.IndexOf(suffix1, StringComparison.OrdinalIgnoreCase);
        if (idx1 > 0) return title[..idx1].Trim();

        var idx2 = title.IndexOf(suffix2, StringComparison.OrdinalIgnoreCase);
        if (idx2 > 0) return title[..idx2].Trim();

        return title.Trim();
    }

    /// <summary>Normalize the text read from a browser address bar into a full URL.</summary>
    public static string NormalizeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var t = raw.Trim().TrimStart('\u200b', '\ufeff', '"', ' ');
        if (t.Length == 0) return string.Empty;

        var low = t.ToLowerInvariant();
        if (low.StartsWith("http://", StringComparison.Ordinal) ||
            low.StartsWith("https://", StringComparison.Ordinal))
            return t;
        if (low.Contains("://", StringComparison.Ordinal))
            return t;

        if (t.Any(char.IsWhiteSpace)) return string.Empty;
        if (!t.Contains('.')) return string.Empty;
        return "https://" + t;
    }

    /// <summary>Extract the registrable-ish host from a URL.</summary>
    public static string ExtractDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                var host = uri.Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                    host = host[4..];
                return host;
            }
        }
        catch { }
        return string.Empty;
    }

    /// <summary>Heuristic incognito detection from window title text.</summary>
    public static bool TitleSuggestsIncognito(string title) =>
        title.Contains("incognito", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("inprivate", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("private browsing", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stable 31-bit int from a string (used for window_id on app_items).</summary>
    public static int StableInt32(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return System.BitConverter.ToInt32(hash, 0) & 0x7FFFFFFF;
    }
}
