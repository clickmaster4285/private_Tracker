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
            CREATE TABLE IF NOT EXISTS local_biometric_sync_state (
                employee_id         TEXT PRIMARY KEY,
                last_sync_at        TEXT,
                last_ack_at         TEXT,
                last_verify_at      TEXT,
                last_verify_result  TEXT,
                profile_version     INTEGER NOT NULL DEFAULT 0,
                model_version       TEXT NOT NULL DEFAULT '',
                failure_count       INTEGER NOT NULL DEFAULT 0,
                updated_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
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
            ins.Parameters.AddWithValue("$blob", FloatsToBytes(t.Vector));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task ClearEmployeeAsync(string employeeId, CancellationToken ct)
    {
        if (_conn == null) return;
        await using var tx = await _conn.BeginTransactionAsync(ct);
        foreach (var sql in new[]
                 {
                     "DELETE FROM local_face_templates WHERE employee_id = $e",
                     "DELETE FROM local_face_profiles WHERE employee_id = $e",
                     "DELETE FROM local_biometric_sync_state WHERE employee_id = $e",
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
            var blob = (byte[])r["vector_blob"];
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
                Vector = BytesToFloats(blob),
            });
        }
        return list;
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

    private static byte[] FloatsToBytes(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
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
