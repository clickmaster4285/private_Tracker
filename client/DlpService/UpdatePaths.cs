namespace AlphaAITracker.Service;

/// <summary>
/// Chrome-like updater staging under ProgramData (SYSTEM-writable), sibling of dlp/.
/// Flag files coordinate the detached apply helper and watchdog.
/// </summary>
internal static class UpdatePaths
{
    public static string RootDir
    {
        get
        {
            return DlpDataPaths.UpdatesRoot;
        }
    }

    public static string LogPath => Path.Combine(RootDir, "update.log");
    public static string LastCheckPath => Path.Combine(RootDir, "last_check_at.txt");
    public static string InProgressFlag => Path.Combine(RootDir, "update_in_progress");
    /// <summary>Legacy path — only cleared, never written (celebration UI removed).</summary>
    public static string CelebrateFlag => Path.Combine(RootDir, "celebrate.json");
    public static string ApplyScriptPath => Path.Combine(RootDir, "aat_update_apply.ps1");

    public static bool IsUpdateInProgress()
    {
        try { return File.Exists(InProgressFlag); }
        catch { return false; }
    }

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(RootDir);
    }

    public static void WriteInProgress(string version)
    {
        EnsureRoot();
        File.WriteAllText(InProgressFlag, $"{DateTime.UtcNow:o}\n{version}\n");
    }

    public static void ClearInProgress()
    {
        try { if (File.Exists(InProgressFlag)) File.Delete(InProgressFlag); } catch { /* ignore */ }
    }

    public static void ClearCelebrate()
    {
        try { if (File.Exists(CelebrateFlag)) File.Delete(CelebrateFlag); } catch { /* ignore */ }
    }
}
