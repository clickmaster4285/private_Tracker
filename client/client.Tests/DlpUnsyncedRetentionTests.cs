using AlphaAITracker.Service;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace client.Tests;

public class DlpUnsyncedRetentionTests
{
    [Theory]
    [InlineData("{\"acceptedIds\":[\"a\"]}", true, new[] { "a" })]
    [InlineData("{\"acceptedIds\":[]}", true, new string[0])]
    [InlineData("{\"rejectedIds\":[\"a\"]}", false, new string[0])]
    [InlineData("not-json", false, new string[0])]
    public void Accepted_ids_are_authoritative(string body, bool selected, string[] ids)
    {
        var ok = AlertAcceptDecision.TrySelectIds(body, out var chosen);
        Assert.Equal(selected, ok);
        Assert.Equal(ids, chosen);
    }

    [Fact]
    public void Unsynced_age_and_cap_deletes_are_gone()
    {
        var flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(PolicyStore);
        Assert.Null(type.GetMethod("PruneExpiredUnsyncedAsync", flags));
        Assert.Null(type.GetMethod("TrimUnsyncedOverCapAsync", flags));
        Assert.Null(type.GetMethod("TrimUnsyncedOlderThanUnlockedAsync", flags));
    }

    [Theory]
    [InlineData(1_000, 600L * 1024 * 1024, false)]
    [InlineData(300L * 1024 * 1024, long.MaxValue, true)]
    [InlineData(1_000, 100L * 1024 * 1024, true)]
    [InlineData(1_000, -1, false)]
    public void Disk_pressure_does_not_treat_unknown_free_space_as_full(long db, long free, bool pressure)
    {
        Assert.Equal(pressure, PolicyStore.IsDiskPressure(db, free));
    }

    [Fact]
    public async Task Sweep_keeps_old_unsynced_alerts_and_deletes_only_accepted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aat-dlp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfg = new DlpConfig { DataDir = dir, Enabled = true };
            var store = new PolicyStore(cfg, NullLogger<PolicyStore>.Instance);
            await store.InitializeAsync(CancellationToken.None);

            await store.EnqueueAlertAsync(
                "old", null, 1, "usb", "medium", "blocked",
                DateTimeOffset.UtcNow.AddDays(-30), "{\"domain\":\"old.example\"}", CancellationToken.None);
            await store.EnqueueAlertAsync(
                "fresh", null, 1, "usb", "medium", "blocked",
                DateTimeOffset.UtcNow, "{\"domain\":\"fresh.example\"}", CancellationToken.None);
            await store.EnqueueAlertAsync(
                "accepted", null, 1, "usb", "medium", "blocked",
                DateTimeOffset.UtcNow.AddDays(-40), "{\"domain\":\"gone.example\"}", CancellationToken.None);

            await using (var conn = new SqliteConnection($"Data Source={cfg.DbPath}"))
            {
                await conn.OpenAsync();
                await using var mark = conn.CreateCommand();
                mark.CommandText = "UPDATE dlp_alerts_local SET is_synced = 1 WHERE id = 'accepted'";
                await mark.ExecuteNonQueryAsync();
            }

            await store.SweepAsync(CancellationToken.None);

            var ids = await ReadIdsAsync(cfg.DbPath);
            Assert.Contains("old", ids);
            Assert.Contains("fresh", ids);
            Assert.DoesNotContain("accepted", ids);
            Assert.Equal(0, await CountEvidenceAsync(cfg.DbPath, "accepted"));
            Assert.Equal(1, await CountEvidenceAsync(cfg.DbPath, "old"));

            await store.MarkAlertsSyncedAsync(new[] { "fresh" }, CancellationToken.None);
            ids = await ReadIdsAsync(cfg.DbPath);
            Assert.Contains("old", ids);
            Assert.DoesNotContain("fresh", ids);
            Assert.Equal(0, await CountEvidenceAsync(cfg.DbPath, "fresh"));
            Assert.Equal(1, await CountEvidenceAsync(cfg.DbPath, "old"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* file lock on Windows */ }
        }
    }

    private static async Task<List<string>> ReadIdsAsync(string dbPath)
    {
        var ids = new List<string>();
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM dlp_alerts_local ORDER BY id";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task<int> CountEvidenceAsync(string dbPath, string alertId)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dlp_alert_evidence WHERE alert_id = $id";
        cmd.Parameters.AddWithValue("$id", alertId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
