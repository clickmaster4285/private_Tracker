namespace AlphaAITracker.Service;

/// <summary>
/// Flag file the Windows uninstaller checks. Written after a removal code is
/// verified so Inno Setup InitializeUninstall can allow ARP uninstall.
/// </summary>
internal static class TeardownGate
{
    public const string FileName = "teardown.ok";

    public static string PathIn(string dataDir) =>
        System.IO.Path.Combine(dataDir, FileName);

    public static void MarkAuthorized(string dataDir, string purpose)
    {
        Directory.CreateDirectory(dataDir);
        var path = PathIn(dataDir);
        File.WriteAllText(path,
            $"authorizedAt={DateTime.UtcNow:o}\npurpose={purpose}\n");
    }

    public static void Clear(string dataDir)
    {
        try
        {
            var path = PathIn(dataDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort */ }
    }
}
