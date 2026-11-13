namespace client.Services.FaceVerify;

/// <summary>
/// LocalAppData layout for Windows face verify (models, biometric.db, session handoff).
/// </summary>
public static class FacePaths
{
    public static string RootDirectory
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlphaAITracker");
            return Path.Combine(root, "face");
        }
    }

    public static string ModelsDirectory => Path.Combine(RootDirectory, "models");
    public static string StagingDirectory => Path.Combine(RootDirectory, "staging");
    public static string BiometricDbPath => Path.Combine(RootDirectory, "biometric.db");
    public static string SessionPath => Path.Combine(RootDirectory, "verify-session.json");
    public static string YuNetPath => Path.Combine(ModelsDirectory, "face_detection_yunet_2023mar.onnx");
    public static string AdaFacePath => Path.Combine(ModelsDirectory, "adaface_ir50_webface4m.onnx");

    public static string MutexName => $@"Global\{client.Core.AppInfo.AppMutex}-face-verify";

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(StagingDirectory);
    }

    public static void WipeAll()
    {
        try
        {
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, recursive: true);
        }
        catch
        {
            // Best-effort: leave leftovers rather than fail account wipe.
        }
    }
}
