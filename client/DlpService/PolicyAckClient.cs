using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>Best-effort DeviceAuth ack of downloaded / installed / verified policy stages.</summary>
internal static class PolicyAckClient
{
    public static async Task TryAckAsync(
        DlpConfig cfg, long version, string? hash, string stage, string detail, CancellationToken ct)
    {
        if (version <= 0 ||
            string.IsNullOrWhiteSpace(cfg.ServerUrl) ||
            string.IsNullOrWhiteSpace(cfg.DeviceToken))
            return;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"{cfg.ServerUrl.TrimEnd('/')}/api/v1/dlp/policy/ack");
            req.Headers.Authorization = new AuthenticationHeaderValue("Device", cfg.DeviceToken);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    policyVersion = version,
                    hash = hash ?? "",
                    stage,
                    detail,
                }),
                Encoding.UTF8,
                "application/json");
            using var resp = await client.SendAsync(req, ct);
            _ = resp;
        }
        catch
        {
            /* offline / 401 — keep enforcing; ack is telemetry */
        }
    }
}
