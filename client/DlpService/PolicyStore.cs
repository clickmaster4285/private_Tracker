using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AlphaAITracker.Service;

/// <summary>
/// Single-writer local store under %ProgramData%\&lt;APP_MUTEX&gt;\dlp\.
/// Policy + alert evidence are relational (no JSON columns).
/// </summary>
public sealed class PolicyStore : IAsyncDisposable
{
    /// <summary>Debug JSON snapshots kept under policy/. Older versions are deleted.</summary>
    private const int PolicySnapshotsToKeep = 3;

    /// <summary>Newest enforcement_audit rows kept. The table is diagnostic only.</summary>
    private const int AuditRowsToKeep = 200;

    /// <summary>Warn when dlp.db itself is this large. Unsynced alerts are not deleted.</summary>
    internal const long DiskWarnDbBytes = 256L * 1024 * 1024;

    /// <summary>Warn when the volume has less than this free. Unsynced alerts are not deleted.</summary>
    internal const long DiskWarnFreeBytes = 500L * 1024 * 1024;

    private readonly DlpConfig _cfg;
    private readonly ILogger<PolicyStore> _logger;
    private SqliteConnection? _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initLock = new();
    private Task? _initTask;

    public PolicyStore(DlpConfig cfg, ILogger<PolicyStore> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        lock (_initLock)
        {
            _initTask ??= InitCoreAsync();
        }
        return _initTask.WaitAsync(ct);
    }

