using Microsoft.Data.Sqlite;

namespace client.Services.FaceVerify;

public sealed class LocalFaceProfile
{
    public string EmployeeId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string Status { get; set; } = "";
    public int ProfileVersion { get; set; }
    public int EnrollmentVersion { get; set; }
    public string ModelId { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public string PreprocessVersion { get; set; } = "face-preprocess-v1";
    public int EmbeddingDims { get; set; } = 512;
    public int EmbeddingCount { get; set; }
}

public sealed class LocalFaceTemplate
{
    public string Id { get; set; } = "";
    public string EmployeeId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string? SampleId { get; set; }
    public string? PoseId { get; set; }
    public string ModelId { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public string PreprocessVersion { get; set; } = "face-preprocess-v1";
    public int EmbeddingDims { get; set; } = 512;
    public double? QualityScore { get; set; }
    public float[] Vector { get; set; } = Array.Empty<float>();
}

public sealed class LocalFaceSample
{
    public string SampleId { get; set; } = "";
    public string EmployeeId { get; set; } = "";
    public string PoseId { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public long ByteSize { get; set; }
    public string LocalPath { get; set; } = "";
}

public sealed class LocalFaceVerifyEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EmployeeId { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string Mode { get; set; } = "simple";
    public string Trigger { get; set; } = "";
    public string PoseId { get; set; } = "";
    public string Result { get; set; } = "";
    public double? Score { get; set; }
    public double? Threshold { get; set; }
    public string Detail { get; set; } = "";
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");
    public bool IsSynced { get; set; }
}

/// <summary>
/// Dedicated SQLite under face/biometric.db so the --face-verify subprocess
/// does not contend with the tracker's alpha_tracker.db writers.
/// </summary>
public sealed class FaceBiometricStore : IDisposable
{
    private SqliteConnection? _conn;

