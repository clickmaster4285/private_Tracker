using System.Text.Json;
using System.Text.Json.Serialization;

namespace client.Services.FaceVerify;

/// <summary>
/// Handoff between the tracker and the WinForms soft-locker (<c>--face-verify</c>).
/// Locker session must not carry device tokens (zero-network soft-lock).
/// </summary>
public sealed class FaceVerifySession
{
    public string WrittenAt { get; set; } = "";
    public string EmployeeId { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public string ApiBase { get; set; } = "";
    public string MachineId { get; set; } = "";
    public double MatchThreshold { get; set; } = 0.85;
    /// <summary>boot | unlock | login — recorded on verify events.</summary>
    public string Trigger { get; set; } = "boot";
    /// <summary>Optional override URL for recognition ONNX if API package route is unavailable.</summary>
    public string? RecModelUrl { get; set; }
    /// <summary>Legacy alias for <see cref="RecModelUrl"/> (JSON compat).</summary>
    [JsonPropertyName("adaFaceUrl")]
    public string? AdaFaceUrl
    {
        get => RecModelUrl;
        set { if (!string.IsNullOrWhiteSpace(value)) RecModelUrl = value; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(FaceVerifySession session)
    {
        FacePaths.EnsureDirectories();
        session.WrittenAt = DateTime.UtcNow.ToString("O");
        var json = JsonSerializer.Serialize(session, JsonOptions);
        var tmp = FacePaths.SessionPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FacePaths.SessionPath, overwrite: true);
    }

    public static FaceVerifySession? TryRead()
    {
        try
        {
            if (!File.Exists(FacePaths.SessionPath)) return null;
            var json = File.ReadAllText(FacePaths.SessionPath);
            return JsonSerializer.Deserialize<FaceVerifySession>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsVerifyProcessRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(FacePaths.MutexName, out var existing))
            {
                existing.Dispose();
                return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }
}
