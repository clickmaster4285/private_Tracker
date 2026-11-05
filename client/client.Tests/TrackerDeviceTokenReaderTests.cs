using client.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace client.Tests;

public class TrackerDeviceTokenReaderTests
{
    [Fact]
    public void Candidates_stay_under_the_current_user_and_include_both_db_names()
    {
        var local = Path.Combine(Path.GetTempPath(), "aat-local");
        var profile = Path.Combine(Path.GetTempPath(), "aat-profile");
        var paths = TrackerDeviceTokenReader.CandidatePaths(
            local, profile, Path.Combine(profile, "other", "alpha_tracker.db"));

        var folder = OperatingSystem.IsWindows() ? "AlphaAITracker" : "alpha-ai-tracker";
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert.Contains(paths, p => p.EndsWith(Path.Combine(folder, "data", "aat_verify.db"), cmp));
        Assert.Contains(paths, p => p.EndsWith(Path.Combine(folder, "data", "alpha_tracker.db"), cmp));
        Assert.Contains(paths, p => p.Contains(Path.Combine("aat-profile", "other"), StringComparison.OrdinalIgnoreCase)
            || p.Contains(Path.Combine("aat-profile", "other")));
    }

    [Fact]
    public void A_database_outside_the_user_profile_is_ignored()
    {
        var local = Path.Combine(Path.GetTempPath(), "aat-local-2");
        var profile = Path.Combine(Path.GetTempPath(), "aat-profile-2");
        var foreign = Path.Combine(Path.GetTempPath(), "someone-else", "alpha_tracker.db");
        var paths = TrackerDeviceTokenReader.CandidatePaths(local, profile, foreign);
        Assert.DoesNotContain(paths, p => p.Contains("someone-else", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Empty_token_is_not_chosen_and_a_newer_file_wins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aat-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var empty = Path.Combine(dir, "empty.db");
            var older = Path.Combine(dir, "older.db");
            var newer = Path.Combine(dir, "newer.db");
            WriteEmployee(empty, "");
            WriteEmployee(older, "token-old");
            WriteEmployee(newer, "token-new");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

            Assert.False(TrackerDeviceTokenReader.TryRead(empty, out _, out _, out var emptyError));
            Assert.Contains("token", emptyError, StringComparison.OrdinalIgnoreCase);

            Assert.True(TrackerDeviceTokenReader.TryPickNewest(
                new[] { empty, older, newer }, out var token, out _, out var source, out _));
            Assert.Equal("token-new", token);
            Assert.Equal(newer, source);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Missing_and_corrupt_databases_fail_without_throwing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aat-token-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var missing = Path.Combine(dir, "missing.db");
            var corrupt = Path.Combine(dir, "corrupt.db");
            File.WriteAllText(corrupt, "this is not sqlite");

            Assert.False(TrackerDeviceTokenReader.TryPickNewest(
                new[] { missing, corrupt }, out var token, out _, out _, out var diagnostic));
            Assert.Equal("", token);
            Assert.Contains("missing", diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(corrupt, diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void WriteEmployee(string path, string token)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE employee_info (employee_id TEXT, device_token TEXT);" +
            "INSERT INTO employee_info (employee_id, device_token) VALUES ('EMP-1', $t);";
        cmd.Parameters.AddWithValue("$t", token);
        cmd.ExecuteNonQuery();
    }
}
