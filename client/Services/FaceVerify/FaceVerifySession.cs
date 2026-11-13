using System.Text.Json;
using System.Text.Json.Serialization;

namespace client.Services.FaceVerify;

/// <summary>
/// Handoff between the tracker (after login) and <c>--face-verify</c> Avalonia subprocess.
/// </summary>
public sealed class FaceVerifySession
{
    public string WrittenAt { get; set; } = "";
    public string EmployeeId { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public string ApiBase { get; set; } = "";
    public string MachineId { get; set; } = "";
    public double MatchThreshold { get; set; } = 0.4;
    /// <summary>Optional override URL if API package route is unavailable.</summary>
    public string? AdaFaceUrl { get; set; }

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