    public async Task InitializeAsync(CancellationToken ct)
    {
        FacePaths.EnsureDirectories();
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = FacePaths.BiometricDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        _conn = new SqliteConnection(cs);
        await _conn.OpenAsync(ct);
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS local_face_profiles (
                employee_id         TEXT PRIMARY KEY,
                profile_id          TEXT NOT NULL,
                status              TEXT NOT NULL DEFAULT '',
                profile_version     INTEGER NOT NULL DEFAULT 0,
                enrollment_version  INTEGER NOT NULL DEFAULT 0,
                model_id            TEXT NOT NULL DEFAULT '',
                model_version       TEXT NOT NULL DEFAULT '',
                preprocess_version  TEXT NOT NULL DEFAULT 'face-preprocess-v1',
                embedding_dims      INTEGER NOT NULL DEFAULT 512,
                embedding_count     INTEGER NOT NULL DEFAULT 0,
                updated_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
            );
            CREATE TABLE IF NOT EXISTS local_face_templates (
                id                  TEXT PRIMARY KEY,
                employee_id         TEXT NOT NULL,
                profile_id          TEXT NOT NULL,
                sample_id           TEXT,
                pose_id             TEXT,
                model_id            TEXT NOT NULL DEFAULT '',
                model_version       TEXT NOT NULL DEFAULT '',
                preprocess_version  TEXT NOT NULL DEFAULT 'face-preprocess-v1',
                embedding_dims      INTEGER NOT NULL DEFAULT 512,
                quality_score       REAL,
                vector_blob         BLOB NOT NULL,
                created_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
            );
            CREATE INDEX IF NOT EXISTS idx_local_face_templates_employee
                ON local_face_templates(employee_id);
            CREATE TABLE IF NOT EXISTS local_face_samples (
                sample_id           TEXT PRIMARY KEY,
                employee_id         TEXT NOT NULL,
                pose_id             TEXT NOT NULL DEFAULT '',
                content_hash        TEXT NOT NULL DEFAULT '',
                byte_size           INTEGER NOT NULL DEFAULT 0,
                local_path          TEXT NOT NULL DEFAULT '',
                updated_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
            );
            CREATE INDEX IF NOT EXISTS idx_local_face_samples_employee
                ON local_face_samples(employee_id);
            CREATE TABLE IF NOT EXISTS local_biometric_sync_state (
                employee_id         TEXT PRIMARY KEY,
                last_sync_at        TEXT,
                last_ack_at         TEXT,
                last_verify_at      TEXT,
                last_verify_result  TEXT,
                first_verify_completed_at TEXT,
                profile_version     INTEGER NOT NULL DEFAULT 0,
                model_version       TEXT NOT NULL DEFAULT '',
                failure_count       INTEGER NOT NULL DEFAULT 0,
                updated_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
            );
            CREATE TABLE IF NOT EXISTS local_face_verify_events (
                id                  TEXT PRIMARY KEY,
                employee_id         TEXT NOT NULL,
                machine_id          TEXT NOT NULL DEFAULT '',
                mode                TEXT NOT NULL DEFAULT 'simple',
                trigger_name        TEXT NOT NULL DEFAULT '',
                pose_id             TEXT NOT NULL DEFAULT '',
                result              TEXT NOT NULL,
                score               REAL,
                threshold           REAL,
                detail              TEXT NOT NULL DEFAULT '',
                created_at          TEXT NOT NULL,
                is_synced           INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_local_face_verify_events_unsynced
                ON local_face_verify_events(is_synced, created_at);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        await EnsureColumnAsync("local_biometric_sync_state", "first_verify_completed_at", "TEXT", ct);
        await EnsureColumnAsync("local_biometric_sync_state", "first_local_test_label", "TEXT", ct);
        await EnsureColumnAsync("local_biometric_sync_state", "first_local_integrity", "TEXT", ct);
    }

    public const string FirstLocalTestLabelDone = "first_local_test_done";

    private async Task EnsureColumnAsync(string table, string column, string type, CancellationToken ct)
    {
        if (_conn == null) return;
        await using var check = _conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        await using var r = await check.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        await using var alter = _conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        await alter.ExecuteNonQueryAsync(ct);
    }

    public async Task ReplaceProfileAsync(LocalFaceProfile profile, IReadOnlyList<LocalFaceTemplate> templates, CancellationToken ct)
    {
        if (_conn == null) throw new InvalidOperationException("not initialized");
        await using var tx = await _conn.BeginTransactionAsync(ct);
        await using (var delT = _conn.CreateCommand())
        {
            delT.Transaction = (SqliteTransaction)tx;
            delT.CommandText = "DELETE FROM local_face_templates WHERE employee_id = $e";
            delT.Parameters.AddWithValue("$e", profile.EmployeeId);
            await delT.ExecuteNonQueryAsync(ct);
        }
        await using (var up = _conn.CreateCommand())
        {
            up.Transaction = (SqliteTransaction)tx;
            up.CommandText = """
                INSERT INTO local_face_profiles
                    (employee_id, profile_id, status, profile_version, enrollment_version,
                     model_id, model_version, preprocess_version, embedding_dims, embedding_count, updated_at)
                VALUES ($e, $p, $s, $pv, $ev, $mid, $mv, $pp, $d, $c, strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
                ON CONFLICT(employee_id) DO UPDATE SET
                    profile_id = excluded.profile_id,
                    status = excluded.status,
                    profile_version = excluded.profile_version,
                    enrollment_version = excluded.enrollment_version,
                    model_id = excluded.model_id,
                    model_version = excluded.model_version,
                    preprocess_version = excluded.preprocess_version,
                    embedding_dims = excluded.embedding_dims,
                    embedding_count = excluded.embedding_count,
                    updated_at = excluded.updated_at
                """;
            up.Parameters.AddWithValue("$e", profile.EmployeeId);
            up.Parameters.AddWithValue("$p", profile.ProfileId);
            up.Parameters.AddWithValue("$s", profile.Status);
            up.Parameters.AddWithValue("$pv", profile.ProfileVersion);
            up.Parameters.AddWithValue("$ev", profile.EnrollmentVersion);
            up.Parameters.AddWithValue("$mid", profile.ModelId);
            up.Parameters.AddWithValue("$mv", profile.ModelVersion);
            up.Parameters.AddWithValue("$pp", profile.PreprocessVersion);
            up.Parameters.AddWithValue("$d", profile.EmbeddingDims);
            up.Parameters.AddWithValue("$c", templates.Count);
            await up.ExecuteNonQueryAsync(ct);
        }
        foreach (var t in templates)
        {
            await using var ins = _conn.CreateCommand();
            ins.Transaction = (SqliteTransaction)tx;
            ins.CommandText = """
                INSERT INTO local_face_templates
                    (id, employee_id, profile_id, sample_id, pose_id, model_id, model_version,
                     preprocess_version, embedding_dims, quality_score, vector_blob)
                VALUES ($id, $e, $p, $sid, $pose, $mid, $mv, $pp, $d, $q, $blob)
                """;
            ins.Parameters.AddWithValue("$id", t.Id);
            ins.Parameters.AddWithValue("$e", t.EmployeeId);
            ins.Parameters.AddWithValue("$p", t.ProfileId);
            ins.Parameters.AddWithValue("$sid", (object?)t.SampleId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$pose", (object?)t.PoseId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$mid", t.ModelId);
            ins.Parameters.AddWithValue("$mv", t.ModelVersion);
            ins.Parameters.AddWithValue("$pp", t.PreprocessVersion);
            ins.Parameters.AddWithValue("$d", t.EmbeddingDims);
            ins.Parameters.AddWithValue("$q", (object?)t.QualityScore ?? DBNull.Value);
            ins.Parameters.AddWithValue("$blob", ProtectFloats(t.Vector));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task UpsertSampleAsync(LocalFaceSample sample, CancellationToken ct)
    {
        if (_conn == null) throw new InvalidOperationException("not initialized");
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_face_samples
                (sample_id, employee_id, pose_id, content_hash, byte_size, local_path, updated_at)
            VALUES ($id, $e, $pose, $h, $sz, $p, strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
            ON CONFLICT(sample_id) DO UPDATE SET
                employee_id = excluded.employee_id,
                pose_id = excluded.pose_id,
                content_hash = excluded.content_hash,
                byte_size = excluded.byte_size,
                local_path = excluded.local_path,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", sample.SampleId);
        cmd.Parameters.AddWithValue("$e", sample.EmployeeId);
        cmd.Parameters.AddWithValue("$pose", sample.PoseId);
        cmd.Parameters.AddWithValue("$h", sample.ContentHash);
        cmd.Parameters.AddWithValue("$sz", sample.ByteSize);
        cmd.Parameters.AddWithValue("$p", sample.LocalPath);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<LocalFaceSample>> GetSamplesAsync(string employeeId, CancellationToken ct)
    {
        if (_conn == null) return Array.Empty<LocalFaceSample>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT sample_id, employee_id, pose_id, content_hash, byte_size, local_path
            FROM local_face_samples WHERE employee_id = $e
            """;
        cmd.Parameters.AddWithValue("$e", employeeId);
        var list = new List<LocalFaceSample>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new LocalFaceSample
            {
                SampleId = r.GetString(0),
                EmployeeId = r.GetString(1),
                PoseId = r.GetString(2),
                ContentHash = r.GetString(3),
                ByteSize = r.GetInt64(4),
                LocalPath = r.GetString(5),
            });
        }
        return list;
    }

    public async Task ClearEmployeeAsync(string employeeId, CancellationToken ct)
    {
        if (_conn == null) return;
        await using var tx = await _conn.BeginTransactionAsync(ct);
        foreach (var sql in new[]
                 {
                     "DELETE FROM local_face_templates WHERE employee_id = $e",
                     "DELETE FROM local_face_profiles WHERE employee_id = $e",
                     "DELETE FROM local_face_samples WHERE employee_id = $e",
                     "DELETE FROM local_biometric_sync_state WHERE employee_id = $e",
                     "DELETE FROM local_face_verify_events WHERE employee_id = $e",
                 })
        {
            await using var cmd = _conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$e", employeeId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<LocalFaceProfile?> GetProfileAsync(string employeeId, CancellationToken ct)
    {
        if (_conn == null) return null;
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT employee_id, profile_id, status, profile_version, enrollment_version,
                   model_id, model_version, preprocess_version, embedding_dims, embedding_count
            FROM local_face_profiles WHERE employee_id = $e LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$e", employeeId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;
        return new LocalFaceProfile
        {
            EmployeeId = r.GetString(0),
            ProfileId = r.GetString(1),
            Status = r.GetString(2),
            ProfileVersion = r.GetInt32(3),
            EnrollmentVersion = r.GetInt32(4),
            ModelId = r.GetString(5),
            ModelVersion = r.GetString(6),
            PreprocessVersion = r.GetString(7),
            EmbeddingDims = r.GetInt32(8),
            EmbeddingCount = r.GetInt32(9),
        };
    }

    public async Task<IReadOnlyList<LocalFaceTemplate>> GetTemplatesAsync(string employeeId, CancellationToken ct)
    {
        if (_conn == null) return Array.Empty<LocalFaceTemplate>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, employee_id, profile_id, sample_id, pose_id, model_id, model_version,
                   preprocess_version, embedding_dims, quality_score, vector_blob
            FROM local_face_templates WHERE employee_id = $e
            """;
        cmd.Parameters.AddWithValue("$e", employeeId);
        var list = new List<LocalFaceTemplate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            float[] vector;
            try
            {
                var blob = (byte[])r["vector_blob"];
                vector = BytesToFloats(UnprotectBlob(blob));
            }
            catch
            {
                continue; // corrupt DPAPI blob — treat as missing
            }
            if (vector.Length == 0)
                continue;
            list.Add(new LocalFaceTemplate
            {
                Id = r.GetString(0),
                EmployeeId = r.GetString(1),
                ProfileId = r.GetString(2),
                SampleId = r.IsDBNull(3) ? null : r.GetString(3),
                PoseId = r.IsDBNull(4) ? null : r.GetString(4),
                ModelId = r.GetString(5),
                ModelVersion = r.GetString(6),
                PreprocessVersion = r.GetString(7),
                EmbeddingDims = r.GetInt32(8),
                QualityScore = r.IsDBNull(9) ? null : r.GetDouble(9),
                Vector = vector,
            });
        }
        return list;
    }

    public async Task<bool> IsFirstVerifyCompleteAsync(string employeeId, CancellationToken ct)
    {
        if (_conn == null) return false;
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT first_verify_completed_at, COALESCE(first_local_test_label, ''),
                   COALESCE(first_local_integrity, '')
            FROM local_biometric_sync_state WHERE employee_id = $e
            """;
        cmd.Parameters.AddWithValue("$e", employeeId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return false;
        var at = r.IsDBNull(0) ? "" : r.GetString(0);
        var label = r.IsDBNull(1) ? "" : r.GetString(1);
        var integrity = r.IsDBNull(2) ? "" : r.GetString(2);
        if (string.IsNullOrWhiteSpace(at)) return false;
        // Prefer explicit label; accept legacy rows that only have the timestamp.
        if (!string.IsNullOrWhiteSpace(label)
            && !string.Equals(label, FirstLocalTestLabelDone, StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrWhiteSpace(integrity)
            && !string.Equals(integrity, ComputeFirstLocalIntegrity(employeeId, at), StringComparison.Ordinal))
            return false; // tamper → treat as incomplete (fail-closed for first-local skip)
        return true;
    }

    public async Task MarkFirstVerifyCompleteAsync(string employeeId, CancellationToken ct)
    {
        if (_conn == null) return;
        var at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.000Z");
        var integrity = ComputeFirstLocalIntegrity(employeeId, at);
        await using var tx = await _conn.BeginTransactionAsync(ct);
        await using var cmd = _conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            INSERT INTO local_biometric_sync_state
                (employee_id, first_verify_completed_at, first_local_test_label, first_local_integrity,
                 last_verify_at, last_verify_result, failure_count, updated_at)
            VALUES ($e, $at, $label, $int,
                    $at, 'verified', 0, $at)
            ON CONFLICT(employee_id) DO UPDATE SET
                first_verify_completed_at = COALESCE(local_biometric_sync_state.first_verify_completed_at, excluded.first_verify_completed_at),
                first_local_test_label = COALESCE(NULLIF(local_biometric_sync_state.first_local_test_label, ''), excluded.first_local_test_label),
                first_local_integrity = COALESCE(NULLIF(local_biometric_sync_state.first_local_integrity, ''), excluded.first_local_integrity),
                last_verify_at = excluded.last_verify_at,
                last_verify_result = excluded.last_verify_result,
                failure_count = 0,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$e", employeeId);
        cmd.Parameters.AddWithValue("$at", at);
        cmd.Parameters.AddWithValue("$label", FirstLocalTestLabelDone);
        cmd.Parameters.AddWithValue("$int", integrity);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string ComputeFirstLocalIntegrity(string employeeId, string completedAt)
    {
        var raw = $"v1|{employeeId}|{completedAt}|{FirstLocalTestLabelDone}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    public async Task MarkSyncAckAsync(string employeeId, int profileVersion, string modelVersion, CancellationToken ct)
    {
        if (_conn == null) return;
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_biometric_sync_state
                (employee_id, last_sync_at, last_ack_at, profile_version, model_version, updated_at)
            VALUES ($e, strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'), strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'), $pv, $mv,
                    strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
            ON CONFLICT(employee_id) DO UPDATE SET
                last_sync_at = excluded.last_sync_at,
                last_ack_at = excluded.last_ack_at,
                profile_version = excluded.profile_version,
                model_version = excluded.model_version,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$e", employeeId);
        cmd.Parameters.AddWithValue("$pv", profileVersion);
        cmd.Parameters.AddWithValue("$mv", modelVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkVerifyAsync(string employeeId, string result, bool failed, CancellationToken ct)
    {
        if (_conn == null) return;
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_biometric_sync_state
                (employee_id, last_verify_at, last_verify_result, failure_count, updated_at)
            VALUES ($e, strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'), $r, $f,
                    strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
            ON CONFLICT(employee_id) DO UPDATE SET
                last_verify_at = excluded.last_verify_at,
                last_verify_result = excluded.last_verify_result,
                failure_count = CASE WHEN $failed = 1
                    THEN local_biometric_sync_state.failure_count + 1 ELSE 0 END,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$e", employeeId);
        cmd.Parameters.AddWithValue("$r", result);
        cmd.Parameters.AddWithValue("$f", failed ? 1 : 0);
        cmd.Parameters.AddWithValue("$failed", failed ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EnqueueVerifyEventAsync(LocalFaceVerifyEvent ev, CancellationToken ct)
    {
        if (_conn == null) return;
        // First-local test is client-only — never enter the sync queue.
        if (string.Equals(ev.Mode, "first", StringComparison.OrdinalIgnoreCase))
            return;
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_face_verify_events
                (id, employee_id, machine_id, mode, trigger_name, pose_id, result, score, threshold, detail, created_at, is_synced)
            VALUES ($id, $e, $m, $mode, $tr, $pose, $r, $s, $th, $d, $c, 0)
            """;
        cmd.Parameters.AddWithValue("$id", ev.Id);
        cmd.Parameters.AddWithValue("$e", ev.EmployeeId);
        cmd.Parameters.AddWithValue("$m", ev.MachineId);
        cmd.Parameters.AddWithValue("$mode", ev.Mode);
        cmd.Parameters.AddWithValue("$tr", ev.Trigger);
        cmd.Parameters.AddWithValue("$pose", ev.PoseId);
        cmd.Parameters.AddWithValue("$r", ev.Result);
        cmd.Parameters.AddWithValue("$s", (object?)ev.Score ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$th", (object?)ev.Threshold ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", ev.Detail);
        cmd.Parameters.AddWithValue("$c", ev.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<LocalFaceVerifyEvent>> ListUnsyncedEventsAsync(int limit, CancellationToken ct)
    {
        if (_conn == null) return Array.Empty<LocalFaceVerifyEvent>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, employee_id, machine_id, mode, trigger_name, pose_id, result, score, threshold, detail, created_at
            FROM local_face_verify_events
            WHERE is_synced = 0 AND lower(mode) <> 'first'
            ORDER BY created_at ASC
            LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<LocalFaceVerifyEvent>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new LocalFaceVerifyEvent
            {
                Id = r.GetString(0),
                EmployeeId = r.GetString(1),
                MachineId = r.GetString(2),
                Mode = r.GetString(3),
                Trigger = r.GetString(4),
                PoseId = r.GetString(5),
                Result = r.GetString(6),
                Score = r.IsDBNull(7) ? null : r.GetDouble(7),
                Threshold = r.IsDBNull(8) ? null : r.GetDouble(8),
                Detail = r.GetString(9),
                CreatedAt = r.GetString(10),
            });
        }
        return list;
    }

    public async Task MarkEventsSyncedAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        if (_conn == null) return;
        foreach (var id in ids)
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE local_face_verify_events SET is_synced = 1 WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static byte[] ProtectFloats(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return OperatingSystem.IsWindows() ? FaceDpapi.Protect(bytes) : bytes;
    }

    private static byte[] UnprotectBlob(byte[] stored)
    {
        try
        {
            return OperatingSystem.IsWindows() ? FaceDpapi.Unprotect(stored) : stored;
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        if (bytes.Length < sizeof(float) || bytes.Length % sizeof(float) != 0)
            return Array.Empty<float>();
        var v = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _conn = null;
    }
}
