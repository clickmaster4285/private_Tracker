using System.Text.Json.Serialization;

namespace AlphaAITracker.Service;

/// <summary>Minimal wire DTOs for <c>GET /api/v1/releases</c> (camelCase).</summary>
internal sealed class BridgeReleasesPayload
{
    [JsonPropertyName("latestStable")] public string LatestStable { get; set; } = "";
    [JsonPropertyName("releases")] public List<BridgeRelease> Releases { get; set; } = new();
}

internal sealed class BridgeRelease
{
    [JsonPropertyName("tagName")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = "";
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("assets")] public List<BridgeReleaseAsset> Assets { get; set; } = new();
}

internal sealed class BridgeReleaseAsset
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

internal static class UpdateVersion
{
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            tag.Trim(), @"^[vV]?(\d+)\.(\d+)\.(\d+)");
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}" : null;
    }

    public static int Compare(string a, string b)
    {
        var (am, an, ap) = Parse(a);
        var (bm, bn, bp) = Parse(b);
        if (am != bm) return am.CompareTo(bm);
        if (an != bn) return an.CompareTo(bn);
        return ap.CompareTo(bp);
    }

    private static (int, int, int) Parse(string v)
    {
        var n = Normalize(v);
        if (n is null) return (0, 0, 0);
        var parts = n.Split('.');
        return (
            int.TryParse(parts[0], out var ma) ? ma : 0,
            int.TryParse(parts[1], out var mi) ? mi : 0,
            int.TryParse(parts[2], out var pa) ? pa : 0);
    }
}
