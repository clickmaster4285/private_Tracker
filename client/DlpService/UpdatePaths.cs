namespace AlphaAITracker.Service;

/// <summary>
/// Chrome-like updater staging under ProgramData (SYSTEM-writable), sibling of dlp/.
/// Flag files coordinate the detached apply helper and watchdog.
/// </summary>
internal static class UpdatePaths
{
    /// <summary>
    /// Stale <c>update_in_progress</c> older than this is cleared so a dead helper
    /// cannot permanently suppress the watchdog / silent updater.
    /// </summary>
    public static readonly TimeSpan InProgressTtl = TimeSpan.FromMinutes(20);

    /// <summary>
    /// After this age, a flag with no staged <c>*.exe</c>/<c>*.part</c> is treated as
    /// an orphaned arm (WriteInProgress without a running Inno).
    /// </summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(2);

    public static string RootDir => DlpDataPaths.UpdatesRoot;

    public static string LogPath => Path.Combine(RootDir, "update.log");
    public static string LastCheckPath => Path.Combine(RootDir, "last_check_at.txt");
    public static string InProgressFlag => Path.Combine(RootDir, "update_in_progress");
    /// <summary>Written by the apply helper when the installer fails (line 1 = version).</summary>
    public static string ApplyFailedPath => Path.Combine(RootDir, "apply_failed.txt");
    /// <summary>Legacy path — only cleared, never written (celebration UI removed).</summary>
    public static string CelebrateFlag => Path.Combine(RootDir, "celebrate.json");
    public static string ApplyScriptPath => Path.Combine(RootDir, "aat_update_apply.ps1");
    /// <summary>
    /// Written while a newer release is queued/downloading/applying so the tracker
    /// yields CPU/bandwidth (sync/live/screenshots) until the upgrade finishes.
    /// </summary>
    public static string PriorityFlag => Path.Combine(RootDir, "update_priority");

    public static bool IsUpdateInProgress()
    {
        try { return File.Exists(InProgressFlag); }
        catch { return false; }
    }

    /// <summary>
    /// True when an apply should still block the watchdog. Clears orphaned flags first.
    /// </summary>
    public static bool IsUpdateInProgressBlocking(TimeSpan? ttl = null)
    {
        if (!IsUpdateInProgress())
            return false;

        if (TryClearStaleInProgress(ttl ?? InProgressTtl, out _))
            return false;

        return IsUpdateInProgress();
    }

    /// <summary>
    /// Clears <c>update_in_progress</c> when TTL exceeded, or when the flag is older
    /// than <see cref="OrphanGrace"/> with no staged installer (helper never ran / died early).
    /// </summary>
    public static bool TryClearStaleInProgress(TimeSpan ttl, out string reason)
    {
        reason = "";
        try
        {
            if (!File.Exists(InProgressFlag))
                return false;

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(InProgressFlag);
            if (age > ttl)
            {
                ClearInProgress();
                ClearUpdatePriority();
                TryDeleteApplyScript();
                reason = $"ttl_exceeded:{age.TotalMinutes:0}m";
                return true;
            }

            var hasPayload = HasStagedInstallerPayload();
            if (!hasPayload && age > OrphanGrace)
            {
                ClearInProgress();
                ClearUpdatePriority();
                TryDeleteApplyScript();
                reason = "orphan_no_installer";
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static bool HasStagedInstallerPayload()
    {
        try
        {
            if (!Directory.Exists(RootDir)) return false;
            return Directory.EnumerateFiles(RootDir, "*.exe").Any()
                || Directory.EnumerateFiles(RootDir, "*.part").Any();
        }
        catch
        {
            return false;
        }
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

    public static void SetUpdatePriority(string version, string reason)
    {
        try
        {
            EnsureRoot();
            File.WriteAllText(
                PriorityFlag,
                $"{DateTime.UtcNow:o}\n{version.Trim()}\n{reason.Trim()}\n");
        }
        catch { /* ignore */ }
    }

    public static void ClearUpdatePriority()
    {
        try { if (File.Exists(PriorityFlag)) File.Delete(PriorityFlag); } catch { /* ignore */ }
    }

    public static bool IsUpdatePriorityActive()
    {
        try
        {
            if (IsUpdateInProgress()) return true;
            if (!File.Exists(PriorityFlag)) return false;
            // Stale priority (>2h) must not park the tracker forever.
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(PriorityFlag) > TimeSpan.FromHours(2))
            {
                ClearUpdatePriority();
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? ReadInProgressVersion()
    {
        try
        {
            if (!File.Exists(InProgressFlag)) return null;
            var lines = File.ReadAllLines(InProgressFlag);
            return lines.Length >= 2 ? lines[1].Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteApplyScript()
    {
        try { if (File.Exists(ApplyScriptPath)) File.Delete(ApplyScriptPath); } catch { /* ignore */ }
    }
}
