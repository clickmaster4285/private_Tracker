using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Production profile refuses fail-open clears (lockdown:false, pause, local
/// blocker switches). Emergency open requires a separately signed, expiring
/// recovery authorization under ProgramData and writes an audit event.
/// </summary>
internal static class ProductionFailClosed
{
    /// <summary>Ship default ON — lab can set ALPHA_DLP_PRODUCTION_PROFILE=false.</summary>
    public static bool Enabled =>
        !string.Equals(
            Environment.GetEnvironmentVariable("ALPHA_DLP_PRODUCTION_PROFILE") ?? "true",
            "false",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a valid recovery authorization is present and not expired.
    /// Shared secret: <c>ALPHA_DLP_RECOVERY_SECRET</c> (or JWT-style HMAC key in env).
    /// File: <c>%ProgramData%\&lt;mutex&gt;\dlp\recovery.auth.json</c>.
    /// </summary>
    public static bool TryConsumeRecoveryAuthorization(out string detail)
    {
        detail = "none";
        try
        {
            var path = Path.Combine(DlpDataPaths.DlpRoot, "recovery.auth.json");
            if (!File.Exists(path))
            {
                detail = "missing";
                return false;
            }

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var untilRaw = root.TryGetProperty("expiresAt", out var exp) ? exp.GetString() ?? "" : "";
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var sig = root.TryGetProperty("sig", out var s) ? s.GetString() ?? "" : "";
            if (!DateTimeOffset.TryParse(untilRaw, out var until) || until <= DateTimeOffset.UtcNow)
            {
                detail = "expired";
                return false;
            }

            var secret = Environment.GetEnvironmentVariable("ALPHA_DLP_RECOVERY_SECRET") ?? "";
            if (string.IsNullOrWhiteSpace(secret))
            {
                detail = "no_secret";
                return false;
            }

            var payload = until.UtcDateTime.ToString("o") + "|" + reason.Trim();
            var expected = Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)))
                .ToLowerInvariant();
            var supplied = sig.Trim();
            if (supplied.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                supplied = supplied["sha256:".Length..];
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(supplied.ToLowerInvariant())))
            {
                detail = "bad_sig";
                return false;
            }

            // Single-use: delete before returning so a second Clear cannot replay.
            try { File.Delete(path); }
            catch (Exception delEx)
            {
                detail = "consume_failed:" + delEx.GetType().Name;
                return false;
            }

            detail = $"ok;until={until:o};reason={Truncate(reason, 80)};consumed=1";
            return true;
        }
        catch (Exception ex)
        {
            detail = "error:" + ex.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// Returns true when a Clear/open is allowed. Production without recovery → false.
    /// </summary>
    public static bool AllowFailOpen(string reason, out string detail)
    {
        if (!Enabled)
        {
            detail = "lab_profile:" + reason;
            return true;
        }

        if (TryConsumeRecoveryAuthorization(out var recovery))
        {
            detail = "recovery:" + recovery + ";reason=" + reason;
            return true;
        }

        detail = "blocked_production:" + reason + ";recovery=" + recovery;
        return false;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max];
}
