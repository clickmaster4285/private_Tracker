using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Signed-shape local catalog of approved applications for the managed launcher UX.
/// Direct / CLI / service / scheduled launches remain protected by WDAC/AppLocker and
/// show the native Windows denial — this catalog only drives branded shortcut launches.
/// </summary>
internal static class ManagedLauncherCatalog
{
    private static string CatalogPath =>
        Path.Combine(DlpDataPaths.DlpRoot, "managed-launcher.json");

    public sealed record Entry(string Id, string Name, string Path, string Note);

    public static void Publish(IReadOnlyList<AllowAppEntry> apps, long policyVersion, string policyHash)
    {
        try
        {
            Directory.CreateDirectory(DlpDataPaths.DlpRoot);
            var entries = new List<Entry>();
            var i = 0;
            foreach (var a in apps)
            {
                var path = (a.Path ?? "").Trim();
                if (path.Length == 0)
                    continue;
                if (AppAllowEnforcer.IsUnsafeUserWritableAllow(path))
                    continue;
                var name = !string.IsNullOrWhiteSpace(a.Product)
                    ? a.Product.Trim()
                    : Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(name))
                    name = "App " + (++i);
                var id = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16]
                    .ToLowerInvariant();
                entries.Add(new Entry(id, name, path, a.Publisher ?? ""));
            }

            var payload = new
            {
                policyVersion,
                policyHash = policyHash ?? "",
                updatedAt = DateTimeOffset.UtcNow.ToString("o"),
                apps = entries,
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            var sig = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            var wrapped = JsonSerializer.Serialize(new
            {
                sha256 = sig,
                catalog = payload,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CatalogPath, wrapped);
        }
        catch
        {
            /* launcher is UX-only; AppLocker/WDAC still enforce */
        }
    }

    public static IReadOnlyList<Entry> LoadApproved()
    {
        try
        {
            if (!File.Exists(CatalogPath))
                return Array.Empty<Entry>();
            using var doc = JsonDocument.Parse(File.ReadAllText(CatalogPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("catalog", out var catalog))
                return Array.Empty<Entry>();
            if (!catalog.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
                return Array.Empty<Entry>();
            var list = new List<Entry>();
            foreach (var el in apps.EnumerateArray())
            {
                var id = el.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (id.Length == 0 && el.TryGetProperty("id", out var id2))
                    id = id2.GetString() ?? "";
                var name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" :
                    el.TryGetProperty("name", out var n2) ? n2.GetString() ?? "" : "";
                var path = el.TryGetProperty("Path", out var p) ? p.GetString() ?? "" :
                    el.TryGetProperty("path", out var p2) ? p2.GetString() ?? "" : "";
                var note = el.TryGetProperty("Note", out var noteEl) ? noteEl.GetString() ?? "" :
                    el.TryGetProperty("note", out var note2) ? note2.GetString() ?? "" : "";
                if (path.Length == 0 || AppAllowEnforcer.IsUnsafeUserWritableAllow(path))
                    continue;
                list.Add(new Entry(id, name, path, note));
            }
            return list;
        }
        catch
        {
            return Array.Empty<Entry>();
        }
    }
}