    private async Task InitCoreAsync()
    {
        Directory.CreateDirectory(_cfg.DataDir);
        Directory.CreateDirectory(_cfg.PolicyDir);

        _conn = new SqliteConnection($"Data Source={_cfg.DbPath}");
        await _conn.OpenAsync();

        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS policy_meta (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    version INTEGER NOT NULL DEFAULT 0,
                    hash TEXT NOT NULL DEFAULT '',
                    applied_at TEXT
                );
                INSERT OR IGNORE INTO policy_meta (id, version, hash) VALUES (1, 0, '');

                CREATE TABLE IF NOT EXISTS policy_rules (
                    rule_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL DEFAULT '',
                    trigger TEXT NOT NULL DEFAULT '',
                    action TEXT NOT NULL DEFAULT 'alert',
                    severity TEXT NOT NULL DEFAULT 'medium',
                    alert INTEGER NOT NULL DEFAULT 1,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    schedule_enabled INTEGER NOT NULL DEFAULT 0,
                    schedule_start TEXT NOT NULL DEFAULT '',
                    schedule_end TEXT NOT NULL DEFAULT '',
                    schedule_timezone TEXT NOT NULL DEFAULT 'UTC'
                );

                CREATE TABLE IF NOT EXISTS policy_domains (
                    domain TEXT NOT NULL,
                    rule_id TEXT,
                    source TEXT NOT NULL DEFAULT 'rule',
                    PRIMARY KEY (domain, source, rule_id)
                );

                CREATE TABLE IF NOT EXISTS policy_paths (
                    rule_id TEXT NOT NULL,
                    path_pattern TEXT NOT NULL,
                    PRIMARY KEY (rule_id, path_pattern)
                );

                CREATE TABLE IF NOT EXISTS policy_schedule_days (
                    rule_id TEXT NOT NULL,
                    day TEXT NOT NULL,
                    PRIMARY KEY (rule_id, day)
                );

                CREATE TABLE IF NOT EXISTS policy_extensions (
                    rule_id TEXT NOT NULL,
                    extension TEXT NOT NULL,
                    PRIMARY KEY (rule_id, extension)
                );

                -- P1 lockdown: flag + allow hosts (kind = 'platform' | 'allow'), and a tiny
                -- key/value table (lockdown_paused_until) written by PolicyPullWorker.
                CREATE TABLE IF NOT EXISTS policy_lockdown (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    lockdown INTEGER NOT NULL DEFAULT 0
                );
                INSERT OR IGNORE INTO policy_lockdown (id, lockdown) VALUES (1, 0);

                CREATE TABLE IF NOT EXISTS policy_allow_hosts (
                    kind TEXT NOT NULL,
                    host TEXT NOT NULL,
                    PRIMARY KEY (kind, host)
                );

                CREATE TABLE IF NOT EXISTS policy_allow_apps (
                    seq INTEGER PRIMARY KEY,
                    publisher TEXT NOT NULL DEFAULT '',
                    product TEXT NOT NULL DEFAULT '',
                    sha256 TEXT NOT NULL DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS policy_kv (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL DEFAULT ''
                );

                CREATE TABLE IF NOT EXISTS dlp_alerts_local (
                    id TEXT PRIMARY KEY,
                    rule_id TEXT,
                    policy_version INTEGER NOT NULL DEFAULT 0,
                    type TEXT NOT NULL DEFAULT '',
                    severity TEXT NOT NULL DEFAULT 'medium',
                    action_taken TEXT NOT NULL DEFAULT 'alerted',
                    occurred_at TEXT NOT NULL,
                    is_synced INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_dlp_alerts_local_unsynced
                    ON dlp_alerts_local(is_synced) WHERE is_synced = 0;

                CREATE TABLE IF NOT EXISTS dlp_alert_evidence (
                    alert_id TEXT NOT NULL,
                    key TEXT NOT NULL,
                    value TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (alert_id, key)
                );

                CREATE TABLE IF NOT EXISTS enforcement_audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    at TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    detail TEXT NOT NULL DEFAULT ''
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await MigrateLegacyAsync();
        await MigrateFailClosedDefaultDenyAsync();
        await PruneStaleAsync();
        _logger.LogInformation("PolicyStore ready at {Path}", _cfg.DbPath);
    }

    /// <summary>
    /// One-shot: treat prior lockdown=0 snapshots as ON so upgraded agents fail-closed
    /// without waiting for a new Publish. Explicit <c>lockdown: false</c> after this
    /// marker still opts out.
    /// </summary>
    private async Task MigrateFailClosedDefaultDenyAsync()
    {
        const string marker = "fail_closed_default_deny_v1";
        try
        {
            await using (var check = _conn!.CreateCommand())
            {
                check.CommandText = "SELECT value FROM policy_kv WHERE key = $k";
                check.Parameters.AddWithValue("$k", marker);
                var existing = await check.ExecuteScalarAsync();
                if (existing is string)
                    return;
            }

            await using (var up = _conn.CreateCommand())
            {
                up.CommandText = "UPDATE policy_lockdown SET lockdown = 1 WHERE id = 1";
                await up.ExecuteNonQueryAsync();
            }

            await using (var mark = _conn.CreateCommand())
            {
                mark.CommandText =
                    "INSERT OR REPLACE INTO policy_kv (key, value) VALUES ($k, '1')";
                mark.Parameters.AddWithValue("$k", marker);
                await mark.ExecuteNonQueryAsync();
            }

            _logger.LogInformation(
                "Fail-closed migrate: local lockdown set ON (Publish allow hosts to open destinations)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fail-closed lockdown migrate skipped");
        }
    }

    private async Task MigrateLegacyAsync()
    {
        // Pull old policy_store.bundle_json / evidence_json once if present.
        try
        {
            await using var check = _conn!.CreateCommand();
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='policy_store'";
            var legacy = await check.ExecuteScalarAsync() as string;
            if (legacy != null)
            {
                await using var read = _conn.CreateCommand();
                read.CommandText = "SELECT version, hash, bundle_json, applied_at FROM policy_store WHERE id = 1";
                await using var r = await read.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    var version = r.GetInt64(0);
                    var hash = r.GetString(1);
                    var bundle = r.IsDBNull(2) ? "{}" : r.GetString(2);
                    var applied = r.IsDBNull(3) ? null : r.GetString(3);
                    await r.DisposeAsync();
                    await using var up = _conn.CreateCommand();
                    up.CommandText = "UPDATE policy_meta SET version=$v, hash=$h, applied_at=$a WHERE id=1";
                    up.Parameters.AddWithValue("$v", version);
                    up.Parameters.AddWithValue("$h", hash);
                    up.Parameters.AddWithValue("$a", (object?)applied ?? DBNull.Value);
                    await up.ExecuteNonQueryAsync();
                    await ReplacePolicyFromBundleAsync(bundle);
                }
                await using var drop = _conn.CreateCommand();
                drop.CommandText = "DROP TABLE IF EXISTS policy_store";
                await drop.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Legacy policy_store migrate skipped");
        }

        try
        {
            await using var col = _conn!.CreateCommand();
            col.CommandText = "PRAGMA table_info(dlp_alerts_local)";
            var hasEvidenceJson = false;
            await using var cr = await col.ExecuteReaderAsync();
            while (await cr.ReadAsync())
            {
                if (string.Equals(cr.GetString(1), "evidence_json", StringComparison.OrdinalIgnoreCase))
                    hasEvidenceJson = true;
            }
            await cr.DisposeAsync();
            if (hasEvidenceJson)
            {
                await using var rows = _conn.CreateCommand();
                rows.CommandText = "SELECT id, evidence_json FROM dlp_alerts_local WHERE evidence_json IS NOT NULL AND evidence_json <> ''";
                await using var rr = await rows.ExecuteReaderAsync();
                var pending = new List<(string Id, string Json)>();
                while (await rr.ReadAsync())
                    pending.Add((rr.GetString(0), rr.IsDBNull(1) ? "{}" : rr.GetString(1)));
                await rr.DisposeAsync();
                foreach (var (id, json) in pending)
                    await ReplaceEvidenceAsync(id, FlatEvidence(json));

                // Rebuild alerts table without evidence_json
                await using var rebuild = _conn.CreateCommand();
                rebuild.CommandText = """
                    CREATE TABLE IF NOT EXISTS dlp_alerts_local_new (
                        id TEXT PRIMARY KEY,
                        rule_id TEXT,
                        policy_version INTEGER NOT NULL DEFAULT 0,
                        type TEXT NOT NULL DEFAULT '',
                        severity TEXT NOT NULL DEFAULT 'medium',
                        action_taken TEXT NOT NULL DEFAULT 'alerted',
                        occurred_at TEXT NOT NULL,
                        is_synced INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL
                    );
                    INSERT OR IGNORE INTO dlp_alerts_local_new
                        (id, rule_id, policy_version, type, severity, action_taken, occurred_at, is_synced, created_at)
                    SELECT id, rule_id, policy_version, type, severity, action_taken, occurred_at, is_synced, created_at
                    FROM dlp_alerts_local;
                    DROP TABLE dlp_alerts_local;
                    ALTER TABLE dlp_alerts_local_new RENAME TO dlp_alerts_local;
                    CREATE INDEX IF NOT EXISTS idx_dlp_alerts_local_unsynced
                        ON dlp_alerts_local(is_synced) WHERE is_synced = 0;
                    """;
                await rebuild.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Legacy evidence_json migrate skipped");
        }
    }

    public async Task<(long Version, string Hash, string BundleJson)> GetAppliedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            long version = 0;
            var hash = "";
            await using (var cmd = _conn!.CreateCommand())
            {
                cmd.CommandText = "SELECT version, hash FROM policy_meta WHERE id = 1";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    version = reader.GetInt64(0);
                    hash = reader.GetString(1);
                }
            }
            var bundle = await AssembleBundleUnlockedAsync(version, ct);
            return (version, hash, bundle);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePolicyAsync(long version, string hash, string bundleJson, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                UPDATE policy_meta SET version = $v, hash = $h, applied_at = $at WHERE id = 1
                """;
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct);

            await ReplacePolicyFromBundleAsync(bundleJson);

            // Debug snapshot only — source of truth is relational tables above.
            var path = Path.Combine(_cfg.PolicyDir, $"policy.v{version}.json");
            await File.WriteAllTextAsync(path, bundleJson, ct);
            var removed = TrimPolicySnapshots();
            _logger.LogInformation(
                "Applied DLP policy version {Version} (dropped {Removed} older snapshots)",
                version, removed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnqueueAlertAsync(
        string id, string? ruleId, long policyVersion, string type, string severity,
        string actionTaken, DateTimeOffset occurredAt, string evidenceJson, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            try
            {
                await using var cmd = _conn!.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO dlp_alerts_local
                        (id, rule_id, policy_version, type, severity, action_taken, occurred_at, is_synced, created_at)
                    VALUES
                        ($id, $rule, $pv, $type, $sev, $act, $at, 0, $now)
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$rule", (object?)ruleId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pv", policyVersion);
                cmd.Parameters.AddWithValue("$type", type);
                cmd.Parameters.AddWithValue("$sev", severity);
                cmd.Parameters.AddWithValue("$act", actionTaken);
                cmd.Parameters.AddWithValue("$at", occurredAt.UtcDateTime.ToString("o"));
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                await cmd.ExecuteNonQueryAsync(ct);

                await ReplaceEvidenceAsync(id, FlatEvidence(evidenceJson));
            }
            catch (SqliteException ex)
            {
                // A full disk cannot store this alert. Older unsynced rows are left in place.
                _logger.LogError(
                    ex,
                    "DLP alert {AlertId} was not stored (SQLite {Code}). Unsynced alerts were not deleted.",
                    id, ex.SqliteErrorCode);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalAlert>> GetUnsyncedAlertsAsync(
        int limit, CancellationToken ct, bool newestFirst = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await using var cmd = _conn!.CreateCommand();
            // newestFirst is a fixed flag, not user text.
            var order = newestFirst ? "DESC" : "ASC";
            cmd.CommandText = $"""
                SELECT id, rule_id, policy_version, type, severity, action_taken, occurred_at
                FROM dlp_alerts_local WHERE is_synced = 0
                ORDER BY occurred_at {order} LIMIT $lim
                """;
            cmd.Parameters.AddWithValue("$lim", limit);
            var list = new List<LocalAlert>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new LocalAlert(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    DateTimeOffset.Parse(reader.GetString(6)),
                    new Dictionary<string, string>()));
            }
            await reader.DisposeAsync();

            foreach (var a in list)
            {
                await using var ev = _conn.CreateCommand();
                ev.CommandText = "SELECT key, value FROM dlp_alert_evidence WHERE alert_id = $id";
                ev.Parameters.AddWithValue("$id", a.Id);
                await using var er = await ev.ExecuteReaderAsync(ct);
                while (await er.ReadAsync(ct))
                    a.Evidence[er.GetString(0)] = er.IsDBNull(1) ? "" : er.GetString(1);
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkAlertsSyncedAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            // One transaction per accepted id so a crash cannot drop evidence and leave the alert,
            // or the reverse. Ids the server did not list are not passed in.
            await using var tx = (SqliteTransaction)await _conn!.BeginTransactionAsync(ct);
            try
            {
                foreach (var id in idList)
                {
                    await using var evidence = _conn.CreateCommand();
                    evidence.Transaction = tx;
                    evidence.CommandText = "DELETE FROM dlp_alert_evidence WHERE alert_id = $id";
                    evidence.Parameters.AddWithValue("$id", id);
                    await evidence.ExecuteNonQueryAsync(ct);

                    await using var alert = _conn.CreateCommand();
                    alert.Transaction = tx;
                    alert.CommandText = "DELETE FROM dlp_alerts_local WHERE id = $id";
                    alert.Parameters.AddWithValue("$id", id);
                    await alert.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AuditAsync(string kind, string detail, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "INSERT INTO enforcement_audit (at, kind, detail) VALUES ($at, $k, $d)";
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$d", detail);
            await cmd.ExecuteNonQueryAsync(ct);
            await TrimAuditUnlockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReplacePolicyFromBundleAsync(string bundleJson)
    {
        await using (var clear = _conn!.CreateCommand())
        {
            clear.CommandText = """
                DELETE FROM policy_rules;
                DELETE FROM policy_domains;
                DELETE FROM policy_paths;
                DELETE FROM policy_schedule_days;
                DELETE FROM policy_extensions;
                DELETE FROM policy_allow_hosts;
                DELETE FROM policy_allow_apps;
                UPDATE policy_lockdown SET lockdown = 0 WHERE id = 1;
                """;
            await clear.ExecuteNonQueryAsync();
        }

        if (string.IsNullOrWhiteSpace(bundleJson))
            return;

        using var doc = JsonDocument.Parse(bundleJson);
        var root = doc.RootElement;

        // Missing lockdown → ON (fail-closed). Explicit false → OFF.
        var lockdownOn = true;
        if (root.TryGetProperty("lockdown", out var ldEl))
            lockdownOn = ldEl.ValueKind != JsonValueKind.False;
        await using (var ld = _conn.CreateCommand())
        {
            ld.CommandText = "UPDATE policy_lockdown SET lockdown = $v WHERE id = 1";
            ld.Parameters.AddWithValue("$v", lockdownOn ? 1 : 0);
            await ld.ExecuteNonQueryAsync();
        }
        await InsertAllowHostsAsync("platform", root, "platformAllow");
        await InsertAllowHostsAsync("allow", root, "allow");
        await InsertAllowAppsAsync(root);

        if (root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in rules.EnumerateArray())
            {
                var id = rule.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) continue;
                var match = rule.TryGetProperty("match", out var m) ? m : default;
                var sched = match.ValueKind == JsonValueKind.Object && match.TryGetProperty("schedule", out var s)
                    ? s : default;

                await using var ins = _conn.CreateCommand();
                ins.CommandText = """
                    INSERT OR REPLACE INTO policy_rules
                    (rule_id, name, trigger, action, severity, alert, enabled,
                     schedule_enabled, schedule_start, schedule_end, schedule_timezone)
                    VALUES ($id,$n,$t,$a,$sev,$al,$en,$se,$ss,$ee,$tz)
                    """;
                ins.Parameters.AddWithValue("$id", id);
                ins.Parameters.AddWithValue("$n", rule.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
                ins.Parameters.AddWithValue("$t", rule.TryGetProperty("trigger", out var tr) ? tr.GetString() ?? "" : "");
                ins.Parameters.AddWithValue("$a", rule.TryGetProperty("action", out var ac) ? ac.GetString() ?? "alert" : "alert");
                ins.Parameters.AddWithValue("$sev", rule.TryGetProperty("severity", out var sev) ? sev.GetString() ?? "medium" : "medium");
                ins.Parameters.AddWithValue("$al", !rule.TryGetProperty("alert", out var al) || al.GetBoolean() ? 1 : 0);
                ins.Parameters.AddWithValue("$en", !rule.TryGetProperty("enabled", out var en) || en.GetBoolean() ? 1 : 0);
                ins.Parameters.AddWithValue("$se", sched.ValueKind == JsonValueKind.Object && sched.TryGetProperty("enabled", out var sen) && sen.GetBoolean() ? 1 : 0);
                ins.Parameters.AddWithValue("$ss", sched.ValueKind == JsonValueKind.Object && sched.TryGetProperty("start", out var st) ? st.GetString() ?? "" : "");
                ins.Parameters.AddWithValue("$ee", sched.ValueKind == JsonValueKind.Object && sched.TryGetProperty("end", out var ed) ? ed.GetString() ?? "" : "");
                ins.Parameters.AddWithValue("$tz", sched.ValueKind == JsonValueKind.Object && sched.TryGetProperty("timezone", out var tz) ? tz.GetString() ?? "UTC" : "UTC");
                await ins.ExecuteNonQueryAsync();

                await InsertStringArrayAsync("policy_domains", "domain", id, "rule", match, "domains");
                await InsertStringArrayAsync("policy_paths", "path_pattern", id, null, match, "paths");
                await InsertStringArrayAsync("policy_extensions", "extension", id, null, match, "extensions");
                if (sched.ValueKind == JsonValueKind.Object && sched.TryGetProperty("days", out var days) && days.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in days.EnumerateArray())
                    {
                        var day = d.GetString();
                        if (string.IsNullOrWhiteSpace(day)) continue;
                        await using var dayCmd = _conn.CreateCommand();
                        dayCmd.CommandText = "INSERT OR IGNORE INTO policy_schedule_days (rule_id, day) VALUES ($r,$d)";
                        dayCmd.Parameters.AddWithValue("$r", id);
                        dayCmd.Parameters.AddWithValue("$d", day.Trim().ToLowerInvariant());
                        await dayCmd.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        if (root.TryGetProperty("shadowDeny", out var sd) &&
            sd.ValueKind == JsonValueKind.Object &&
            sd.TryGetProperty("domains", out var sdd) &&
            sdd.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in sdd.EnumerateArray())
            {
                var dom = d.GetString();
                if (string.IsNullOrWhiteSpace(dom)) continue;
                await using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO policy_domains (domain, rule_id, source) VALUES ($d,'', 'shadow')";
                cmd.Parameters.AddWithValue("$d", dom.Trim().ToLowerInvariant());
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    private async Task InsertAllowHostsAsync(string kind, JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var obj) || obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Array)
            return;
        foreach (var el in hosts.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) continue;
            var h = el.GetString();
            if (string.IsNullOrWhiteSpace(h)) continue;
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO policy_allow_hosts (kind, host) VALUES ($k,$h)";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$h", h.Trim());
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task InsertAllowAppsAsync(JsonElement root)
    {
        if (!root.TryGetProperty("allow", out var allow) || allow.ValueKind != JsonValueKind.Object ||
            !allow.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
            return;
        var seq = 0;
        foreach (var el in apps.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var publisher = el.TryGetProperty("publisher", out var p) ? p.GetString() ?? "" : "";
            var product = el.TryGetProperty("product", out var pr) ? pr.GetString() ?? "" : "";
            var sha = el.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "";
            publisher = publisher.Trim();
            product = product.Trim();
            sha = sha.Trim().ToLowerInvariant().Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
            if (publisher.Length == 0 && sha.Length != 64) continue;
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                "INSERT INTO policy_allow_apps (seq, publisher, product, sha256) VALUES ($i,$p,$r,$h)";
            cmd.Parameters.AddWithValue("$i", seq++);
            cmd.Parameters.AddWithValue("$p", publisher);
            cmd.Parameters.AddWithValue("$r", product);
            cmd.Parameters.AddWithValue("$h", sha);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private const string KeyLockdownPausedUntil = "lockdown_paused_until";

    /// <summary>
    /// Persist the server's <c>lockdownPausedUntil</c> (UTC). Null clears the pause.
    /// EnforceWorker reads it every tick via <see cref="GetLockdownPausedUntilAsync"/>.
    /// </summary>
    public async Task SetLockdownPausedUntilAsync(DateTimeOffset? until, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await using var cmd = _conn!.CreateCommand();
            if (until is null)
            {
                cmd.CommandText = "DELETE FROM policy_kv WHERE key = $k";
                cmd.Parameters.AddWithValue("$k", KeyLockdownPausedUntil);
            }
            else
            {
                cmd.CommandText = "INSERT OR REPLACE INTO policy_kv (key, value) VALUES ($k, $v)";
                cmd.Parameters.AddWithValue("$k", KeyLockdownPausedUntil);
                cmd.Parameters.AddWithValue("$v", until.Value.UtcDateTime.ToString("o"));
            }
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DateTimeOffset?> GetLockdownPausedUntilAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT value FROM policy_kv WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", KeyLockdownPausedUntil);
            var v = await cmd.ExecuteScalarAsync(ct) as string;
            if (string.IsNullOrWhiteSpace(v)) return null;
            return DateTimeOffset.TryParse(
                v, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InsertStringArrayAsync(
        string table, string col, string ruleId, string? source, JsonElement match, string prop)
    {
        if (match.ValueKind != JsonValueKind.Object || !match.TryGetProperty(prop, out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var el in arr.EnumerateArray())
        {
            var v = el.GetString();
            if (string.IsNullOrWhiteSpace(v)) continue;
            await using var cmd = _conn!.CreateCommand();
            if (source != null)
            {
                cmd.CommandText = $"INSERT OR IGNORE INTO {table} ({col}, rule_id, source) VALUES ($v,$r,$s)";
                cmd.Parameters.AddWithValue("$s", source);
            }
            else if (table == "policy_paths")
            {
                cmd.CommandText = "INSERT OR IGNORE INTO policy_paths (rule_id, path_pattern) VALUES ($r,$v)";
            }
            else
            {
                cmd.CommandText = $"INSERT OR IGNORE INTO {table} (rule_id, {col}) VALUES ($r,$v)";
            }
            cmd.Parameters.AddWithValue("$v", v.Trim());
            cmd.Parameters.AddWithValue("$r", ruleId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<string> AssembleBundleUnlockedAsync(long version, CancellationToken ct)
    {
        var rules = new List<object>();
        await using (var cmd = _conn!.CreateCommand())
        {
            cmd.CommandText = """
                SELECT rule_id, name, trigger, action, severity, alert, enabled,
                       schedule_enabled, schedule_start, schedule_end, schedule_timezone
                FROM policy_rules
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var match = new Dictionary<string, object?>();
                var domains = await LoadStringsAsync(
                    "SELECT domain FROM policy_domains WHERE rule_id=$r AND source='rule'", id, ct);
                if (domains.Count > 0) match["domains"] = domains;
                var paths = await LoadStringsAsync(
                    "SELECT path_pattern FROM policy_paths WHERE rule_id=$r", id, ct);
                if (paths.Count > 0) match["paths"] = paths;
                var exts = await LoadStringsAsync(
                    "SELECT extension FROM policy_extensions WHERE rule_id=$r", id, ct);
                if (exts.Count > 0) match["extensions"] = exts;
                if (reader.GetInt64(7) == 1)
                {
                    var days = await LoadStringsAsync(
                        "SELECT day FROM policy_schedule_days WHERE rule_id=$r", id, ct);
                    match["schedule"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["start"] = reader.GetString(8),
                        ["end"] = reader.GetString(9),
                        ["timezone"] = reader.GetString(10),
                        ["days"] = days,
                    };
                }
                rules.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["name"] = reader.GetString(1),
                    ["trigger"] = reader.GetString(2),
                    ["action"] = reader.GetString(3),
                    ["severity"] = reader.GetString(4),
                    ["alert"] = reader.GetInt64(5) == 1,
                    ["enabled"] = reader.GetInt64(6) == 1,
                    ["match"] = match,
                    ["scope"] = new Dictionary<string, object?> { ["all"] = true },
                });
            }
        }

        var shadow = await LoadStringsAsync(
            "SELECT domain FROM policy_domains WHERE source='shadow'", null, ct);

        long lockdown = 0;
        await using (var ld = _conn!.CreateCommand())
        {
            ld.CommandText = "SELECT lockdown FROM policy_lockdown WHERE id = 1";
            lockdown = Convert.ToInt64(await ld.ExecuteScalarAsync(ct) ?? 0L);
        }
        var platformHosts = await LoadStringsAsync(
            "SELECT host FROM policy_allow_hosts WHERE kind='platform'", null, ct);
        var allowHosts = await LoadStringsAsync(
            "SELECT host FROM policy_allow_hosts WHERE kind='allow'", null, ct);
        var allowApps = new List<Dictionary<string, object?>>();
        await using (var appCmd = _conn!.CreateCommand())
        {
            appCmd.CommandText =
                "SELECT publisher, product, sha256 FROM policy_allow_apps ORDER BY seq";
            await using var appReader = await appCmd.ExecuteReaderAsync(ct);
            while (await appReader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>();
                var pub = appReader.GetString(0);
                var prod = appReader.GetString(1);
                var sha = appReader.GetString(2);
                if (!string.IsNullOrEmpty(pub)) row["publisher"] = pub;
                if (!string.IsNullOrEmpty(prod)) row["product"] = prod;
                if (!string.IsNullOrEmpty(sha)) row["sha256"] = sha;
                if (row.Count > 0) allowApps.Add(row);
            }
        }

        // version 0: omit lockdown so parser treats it as default ON (no Publish yet).
        // version > 0: emit explicit true/false from the last applied snapshot.
        var dict = new Dictionary<string, object?>
        {
            ["policyVersion"] = version,
            ["platformAllow"] = new Dictionary<string, object?> { ["hosts"] = platformHosts },
            ["allow"] = new Dictionary<string, object?>
            {
                ["hosts"] = allowHosts,
                ["apps"] = allowApps,
            },
            ["rules"] = rules,
            ["shadowDeny"] = new Dictionary<string, object?>
            {
                ["domains"] = shadow,
                ["publishers"] = Array.Empty<string>(),
                ["appNames"] = Array.Empty<string>(),
            },
        };
        if (version > 0)
            dict["lockdown"] = lockdown == 1;
        return JsonSerializer.Serialize(dict);
    }

    private async Task<List<string>> LoadStringsAsync(string sql, string? ruleId, CancellationToken ct)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        if (ruleId != null)
            cmd.Parameters.AddWithValue("$r", ruleId);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    private async Task ReplaceEvidenceAsync(string alertId, Dictionary<string, string> evidence)
    {
        await using var del = _conn!.CreateCommand();
        del.CommandText = "DELETE FROM dlp_alert_evidence WHERE alert_id = $id";
        del.Parameters.AddWithValue("$id", alertId);
        await del.ExecuteNonQueryAsync();
        foreach (var (k, v) in evidence)
        {
            await using var ins = _conn.CreateCommand();
            ins.CommandText = "INSERT OR REPLACE INTO dlp_alert_evidence (alert_id, key, value) VALUES ($a,$k,$v)";
            ins.Parameters.AddWithValue("$a", alertId);
            ins.Parameters.AddWithValue("$k", k);
            ins.Parameters.AddWithValue("$v", v ?? "");
            await ins.ExecuteNonQueryAsync();
        }
    }

    internal static Dictionary<string, string> FlatEvidence(string? evidenceJson)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(evidenceJson))
            return dict;
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return dict;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                dict[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Number => p.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    JsonValueKind.Array => string.Join(",",
                        p.Value.EnumerateArray()
                            .Select(el => el.ValueKind == JsonValueKind.String
                                ? (el.GetString() ?? "")
                                : el.GetRawText())),
                    _ => p.Value.GetRawText(),
                };
            }
        }
        catch { /* ignore bad evidence */ }
        return dict;
    }

    /// <summary>
    /// Deletes alerts the server already accepted (<c>is_synced = 1</c>), orphan evidence,
    /// and excess audit rows. Unsynced alerts are never deleted for age or count.
    /// </summary>
    private async Task PruneStaleAsync()
    {
        var syncedAlerts = 0;
        var auditDropped = 0;
        try
        {
            await using (var evidence = _conn!.CreateCommand())
            {
                evidence.CommandText = """
                    DELETE FROM dlp_alert_evidence
                    WHERE alert_id IN (SELECT id FROM dlp_alerts_local WHERE is_synced = 1)
                    """;
                await evidence.ExecuteNonQueryAsync();
            }

            await using (var alerts = _conn.CreateCommand())
            {
                alerts.CommandText = "DELETE FROM dlp_alerts_local WHERE is_synced = 1";
                syncedAlerts = await alerts.ExecuteNonQueryAsync();
            }

            await using (var orphans = _conn.CreateCommand())
            {
                orphans.CommandText = """
                    DELETE FROM dlp_alert_evidence
                    WHERE NOT EXISTS (
                        SELECT 1 FROM dlp_alerts_local a WHERE a.id = dlp_alert_evidence.alert_id)
                    """;
                await orphans.ExecuteNonQueryAsync();
            }

            auditDropped = await TrimAuditUnlockedAsync();
            await VacuumFreePagesAsync();
            await WarnDiskPressureAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DLP local retention prune failed");
        }

        var snapshots = 0;
        try { snapshots = TrimPolicySnapshots(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Policy snapshot trim skipped"); }

        if (syncedAlerts > 0 || auditDropped > 0 || snapshots > 0)
        {
            _logger.LogInformation(
                "Pruned DLP local store: syncedAlerts={Alerts}, auditRows={Audit}, policySnapshots={Snapshots}",
                syncedAlerts, auditDropped, snapshots);
        }
    }

    /// <summary>Same sweep as startup, for tests against a temporary database.</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            await PruneStaleAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// True when the database file or the free space on its volume crosses the warning line.
    /// A negative free-space value means the volume could not be read and is not pressure.
    /// </summary>
    internal static bool IsDiskPressure(long dbBytes, long freeBytes) =>
        dbBytes >= DiskWarnDbBytes || (freeBytes >= 0 && freeBytes < DiskWarnFreeBytes);

    private async Task WarnDiskPressureAsync()
    {
        long dbBytes = 0;
        if (File.Exists(_cfg.DbPath))
            dbBytes = new FileInfo(_cfg.DbPath).Length;

        long freeBytes = -1;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_cfg.DbPath));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                    freeBytes = drive.AvailableFreeSpace;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DLP disk free-space check skipped");
        }

        if (!IsDiskPressure(dbBytes, freeBytes))
            return;

        var detail = $"dbBytes={dbBytes};freeBytes={freeBytes}";
        _logger.LogWarning(
            "DLP store disk pressure ({Detail}). Unsynced alerts were not deleted. A full disk can still reject new inserts.",
            detail);
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "INSERT INTO enforcement_audit (at, kind, detail) VALUES ($at, $k, $d)";
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$k", "disk_pressure");
        cmd.Parameters.AddWithValue("$d", detail);
        await cmd.ExecuteNonQueryAsync();
        await TrimAuditUnlockedAsync();
    }

    /// <summary>
    /// Deletes leave free pages. A failed VACUUM on a previous boot retries
    /// here because freelist_count stays above zero until it succeeds.
    /// </summary>
    private async Task VacuumFreePagesAsync()
    {
        await using var freelist = _conn!.CreateCommand();
        freelist.CommandText = "PRAGMA freelist_count";
        var freePages = Convert.ToInt64(await freelist.ExecuteScalarAsync() ?? 0);
        if (freePages <= 0) return;

        await using var vacuum = _conn.CreateCommand();
        vacuum.CommandText = "VACUUM";
        await vacuum.ExecuteNonQueryAsync();
    }

    private async Task<int> TrimAuditUnlockedAsync()
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            DELETE FROM enforcement_audit
            WHERE id NOT IN (
                SELECT id FROM enforcement_audit ORDER BY id DESC LIMIT $keep)
            """;
        cmd.Parameters.AddWithValue("$keep", AuditRowsToKeep);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Keep the highest numeric policy.vN.json files. v110 stays ahead of v99.</summary>
    private int TrimPolicySnapshots()
    {
        if (!Directory.Exists(_cfg.PolicyDir)) return 0;

        var parsed = new List<(long Version, string Path)>();
        foreach (var file in Directory.EnumerateFiles(_cfg.PolicyDir, "policy.v*.json"))
        {
            var name = Path.GetFileName(file);
            if (!TryParsePolicyVersion(name, out var version)) continue;
            parsed.Add((version, file));
        }

        if (parsed.Count <= PolicySnapshotsToKeep) return 0;

        var removed = 0;
        foreach (var item in parsed.OrderByDescending(p => p.Version).Skip(PolicySnapshotsToKeep))
        {
            try
            {
                File.Delete(item.Path);
                removed++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete old policy snapshot {Path}", item.Path);
            }
        }
        return removed;
    }

    internal static bool TryParsePolicyVersion(string fileName, out long version)
    {
        version = 0;
        const string prefix = "policy.v";
        const string suffix = ".json";
        if (fileName.Length <= prefix.Length + suffix.Length) return false;
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var digits = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return digits.Length > 0 && long.TryParse(digits, out version);
    }

    private void EnsureOpen()
    {
        if (_conn == null)
            throw new InvalidOperationException("PolicyStore not initialized");
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn != null)
        {
            await _conn.DisposeAsync();
            _conn = null;
        }
        _gate.Dispose();
    }
}

public sealed class LocalAlert
{
    public LocalAlert(
        string id, string? ruleId, long policyVersion, string type, string severity,
        string actionTaken, DateTimeOffset occurredAt, Dictionary<string, string> evidence)
    {
        Id = id;
        RuleId = ruleId;
        PolicyVersion = policyVersion;
        Type = type;
        Severity = severity;
        ActionTaken = actionTaken;
        OccurredAt = occurredAt;
        Evidence = evidence;
    }

    public string Id { get; }
    public string? RuleId { get; }
    public long PolicyVersion { get; }
    public string Type { get; }
    public string Severity { get; }
    public string ActionTaken { get; }
    public DateTimeOffset OccurredAt { get; }
    public Dictionary<string, string> Evidence { get; }
}
