using System.Text.Json;

namespace client.Services.FaceVerify;

/// <summary>
/// Soft-locker → tracker signal: re-download embeddings (locker is zero-network).
/// </summary>
public static class FaceEmbeddingRequest
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Write(string employeeId, string reason)
    {
        try
        {
            FacePaths.EnsureDirectories();
            var payload = new
            {
                employeeId,
                reason,
                requestedAt = DateTime.UtcNow.ToString("O"),
            };
            var tmp = FacePaths.RequestEmbeddingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOpts));
            File.Move(tmp, FacePaths.RequestEmbeddingsPath, overwrite: true);
        }
        catch { /* ignore */ }
    }

    public static bool TryConsume(out string employeeId, out string reason)
    {
        employeeId = "";
        reason = "";
        try
        {
            if (!File.Exists(FacePaths.RequestEmbeddingsPath))
                return false;
            var json = File.ReadAllText(FacePaths.RequestEmbeddingsPath);
            using var doc = JsonDocument.Parse(json);
            employeeId = doc.RootElement.TryGetProperty("employeeId", out var e)
                ? e.GetString() ?? ""
                : "";
            reason = doc.RootElement.TryGetProperty("reason", out var r)
                ? r.GetString() ?? ""
                : "";
            try { File.Delete(FacePaths.RequestEmbeddingsPath); } catch { /* ignore */ }
            return !string.IsNullOrWhiteSpace(employeeId);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPending()
    {
        try { return File.Exists(FacePaths.RequestEmbeddingsPath); }
        catch { return false; }
    }
}
