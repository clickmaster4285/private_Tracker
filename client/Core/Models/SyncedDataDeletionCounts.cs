namespace client.Core.Models;

/// <summary>
/// Per-table deleted-row counts returned by the 24h retention cleanup
/// (SyncService → SqliteLogStore.DeleteSyncedDataOlderThanAsync).
/// </summary>
public record SyncedDataDeletionCounts(
    int AppItems,
    int AppSessions,
    int InstalledApps,
    int InstalledPackages,
    int NetworkRows)
{
    public static readonly SyncedDataDeletionCounts Empty = new(0, 0, 0, 0, 0);

    public int Total => AppItems + AppSessions + InstalledApps + InstalledPackages + NetworkRows;
}
