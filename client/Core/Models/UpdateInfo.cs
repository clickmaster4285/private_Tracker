using System.Text.Json.Serialization;

namespace client.Core.Models;

/// <summary>
/// A candidate software update from the Alpha AI Tracker public releases bridge
/// (<c>GET /api/v1/releases</c>) — latest stable version, notes, and the installer
/// asset for THIS machine. Produced by <see cref="Services.AppUpdateService"/>.
/// </summary>
public class UpdateInfo
{
    /// <summary>Normalized semantic version, e.g. "1.1.0" (leading 'v' stripped).</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Raw release tag, e.g. "v1.1.0".</summary>
    public string TagName { get; init; } = string.Empty;

    /// <summary>Markdown release notes body.</summary>
    public string ReleaseNotes { get; init; } = string.Empty;

    /// <summary>File name of the matching installer asset, e.g. "alpha-ai-tracker_1.1.0_amd64.deb".</summary>
    public string AssetName { get; init; } = string.Empty;

    /// <summary>
    /// Download URL for the asset — always the Alpha API server
    /// (<c>{ALPHA_SERVER_URL}/api/v1/downloads/{AssetId}</c>).
    /// </summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>Server asset id (stable per installer filename) used by the download endpoint.</summary>
    public long AssetId { get; init; }

    /// <summary>Published byte length from the release list. Zero when the list omitted it.</summary>
    public long AssetSize { get; init; }

    /// <summary>Publish timestamp (UTC).</summary>
    public DateTime? PublishedAt { get; init; }

    /// <summary>
    /// Numeric three-part comparison of two version strings. Returns &lt; 0 when a is
    /// older, 0 when equal, &gt; 0 when a is newer. Non-numeric suffixes ("-beta",
    /// "+sha") are ignored for comparison, so "1.1.0-beta" == "1.1.0".
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var (am, an, ap) = Parse(a);
        var (bm, bn, bp) = Parse(b);
        if (am != bm) return am.CompareTo(bm);
        if (an != bn) return an.CompareTo(bn);
        return ap.CompareTo(bp);
    }

    /// <summary>
    /// Normalizes a release tag into a plain "X.Y.Z" version string, or null when the
    /// tag carries no recognizable three-part version (e.g. "nightly").
    /// </summary>
    public static string? NormalizeVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            tag.Trim(), @"^[vV]?(\d+)\.(\d+)\.(\d+)");
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}" : null;
    }

    private static (int Major, int Minor, int Patch) Parse(string v)
    {
        var n = NormalizeVersion(v);
        if (n is null) return (0, 0, 0);
        var parts = n.Split('.');
        return (
            int.TryParse(parts[0], out var ma) ? ma : 0,
            int.TryParse(parts[1], out var mi) ? mi : 0,
            int.TryParse(parts[2], out var pa) ? pa : 0);
    }
}

/// <summary>Wire shape for <c>GET /api/v1/releases</c> (camelCase JSON).</summary>
public class BridgeReleasesPayload
{
    [JsonPropertyName("latestStable")] public string LatestStable { get; set; } = string.Empty;
    [JsonPropertyName("releases")] public List<BridgeRelease> Releases { get; set; } = new();
}

public class BridgeRelease
{
    [JsonPropertyName("tagName")] public string TagName { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = string.Empty;
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("assets")] public List<BridgeReleaseAsset> Assets { get; set; } = new();
}

public class BridgeReleaseAsset
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("platform")] public string Platform { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
}
