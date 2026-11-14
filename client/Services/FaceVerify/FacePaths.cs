namespace client.Services.FaceVerify;

/// <summary>
/// LocalAppData layout for Windows face verify (models, samples, biometric.db, session handoff).
/// </summary>
public static class FacePaths
{
    /// <summary>Test-only override for isolated biometric.db paths.</summary>
    internal static string? RootDirectoryOverride { get; set; }

    public static string RootDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(RootDirectoryOverride))
                return RootDirectoryOverride;
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlphaAITracker");
            return Path.Combine(root, "face");
        }
    }

    public static string ModelsDirectory => Path.Combine(RootDirectory, "models");
    public static string SamplesDirectory => Path.Combine(RootDirectory, "samples");
    public static string StagingDirectory => Path.Combine(RootDirectory, "staging");
    public static string BiometricDbPath => Path.Combine(RootDirectory, "biometric.db");
    public static string SessionPath => Path.Combine(RootDirectory, "verify-session.json");
    public static string RequestEmbeddingsPath => Path.Combine(RootDirectory, "request_embeddings.json");

    /// <summary>InsightFace buffalo_l SCRFD detector.</summary>
    public static string DetPath => Path.Combine(ModelsDirectory, "det_10g.onnx");
    /// <summary>InsightFace buffalo_l recognition (w600k_r50).</summary>
    public static string RecPath => Path.Combine(ModelsDirectory, "w600k_r50.onnx");

    public static string ExpectedModelId => FaceModelIds.BuffaloL;

    public static string MutexName => $@"Global\{client.Core.AppInfo.AppMutex}-face-verify";

    /// <summary>Soft-lock process / window branding (not Windows Credential Provider).</summary>
    public const string LockerWindowTitle = "alpha-ai-tracker-face-verification";

    public static string SampleImagePath(string sampleId) =>
        Path.Combine(SamplesDirectory, sampleId.Trim() + ".jpg");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(SamplesDirectory);
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

public static class FaceModelIds
{
    public const string BuffaloL = "buffalo-l-w600k-r50";
    public const string PreprocessVersion = "face-preprocess-v2-buffalo-l";
}
