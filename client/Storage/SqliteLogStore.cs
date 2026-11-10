using System.Data.Common;
using Microsoft.Data.Sqlite;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;
using client.Configuration;
using client.Services;

namespace client.Storage;

public class SqliteLogStore : ILogStore, IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    // Machine identity (R2/R7, 2026-10-19). Resolved LAZILY on every use instead of
    // being captured at construction: `--remove-account` resets AppConfig.ClientId to
    // empty so the next login mints a brand-new id, and a store holding the old value
    // would stamp the retired machine id onto the fresh account.
    private readonly Func<string>? _machineIdProvider;
    private string MachineId => _machineIdProvider?.Invoke() ?? string.Empty;
    private SqliteConnection? _connection;

    /// <summary>
    /// Read-only connection (Time and Attendance, Phase 1, finalplan R8 + section 2.8).
    /// Background readers (AttendanceAggregator, future read-heavy services) use this
    /// connection so they never serialize with the gated write connection that
    /// LogCollectorService and SyncService hold during the collection cycle. WAL mode
    /// (enabled in InitializeAsync below) is the prerequisite - readers and writers can
    /// proceed concurrently only when the journal mode is WAL.
    /// </summary>
    private SqliteConnection? _readConnection;
    private readonly SemaphoreSlim _readConnectionGate = new(1, 1);

    /// <summary>
    /// Concurrency gate protecting the single shared WRITE SqliteConnection.
    /// SemaphoreSlim(1,1) = exclusive access. NOT reentrant - see private
    /// ungated helpers (SetStatusCoreAsync, GetEmployeeInfoCoreAsync) that
    /// composite methods call instead of the public gated versions.
    /// The READ connection above is NOT gated by this; it has its own gate which
    /// is essentially a no-op (a single reader at a time) but reserves the
    /// connection so two readers don't share its commands concurrently.
    /// </summary>
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public SqliteLogStore(string dbPath, string? encryptionKey = null, string? machineId = null, Func<string>? machineIdProvider = null)
    {
        _dbPath = dbPath;
        // Either a fixed id (legacy call sites) or a live provider (Program.cs passes
        // `() => config.ClientId` so a wipe + re-login in the same process is correct).
        _machineIdProvider = machineIdProvider ?? (string.IsNullOrEmpty(machineId) ? null : () => machineId);
        // Cache=Shared is the WAL-friendly connection string: the same .db file
        // can be opened by the read connection without re-opening the file.
        var cs = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
        _connectionString = cs;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _initializationGate.WaitAsync(ct);
        try
        {
        if (_initialized) return;

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await _connectionGate.WaitAsync(ct);
        try
        {
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(ct);

            // Defense-in-depth: if another process (or same-process race) holds
            // a lock on the .db file, wait up to 5s instead of immediately failing.
            using var busyCmd = _connection.CreateCommand();
            busyCmd.CommandText = "PRAGMA busy_timeout = 5000;";
            await busyCmd.ExecuteNonQueryAsync(ct);

            // ── WAL mode (Time and Attendance, finalplan R8 + section 2.8) ──
            // MUST be set BEFORE opening the read-only connection. The read
            // connection inherits the journal mode of the database file, so if WAL
            // were enabled AFTER the read connection opened, that connection would
            // see a legacy rollback journal and readers would still block writers.
            // PRAGMA journal_mode = WAL is idempotent and persistent across
            // connections - setting it on every boot is the safe pattern.
            using (var walCmd = _connection.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode = WAL;";
                await walCmd.ExecuteNonQueryAsync(ct);
            }
            // synchronous=NORMAL pairs with WAL: durability is bounded by the
            // checkpoint interval, not every write. Faster than the default FULL
            // while still surviving a power loss without DB corruption (only the
            // most recent uncheckpointed transaction can be lost, acceptable for
            // telemetry).
            using (var syncCmd = _connection.CreateCommand())
            {
                syncCmd.CommandText = "PRAGMA synchronous = NORMAL;";
                await syncCmd.ExecuteNonQueryAsync(ct);
            }

            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.CreateTableSql;
            await cmd.ExecuteNonQueryAsync(ct);

            await RunMigrationsAsync(ct);

            // Machine identity (R2): every telemetry INSERT reads its machine_id from
            // this single row, so writing it here is the one place the identity is
            // established. Upsert keeps it current if the id changed (e.g. after a
            // prior wipe generated a new one).
            await SetMachineIdentityCoreAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }

        // Open the read-only connection AFTER WAL is enabled (see comment above).
        // Mode=ReadOnly ensures this connection physically CANNOT write; any
        // accidental write call throws, eliminating an entire class of bugs where
        // a reader silently mutates the DB.
        await _readConnectionGate.WaitAsync(ct);
        try
        {
            _readConnection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Cache=Shared");
            await _readConnection.OpenAsync(ct);
            using var rBusy = _readConnection.CreateCommand();
            rBusy.CommandText = "PRAGMA busy_timeout = 5000;";
            await rBusy.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _readConnectionGate.Release();
        }
        _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    /// <summary>
    /// Time and Attendance (finalplan R8, section 2.8): acquire the read connection
    /// for callers that are read-only (AttendanceAggregator, future analytics, tests).
    /// The callback receives a live SqliteConnection in ReadOnly mode; it is the
    /// callback's responsibility to create + execute + dispose commands quickly. The
    /// connection gate ensures only one reader runs at a time so two concurrent
    /// SELECTs never share an in-flight DataReader.
    /// </summary>
    internal async Task<T> WithReadConnectionAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken ct)
    {
        if (_readConnection == null) throw new InvalidOperationException("SqliteLogStore not initialized");
        await _readConnectionGate.WaitAsync(ct);
        try
        {
            return await action(_readConnection);
        }
        finally
        {
            _readConnectionGate.Release();
        }
    }

    private async Task RunMigrationsAsync(CancellationToken ct)
    {
        if (_connection == null) return;

        foreach (var statement in DatabaseSchema.MigrateSql.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var sql = StripSqlLineComments(statement).Trim();
            if (string.IsNullOrEmpty(sql)) continue;
            try
            {
                var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
                (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)
                 || ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)))
            {
                // duplicate column: column already exists from a prior migration
                // already exists: index/table already created (idempotent migration fragments)
            }
        }

        // Inventory lifecycle v2: one-time rebuild of the inventory tables into the
        // rows-per-install-cycle shape (see RebuildInventoryTablesIfLegacyAsync).
        await RebuildInventoryTablesIfLegacyAsync(ct);

        var indexCmd = _connection.CreateCommand();
        indexCmd.CommandText = @"
            CREATE INDEX IF NOT EXISTS idx_app_sessions_process_id ON app_sessions(process_id);
            CREATE INDEX IF NOT EXISTS idx_app_sessions_open ON app_sessions(ended_at, process_id);
            CREATE INDEX IF NOT EXISTS idx_app_items_context ON app_items(app_session_id, item_type, identifier);
            -- Sync engine (2026-08-11): keep WHERE is_synced = 0 ORDER BY ... LIMIT cheap even at
            -- 50k+ queued rows. Fresh DBs already get these via DatabaseSchema; existing DBs need
            -- them here (app_sessions/app_items were the only big tables without an is_synced index).
            CREATE INDEX IF NOT EXISTS idx_app_sessions_unsent ON app_sessions(is_synced, started_at);
            CREATE INDEX IF NOT EXISTS idx_app_items_unsent ON app_items(is_synced, opened_at);
        ";
        await indexCmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Removes -- line comments before executing a migration fragment. MigrateSql is split on
    /// ';' and a semicolon inside a comment would otherwise produce a bogus statement
    /// (e.g. "normal OS events..." after "sentinel rows; normal...").
    /// </summary>
    private static string StripSqlLineComments(string sql)
    {
        var kept = new List<string>();
        foreach (var line in sql.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    /// <summary>
    /// One-time migration to the inventory lifecycle v2 shape (rows-per-install-cycle).
    /// Detects a legacy table (v1 lifecycle: has install_count, or lacks is_installed) and
    /// rebuilds installed_applications / installed_packages: app_name and the package
    /// fingerprint lose their UNIQUE constraints (a reinstall opens a NEW row), install_count
    /// is dropped, and install_date is reset to NULL — the OS did not report it, so
    /// pre-existing software shows as "Unknown" until a fresh install is detected.
    ///
    /// ⚠️ GATE-CONTRACT: this runs from RunMigrationsAsync which is already inside
    /// InitializeAsync's _connectionGate — SemaphoreSlim(1,1) is NOT reentrant, so this
    /// method MUST NOT acquire the gate itself (acquiring it here deadlocks startup and the
    /// rebuild silently never runs). It is only ever called while the gate is held.
    /// </summary>
    private async Task RebuildInventoryTablesIfLegacyAsync(CancellationToken ct)
    {
        if (_connection == null) return;

        var probe = _connection.CreateCommand();
        probe.CommandText = @"
            SELECT (SELECT COUNT(*) FROM pragma_table_info('installed_applications') WHERE name = 'install_count')
                 + CASE WHEN (SELECT COUNT(*) FROM pragma_table_info('installed_applications') WHERE name = 'is_installed') = 0 THEN 1 ELSE 0 END";
        var legacy = Convert.ToInt32(await probe.ExecuteScalarAsync(ct)) > 0;
        if (!legacy) return;

        var hasLifecycle = true;
        var probe2 = _connection.CreateCommand();
        probe2.CommandText = "SELECT COUNT(*) FROM pragma_table_info('installed_applications') WHERE name = 'uninstall_date'";
        hasLifecycle = Convert.ToInt32(await probe2.ExecuteScalarAsync(ct)) > 0;

        // install_date is deliberately NOT carried over: v1 backfilled it with detected_at,
        // which was wrong (unknown ≠ first-seen). Reset to NULL for all pre-existing rows.
        // is_installed is DERIVED from uninstall_date (NULL = open/installed) rather than
        // copied — v1 could carry is_installed=1 together with an uninstall_date (reinstall
        // artifact), which would wrongly render a closed cycle as installed.
        var appSelect = hasLifecycle
            ? "SELECT id, app_name, binary_name, app_version, publisher, install_path, NULL, uninstall_string, change_type, CASE WHEN uninstall_date IS NULL THEN 1 ELSE 0 END, uninstall_date, is_browser, desktop_id, categories, detected_at, is_synced, synced_at, created_at FROM installed_applications"
            : "SELECT id, app_name, binary_name, app_version, publisher, install_path, NULL, uninstall_string, change_type, 1, NULL, is_browser, desktop_id, categories, detected_at, is_synced, synced_at, created_at FROM installed_applications";
        var pkgSelect = hasLifecycle
            ? "SELECT id, package_name, version, category, source_manager, install_path, publisher, description, NULL, CASE WHEN uninstall_date IS NULL THEN 1 ELSE 0 END, uninstall_date, detected_at, is_synced, synced_at, created_at FROM installed_packages"
            : "SELECT id, package_name, version, category, source_manager, install_path, publisher, description, NULL, 1, NULL, detected_at, is_synced, synced_at, created_at FROM installed_packages";

        // No gate acquisition here — the gate is already held by the caller (see doc).
        // The rebuild DROPs + RENAMEs tables that app_sessions FKs reference (installed_app_id /
        // installed_package_id), so foreign_keys must be OFF for the duration (SQLite's standard
        // table-rebuild procedure). Ids are preserved by the rebuild, so the FK references stay
        // valid after the rename. PRAGMA foreign_keys is a no-op INSIDE a transaction — it must
        // be toggled outside the tx (we hold the connection gate, so this is exclusive).
        var fkOff = _connection.CreateCommand();
        fkOff.CommandText = "PRAGMA foreign_keys = OFF";
        await fkOff.ExecuteNonQueryAsync(ct);
        try
        {
            await using var tx = await _connection.BeginTransactionAsync(ct);

            // ── installed_applications: no UNIQUE(app_name), no install_count ──
            await ExecTxAsync("DROP TABLE IF EXISTS installed_applications_new", tx, ct);
            await ExecTxAsync(@"
                CREATE TABLE installed_applications_new (
                    id               TEXT PRIMARY KEY,
                    app_name         TEXT NOT NULL,
                    binary_name      TEXT NOT NULL DEFAULT '',
                    app_version      TEXT NOT NULL DEFAULT '',
                    publisher        TEXT NOT NULL DEFAULT '',
                    install_path     TEXT NOT NULL DEFAULT '',
                    install_date     TEXT,
                    uninstall_string TEXT NOT NULL DEFAULT '',
                    change_type      TEXT NOT NULL DEFAULT 'seen',
                    is_installed     INTEGER NOT NULL DEFAULT 1,
                    uninstall_date   TEXT,
                    is_browser       INTEGER NOT NULL DEFAULT 0,
                    desktop_id       TEXT NOT NULL DEFAULT '',
                    categories       TEXT NOT NULL DEFAULT '',
                    detected_at      TEXT NOT NULL,
                    is_synced        INTEGER NOT NULL DEFAULT 0,
                    synced_at        TEXT,
                    created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
                )", tx, ct);
            await ExecTxAsync(@"
                INSERT INTO installed_applications_new
                    (id, app_name, binary_name, app_version, publisher, install_path, install_date,
                     uninstall_string, change_type, is_installed, uninstall_date,
                     is_browser, desktop_id, categories, detected_at, is_synced, synced_at, created_at)
                " + appSelect, tx, ct);
            await ExecTxAsync("DROP TABLE installed_applications", tx, ct);
            await ExecTxAsync("ALTER TABLE installed_applications_new RENAME TO installed_applications", tx, ct);
            await ExecTxAsync(@"
                CREATE INDEX IF NOT EXISTS idx_installed_apps_unsent ON installed_applications(is_synced, detected_at);
                CREATE INDEX IF NOT EXISTS idx_installed_apps_name ON installed_applications(app_name);
                CREATE INDEX IF NOT EXISTS idx_installed_apps_binary ON installed_applications(binary_name);
                CREATE INDEX IF NOT EXISTS idx_installed_apps_desktop_id ON installed_applications(desktop_id);", tx, ct);

            // ── installed_packages: no install_count ──
            await ExecTxAsync("DROP TABLE IF EXISTS installed_packages_new", tx, ct);
            await ExecTxAsync(@"
                CREATE TABLE installed_packages_new (
                    id               TEXT PRIMARY KEY,
                    package_name     TEXT NOT NULL,
                    version          TEXT NOT NULL DEFAULT '',
                    category         TEXT NOT NULL DEFAULT 'tool',
                    source_manager   TEXT NOT NULL DEFAULT '',
                    install_path     TEXT NOT NULL DEFAULT '',
                    publisher        TEXT NOT NULL DEFAULT '',
                    description      TEXT NOT NULL DEFAULT '',
                    install_date     TEXT,
                    is_installed     INTEGER NOT NULL DEFAULT 1,
                    uninstall_date   TEXT,
                    detected_at      TEXT NOT NULL,
                    is_synced        INTEGER NOT NULL DEFAULT 0,
                    synced_at        TEXT,
                    created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
                )", tx, ct);
            await ExecTxAsync(@"
                INSERT INTO installed_packages_new
                    (id, package_name, version, category, source_manager, install_path,
                     publisher, description, install_date, is_installed, uninstall_date,
                     detected_at, is_synced, synced_at, created_at)
                " + pkgSelect, tx, ct);
            await ExecTxAsync("DROP TABLE installed_packages", tx, ct);
            await ExecTxAsync("ALTER TABLE installed_packages_new RENAME TO installed_packages", tx, ct);
            await ExecTxAsync(@"
                CREATE INDEX IF NOT EXISTS idx_installed_packages_unsent ON installed_packages(is_synced, detected_at);
                CREATE INDEX IF NOT EXISTS idx_installed_packages_source ON installed_packages(source_manager);
                CREATE INDEX IF NOT EXISTS idx_installed_packages_category ON installed_packages(category);
                CREATE INDEX IF NOT EXISTS idx_installed_packages_fingerprint ON installed_packages(package_name, source_manager);", tx, ct);

            await tx.CommitAsync(ct);
        }
        finally
        {
            var fkOn = _connection.CreateCommand();
            fkOn.CommandText = "PRAGMA foreign_keys = ON";
            await fkOn.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task ExecTxAsync(string sql, System.Data.Common.DbTransaction tx, CancellationToken ct)
    {
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        ((System.Data.Common.DbCommand)cmd).Transaction = tx;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ────────────────────────────────────────
    // Device Hardware Info
    // ────────────────────────────────────────

    public async Task StoreDeviceHardwareInfoAsync(IReadOnlyList<DeviceHardwareInfo> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.InsertDeviceHardwareInfoSql;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pMac = cmd.Parameters.Add("$mac_address", SqliteType.Text);
            var pHost = cmd.Parameters.Add("$hostname", SqliteType.Text);
            var pOsName = cmd.Parameters.Add("$os_name", SqliteType.Text);
            var pOsVer = cmd.Parameters.Add("$os_version", SqliteType.Text);
            var pCpuModel = cmd.Parameters.Add("$cpu_model", SqliteType.Text);
            var pCpuCores = cmd.Parameters.Add("$cpu_cores", SqliteType.Integer);
            var pRamMb = cmd.Parameters.Add("$ram_total_mb", SqliteType.Integer);
            var pGpuModel = cmd.Parameters.Add("$gpu_model", SqliteType.Text);
            var pGpuVram = cmd.Parameters.Add("$gpu_vram_mb", SqliteType.Integer);
            var pCollectedAt = cmd.Parameters.Add("$collected_at", SqliteType.Text);

            await using var tx = await _connection.BeginTransactionAsync(ct);
            ((DbCommand)cmd).Transaction = tx;
            foreach (var e in entries)
            {
                pId.Value = e.Id;
                pMac.Value = e.MacAddress;
                pHost.Value = e.Hostname;
                pOsName.Value = e.OsName;
                pOsVer.Value = e.OsVersion;
                pCpuModel.Value = e.CpuModel;
                pCpuCores.Value = e.CpuCores;
                pRamMb.Value = e.RamTotalMb;
                pGpuModel.Value = e.GpuModel;
                pGpuVram.Value = e.GpuVramMb;
                pCollectedAt.Value = e.CollectedAt.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<DeviceHardwareInfo>> GetUnsentDeviceHardwareInfoAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<DeviceHardwareInfo>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM device_hardware_info WHERE is_synced = 0 ORDER BY collected_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<DeviceHardwareInfo>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapDeviceHardwareReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<DeviceHardwareInfo?> GetLastDeviceHardwareInfoAsync(CancellationToken ct)
    {
        if (_connection == null) return null;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM device_hardware_info ORDER BY collected_at DESC, created_at DESC LIMIT 1";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) return MapDeviceHardwareReader(reader);
            return null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkDeviceHardwareInfoSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("device_hardware_info", "id", ids, ct);
    }

    /// <summary>
    /// Shared batched mark-sent: ONE UPDATE ... WHERE id IN (...) per chunk instead of one
    /// UPDATE per row (the old loop ran 50k individual statements when draining a big
    /// backlog). Chunks of 400 stay safely under SQLite's SQLITE_MAX_VARIABLE_NUMBER.
    /// </summary>
    private async Task MarkSentCoreAsync(string table, string idColumn, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            foreach (var chunk in ids.Chunk(400))
            {
                var inClause = string.Join(",", chunk.Select((_, i) => $"$id{i}"));
                var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    $"UPDATE {table} SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now') WHERE {idColumn} IN ({inClause})";
                for (int i = 0; i < chunk.Length; i++)
                    cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }


    // ────────────────────────────────────────
    // Installed Applications
    // ────────────────────────────────────────

    public async Task StoreInstalledApplicationsAsync(IReadOnlyList<InstalledApplication> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            // Baseline scan: when no open (currently-installed) rows exist yet, this is the
            // tracker's FIRST inventory scan — everything it finds was already installed, so
            // install_date stays NULL ("Unknown"). On later scans a name without an open row
            // is a NEW install (or reinstall) and gets the current time stamped on it.
            var baseline = !await HasOpenAppRowsCoreAsync(ct);
            var nowIso = DateTime.UtcNow.ToString("O");

            var updCmd = _connection.CreateCommand();
            updCmd.CommandText = @"
                UPDATE installed_applications
                SET binary_name = COALESCE(NULLIF($binary_name, ''), binary_name),
                    app_version = $app_version,
                    publisher = COALESCE(NULLIF($publisher, ''), publisher),
                    install_path = COALESCE(NULLIF($install_path, ''), install_path),
                    change_type = CASE WHEN change_type = 'installed' THEN 'installed' ELSE $change_type END,
                    is_browser = MAX(is_browser, $is_browser),
                    desktop_id = COALESCE(NULLIF($desktop_id, ''), desktop_id),
                    categories = COALESCE(NULLIF($categories, ''), categories),
                    detected_at = $detected_at,
                    is_synced = 0
                WHERE app_name = $app_name AND uninstall_date IS NULL";
            var pNameU = updCmd.Parameters.Add("$app_name", SqliteType.Text);
            var pBinaryU = updCmd.Parameters.Add("$binary_name", SqliteType.Text);
            var pVerU = updCmd.Parameters.Add("$app_version", SqliteType.Text);
            var pPubU = updCmd.Parameters.Add("$publisher", SqliteType.Text);
            var pPathU = updCmd.Parameters.Add("$install_path", SqliteType.Text);
            var pChangeU = updCmd.Parameters.Add("$change_type", SqliteType.Text);
            var pBrowserU = updCmd.Parameters.Add("$is_browser", SqliteType.Integer);
            var pDesktopU = updCmd.Parameters.Add("$desktop_id", SqliteType.Text);
            var pCatsU = updCmd.Parameters.Add("$categories", SqliteType.Text);
            var pDetectedU = updCmd.Parameters.Add("$detected_at", SqliteType.Text);

            var insCmd = _connection.CreateCommand();
            insCmd.CommandText = DatabaseSchema.InsertInstalledApplicationSql;
            var pId = insCmd.Parameters.Add("$id", SqliteType.Text);
            var pName = insCmd.Parameters.Add("$app_name", SqliteType.Text);
            var pBinary = insCmd.Parameters.Add("$binary_name", SqliteType.Text);
            var pVer = insCmd.Parameters.Add("$app_version", SqliteType.Text);
            var pPub = insCmd.Parameters.Add("$publisher", SqliteType.Text);
            var pPath = insCmd.Parameters.Add("$install_path", SqliteType.Text);
            var pDate = insCmd.Parameters.Add("$install_date", SqliteType.Text);
            var pUninst = insCmd.Parameters.Add("$uninstall_string", SqliteType.Text);
            var pChange = insCmd.Parameters.Add("$change_type", SqliteType.Text);
            var pIsInstalled = insCmd.Parameters.Add("$is_installed", SqliteType.Integer);
            var pUninstallDate = insCmd.Parameters.Add("$uninstall_date", SqliteType.Text);
            var pDetected = insCmd.Parameters.Add("$detected_at", SqliteType.Text);
            var pIsBrowser = insCmd.Parameters.Add("$is_browser", SqliteType.Integer);
            var pDesktopId = insCmd.Parameters.Add("$desktop_id", SqliteType.Text);
            var pCategories = insCmd.Parameters.Add("$categories", SqliteType.Text);

            var findCmd = _connection.CreateCommand();
            findCmd.CommandText = "SELECT id FROM installed_applications WHERE app_name = $app_name AND uninstall_date IS NULL LIMIT 1";
            var pFindName = findCmd.Parameters.Add("$app_name", SqliteType.Text);

            await using var tx = await _connection.BeginTransactionAsync(ct);
            ((DbCommand)updCmd).Transaction = tx;
            ((DbCommand)insCmd).Transaction = tx;
            ((DbCommand)findCmd).Transaction = tx;

            foreach (var e in entries)
            {
                pFindName.Value = e.AppName;
                var openId = await findCmd.ExecuteScalarAsync(ct) as string;

                if (openId != null)
                {
                    // Open cycle exists — refresh metadata in place, keep install_date.
                    pNameU.Value = e.AppName;
                    pBinaryU.Value = e.BinaryName;
                    pVerU.Value = e.AppVersion;
                    pPubU.Value = e.Publisher;
                    pPathU.Value = e.InstallPath;
                    pChangeU.Value = e.ChangeType;
                    pBrowserU.Value = e.IsBrowser ? 1 : 0;
                    pDesktopU.Value = e.DesktopId;
                    pCatsU.Value = e.Categories;
                    pDetectedU.Value = e.DetectedAt.ToString("O");
                    await updCmd.ExecuteNonQueryAsync(ct);
                    continue;
                }

                // New install / reinstall → open a NEW cycle row. install_date = the OS
                // reported date if any; on the baseline scan (first ever) NULL (unknown);
                // otherwise the detection time (this is a brand-new install).
                pId.Value = e.Id;
                pName.Value = e.AppName;
                pBinary.Value = e.BinaryName;
                pVer.Value = e.AppVersion;
                pPub.Value = e.Publisher;
                pPath.Value = e.InstallPath;
                pDate.Value = e.InstallDate?.ToString("O")
                    ?? (baseline ? (object)DBNull.Value : nowIso);
                pUninst.Value = e.UninstallString;
                pChange.Value = e.ChangeType;
                pIsInstalled.Value = 1;
                pUninstallDate.Value = DBNull.Value;
                pDetected.Value = e.DetectedAt.ToString("O");
                pIsBrowser.Value = e.IsBrowser ? 1 : 0;
                pDesktopId.Value = e.DesktopId;
                pCategories.Value = e.Categories;
                await insCmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>Ungated: any currently-installed app cycle exists (baseline detection).</summary>
    private async Task<bool> HasOpenAppRowsCoreAsync(CancellationToken ct)
    {
        if (_connection == null) return false;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM installed_applications WHERE uninstall_date IS NULL";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    public async Task<IReadOnlyList<InstalledApplication>> GetUnsentInstalledApplicationsAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<InstalledApplication>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM installed_applications WHERE is_synced = 0 ORDER BY detected_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<InstalledApplication>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapInstalledAppReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkInstalledApplicationsSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("installed_applications", "id", ids, ct);
    }

    /// <summary>Every installed_applications row — the full inventory the Installed Applications page renders.</summary>
    public async Task<IReadOnlyList<InstalledApplication>> GetAllInstalledAppsAsync(CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<InstalledApplication>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM installed_applications ORDER BY app_name COLLATE NOCASE ASC";
            var results = new List<InstalledApplication>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapInstalledAppReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    // ────────────────────────────────────────
    // Installed Packages
    // ────────────────────────────────────────

    public async Task StoreInstalledPackagesAsync(IReadOnlyList<InstalledPackage> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            // Baseline scan: first inventory scan → everything found was already installed →
            // install_date NULL (unknown). Later scans: a package without an open cycle row is
            // a NEW install (or reinstall) and gets the current time stamped on it.
            var baseline = !await HasOpenPackageRowsCoreAsync(ct);
            var nowIso = DateTime.UtcNow.ToString("O");

            var updCmd = _connection.CreateCommand();
            updCmd.CommandText = @"
                UPDATE installed_packages
                SET version = $version,
                    category = CASE WHEN category = 'tool' THEN $category ELSE category END,
                    install_path = COALESCE(NULLIF($install_path, ''), install_path),
                    publisher = COALESCE(NULLIF($publisher, ''), publisher),
                    description = COALESCE(NULLIF($description, ''), description),
                    detected_at = $detected_at,
                    is_synced = 0
                WHERE package_name = $package_name AND source_manager = $source_manager AND uninstall_date IS NULL";
            var pNameU = updCmd.Parameters.Add("$package_name", SqliteType.Text);
            var pSrcU = updCmd.Parameters.Add("$source_manager", SqliteType.Text);
            var pVerU = updCmd.Parameters.Add("$version", SqliteType.Text);
            var pCatU = updCmd.Parameters.Add("$category", SqliteType.Text);
            var pPathU = updCmd.Parameters.Add("$install_path", SqliteType.Text);
            var pPubU = updCmd.Parameters.Add("$publisher", SqliteType.Text);
            var pDescU = updCmd.Parameters.Add("$description", SqliteType.Text);
            var pDetectedU = updCmd.Parameters.Add("$detected_at", SqliteType.Text);

            var insCmd = _connection.CreateCommand();
            insCmd.CommandText = DatabaseSchema.InsertInstalledPackageSql;
            var pId = insCmd.Parameters.Add("$id", SqliteType.Text);
            var pName = insCmd.Parameters.Add("$package_name", SqliteType.Text);
            var pVer = insCmd.Parameters.Add("$version", SqliteType.Text);
            var pCat = insCmd.Parameters.Add("$category", SqliteType.Text);
            var pSrc = insCmd.Parameters.Add("$source_manager", SqliteType.Text);
            var pPath = insCmd.Parameters.Add("$install_path", SqliteType.Text);
            var pPub = insCmd.Parameters.Add("$publisher", SqliteType.Text);
            var pDesc = insCmd.Parameters.Add("$description", SqliteType.Text);
            var pInstallDate = insCmd.Parameters.Add("$install_date", SqliteType.Text);
            var pIsInstalled = insCmd.Parameters.Add("$is_installed", SqliteType.Integer);
            var pUninstallDate = insCmd.Parameters.Add("$uninstall_date", SqliteType.Text);
            var pDetected = insCmd.Parameters.Add("$detected_at", SqliteType.Text);

            var findCmd = _connection.CreateCommand();
            findCmd.CommandText = @"
                SELECT id FROM installed_packages
                WHERE package_name = $package_name AND source_manager = $source_manager AND uninstall_date IS NULL
                LIMIT 1";
            var pFindName = findCmd.Parameters.Add("$package_name", SqliteType.Text);
            var pFindSrc = findCmd.Parameters.Add("$source_manager", SqliteType.Text);

            await using var tx = await _connection.BeginTransactionAsync(ct);
            ((DbCommand)updCmd).Transaction = tx;
            ((DbCommand)insCmd).Transaction = tx;
            ((DbCommand)findCmd).Transaction = tx;

            foreach (var e in entries)
            {
                pFindName.Value = e.PackageName;
                pFindSrc.Value = e.SourceManager;
                var openId = await findCmd.ExecuteScalarAsync(ct) as string;

                if (openId != null)
                {
                    // Open cycle exists — refresh metadata in place, keep install_date.
                    pNameU.Value = e.PackageName;
                    pSrcU.Value = e.SourceManager;
                    pVerU.Value = e.Version;
                    pCatU.Value = e.Category;
                    pPathU.Value = e.InstallPath;
                    pPubU.Value = e.Publisher;
                    pDescU.Value = e.Description;
                    pDetectedU.Value = e.DetectedAt.ToString("O");
                    await updCmd.ExecuteNonQueryAsync(ct);
                    continue;
                }

                // New install / reinstall → open a NEW cycle row (see apps store for semantics).
                pId.Value = e.Id;
                pName.Value = e.PackageName;
                pVer.Value = e.Version;
                pCat.Value = e.Category;
                pSrc.Value = e.SourceManager;
                pPath.Value = e.InstallPath;
                pPub.Value = e.Publisher;
                pDesc.Value = e.Description;
                pInstallDate.Value = e.InstallDate?.ToString("O")
                    ?? (baseline ? (object)DBNull.Value : nowIso);
                pIsInstalled.Value = 1;
                pUninstallDate.Value = DBNull.Value;
                pDetected.Value = e.DetectedAt.ToString("O");
                await insCmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>Ungated: any currently-installed package cycle exists (baseline detection).</summary>
    private async Task<bool> HasOpenPackageRowsCoreAsync(CancellationToken ct)
    {
        if (_connection == null) return false;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM installed_packages WHERE uninstall_date IS NULL";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    public async Task<IReadOnlyList<InstalledPackage>> GetUnsentInstalledPackagesAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<InstalledPackage>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM installed_packages WHERE is_synced = 0 ORDER BY detected_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<InstalledPackage>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapInstalledPackageReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkInstalledPackagesSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("installed_packages", "id", ids, ct);
    }

    /// <summary>Every installed_packages row — the full inventory the Installed Applications page renders.</summary>
    public async Task<IReadOnlyList<InstalledPackage>> GetAllInstalledPackagesAsync(CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<InstalledPackage>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM installed_packages ORDER BY package_name COLLATE NOCASE ASC";
            var results = new List<InstalledPackage>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapInstalledPackageReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    // ────────────────────────────────────────
    // Inventory Lifecycle (install / uninstall / reinstall tracking)
    // ────────────────────────────────────────

    /// <summary>
    /// Close install cycles that are no longer present in a completed OS scan (rows-per-cycle
    /// model). Every OPEN row (uninstall_date IS NULL) whose key is missing from the scan gets
    /// closed: uninstall_date=now, is_installed=0 — that row is the finished install cycle and
    /// stays as history. A reinstall is NOT handled here: the next scan's Store* method opens a
    /// brand-new cycle row (with the new install_date). A pass is skipped when its seen-set is
    /// empty, and the 50% confidence guard skips closing when a scan looks partial (returned
    /// fewer than half the open rows) — a failed scan must not uninstall everything.
    /// </summary>
    public async Task ApplyInventoryLifecycleAsync(
        IReadOnlySet<string> seenAppNames,
        IReadOnlySet<string> seenPackageKeys,
        DateTime now,
        CancellationToken ct)
    {
        if (_connection == null) return;
        if (seenAppNames.Count == 0 && seenPackageKeys.Count == 0) return;

        await _connectionGate.WaitAsync(ct);
        try
        {
            await using var tx = await _connection.BeginTransactionAsync(ct);
            var nowIso = now.ToString("O");

            if (seenAppNames.Count > 0)
            {
                // Open cycles currently recorded.
                var openCmd = _connection.CreateCommand();
                openCmd.CommandText = "SELECT app_name FROM installed_applications WHERE uninstall_date IS NULL";
                ((DbCommand)openCmd).Transaction = tx;
                var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var r = await openCmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct)) open.Add(r.GetString(0));

                // Confidence guard: a partial scan (one .desktop/registry walk failed) must
                // not close the cycles its failed source would have reported.
                if (seenAppNames.Count >= open.Count * 0.5)
                {
                    foreach (var name in open)
                    {
                        if (seenAppNames.Contains(name)) continue;
                        var upd = _connection.CreateCommand();
                        upd.CommandText = @"
                            UPDATE installed_applications
                            SET is_installed = 0, uninstall_date = $at, is_synced = 0
                            WHERE app_name = $name AND uninstall_date IS NULL";
                        upd.Parameters.AddWithValue("$name", name);
                        upd.Parameters.AddWithValue("$at", nowIso);
                        await upd.ExecuteNonQueryAsync(ct);
                    }
                }
            }

            if (seenPackageKeys.Count > 0)
            {
                // Same close pass for packages, keyed on package_name|source_manager.
                var openCmd = _connection.CreateCommand();
                openCmd.CommandText = "SELECT package_name, source_manager FROM installed_packages WHERE uninstall_date IS NULL";
                ((DbCommand)openCmd).Transaction = tx;
                var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var r = await openCmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        open.Add(r.GetString(0) + "|" + r.GetString(1));

                if (seenPackageKeys.Count >= open.Count * 0.5)
                {
                    foreach (var key in open)
                    {
                        if (seenPackageKeys.Contains(key)) continue;
                        var sep = key.IndexOf('|');
                        var upd = _connection.CreateCommand();
                        upd.CommandText = @"
                            UPDATE installed_packages
                            SET is_installed = 0, uninstall_date = $at, is_synced = 0
                            WHERE package_name = $name AND source_manager = $source AND uninstall_date IS NULL";
                        upd.Parameters.AddWithValue("$name", key[..sep]);
                        upd.Parameters.AddWithValue("$source", key[(sep + 1)..]);
                        upd.Parameters.AddWithValue("$at", nowIso);
                        await upd.ExecuteNonQueryAsync(ct);
                    }
                }
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    // ────────────────────────────────────────
    // Installed App/Package Lookup
    // ────────────────────────────────────────

    public async Task<InstalledApplication?> GetInstalledAppByBinaryNameAsync(string binaryName, CancellationToken ct)
    {
        if (_connection == null || string.IsNullOrWhiteSpace(binaryName)) return null;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            // Prefer the currently-installed cycle (uninstall_date IS NULL) over history rows.
            cmd.CommandText = @"SELECT * FROM installed_applications WHERE binary_name = $binary_name
                ORDER BY (uninstall_date IS NULL) DESC, detected_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$binary_name", binaryName);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return MapInstalledAppReader(reader);
            return null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<InstalledApplication?> GetInstalledAppByBinaryNameFuzzyAsync(string processName, CancellationToken ct)
    {
        if (_connection == null || string.IsNullOrWhiteSpace(processName)) return null;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            // AND/OR precedence fix: the app_name match must not escape the binary_name
            // guard (a bare `OR app_name LIKE` returned junk rows, e.g. the auto-registered
            // 'chrome' entry, before the real 'Google Chrome' row). Prefer browser rows and
            // the shortest binary name (closest match) as a deterministic tiebreak.
            cmd.CommandText = @"
                SELECT * FROM installed_applications
                WHERE (binary_name != ''
                       AND (binary_name LIKE '%' || $name || '%'
                            OR $name LIKE '%' || binary_name || '%'))
                   OR (binary_name != '' AND app_name LIKE '%' || $name || '%')
                ORDER BY (uninstall_date IS NULL) DESC, is_browser DESC, length(binary_name) ASC
                LIMIT 1";
            cmd.Parameters.AddWithValue("$name", processName);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return MapInstalledAppReader(reader);
            return null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<InstalledPackage?> GetInstalledPackageByNameAsync(string packageName, CancellationToken ct)
    {
        if (_connection == null || string.IsNullOrWhiteSpace(packageName)) return null;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            // Prefer the currently-installed cycle (uninstall_date IS NULL) over history rows.
            cmd.CommandText = @"SELECT * FROM installed_packages WHERE package_name = $package_name
                ORDER BY (uninstall_date IS NULL) DESC, detected_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$package_name", packageName);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return MapInstalledPackageReader(reader);
            return null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<HashSet<string>> GetAllInstalledAppBinaryNamesAsync(CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_connection == null) return result;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT binary_name FROM installed_applications WHERE binary_name != ''";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
            }
            return result;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<HashSet<string>> GetAllInstalledPackageNamesAsync(CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_connection == null) return result;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT package_name FROM installed_packages";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
            }
            return result;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<string> StoreInstalledAppAsync(InstalledApplication entry, CancellationToken ct)
    {
        if (_connection == null) return entry.Id;
        await _connectionGate.WaitAsync(ct);
        try
        {
            // Rows-per-cycle: an open cycle (uninstall_date IS NULL) is reused; otherwise a
            // NEW cycle row opens (a reinstall detected at runtime also opens a new row).
            var findCmd = _connection.CreateCommand();
            findCmd.CommandText = "SELECT id FROM installed_applications WHERE app_name = $app_name AND uninstall_date IS NULL LIMIT 1";
            findCmd.Parameters.AddWithValue("$app_name", entry.AppName);
            var openId = await findCmd.ExecuteScalarAsync(ct) as string;
            if (openId != null)
            {
                var upd = _connection.CreateCommand();
                upd.CommandText = @"
                    UPDATE installed_applications
                    SET binary_name = COALESCE(NULLIF($binary_name, ''), binary_name),
                        app_version = $app_version,
                        install_path = COALESCE(NULLIF($install_path, ''), install_path),
                        detected_at = $detected_at,
                        is_synced = 0
                    WHERE id = $id";
                upd.Parameters.AddWithValue("$id", openId);
                upd.Parameters.AddWithValue("$binary_name", entry.BinaryName);
                upd.Parameters.AddWithValue("$app_version", entry.AppVersion);
                upd.Parameters.AddWithValue("$install_path", entry.InstallPath);
                upd.Parameters.AddWithValue("$detected_at", entry.DetectedAt.ToString("O"));
                await upd.ExecuteNonQueryAsync(ct);
                return openId;
            }

            var baseline = !await HasOpenAppRowsCoreAsync(ct);
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.InsertInstalledApplicationSql;
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.Parameters.AddWithValue("$app_name", entry.AppName);
            cmd.Parameters.AddWithValue("$binary_name", entry.BinaryName);
            cmd.Parameters.AddWithValue("$app_version", entry.AppVersion);
            cmd.Parameters.AddWithValue("$publisher", entry.Publisher);
            cmd.Parameters.AddWithValue("$install_path", entry.InstallPath);
            cmd.Parameters.AddWithValue("$install_date", entry.InstallDate?.ToString("O")
                ?? (baseline ? (object)DBNull.Value : DateTime.UtcNow.ToString("O")));
            cmd.Parameters.AddWithValue("$uninstall_string", entry.UninstallString);
            cmd.Parameters.AddWithValue("$change_type", entry.ChangeType);
            cmd.Parameters.AddWithValue("$is_installed", 1);
            cmd.Parameters.AddWithValue("$uninstall_date", DBNull.Value);
            cmd.Parameters.AddWithValue("$is_browser", entry.IsBrowser ? 1 : 0);
            cmd.Parameters.AddWithValue("$desktop_id", entry.DesktopId);
            cmd.Parameters.AddWithValue("$categories", entry.Categories);
            cmd.Parameters.AddWithValue("$detected_at", entry.DetectedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
            return entry.Id;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task DeleteInstalledAppAsync(string id, CancellationToken ct)
    {
        if (_connection == null) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM installed_applications WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<InstalledApplication>> GetInstalledAppsWithEmptyDesktopIdAsync(CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<InstalledApplication>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM installed_applications WHERE desktop_id IS NULL OR desktop_id = ''";
            var results = new List<InstalledApplication>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapInstalledAppReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<string> StoreInstalledPackageAsync(InstalledPackage entry, CancellationToken ct)
    {
        if (_connection == null) return entry.Id;
        await _connectionGate.WaitAsync(ct);
        try
        {
            // Rows-per-cycle: reuse the open cycle, else open a NEW one.
            var findCmd = _connection.CreateCommand();
            findCmd.CommandText = @"
                SELECT id FROM installed_packages
                WHERE package_name = $package_name AND source_manager = $source_manager AND uninstall_date IS NULL
                LIMIT 1";
            findCmd.Parameters.AddWithValue("$package_name", entry.PackageName);
            findCmd.Parameters.AddWithValue("$source_manager", entry.SourceManager);
            var openId = await findCmd.ExecuteScalarAsync(ct) as string;
            if (openId != null)
            {
                var upd = _connection.CreateCommand();
                upd.CommandText = @"
                    UPDATE installed_packages
                    SET version = $version, detected_at = $detected_at, is_synced = 0
                    WHERE id = $id";
                upd.Parameters.AddWithValue("$id", openId);
                upd.Parameters.AddWithValue("$version", entry.Version);
                upd.Parameters.AddWithValue("$detected_at", entry.DetectedAt.ToString("O"));
                await upd.ExecuteNonQueryAsync(ct);
                return openId;
            }

            var baseline = !await HasOpenPackageRowsCoreAsync(ct);
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.InsertInstalledPackageSql;
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.Parameters.AddWithValue("$package_name", entry.PackageName);
            cmd.Parameters.AddWithValue("$version", entry.Version);
            cmd.Parameters.AddWithValue("$category", entry.Category);
            cmd.Parameters.AddWithValue("$source_manager", entry.SourceManager);
            cmd.Parameters.AddWithValue("$install_path", entry.InstallPath);
            cmd.Parameters.AddWithValue("$publisher", entry.Publisher);
            cmd.Parameters.AddWithValue("$description", entry.Description);
            cmd.Parameters.AddWithValue("$install_date", entry.InstallDate?.ToString("O")
                ?? (baseline ? (object)DBNull.Value : DateTime.UtcNow.ToString("O")));
            cmd.Parameters.AddWithValue("$is_installed", 1);
            cmd.Parameters.AddWithValue("$uninstall_date", DBNull.Value);
            cmd.Parameters.AddWithValue("$detected_at", entry.DetectedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
            return entry.Id;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    // ────────────────────────────────────────
    // Network Info
    // ────────────────────────────────────────

    public async Task StoreNetworkInfoAsync(IReadOnlyList<NetworkInfo> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.InsertNetworkInfoSql;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pPubIp = cmd.Parameters.Add("$public_ip", SqliteType.Text);
            var pPrivIp = cmd.Parameters.Add("$private_ip", SqliteType.Text);
            var pIfName = cmd.Parameters.Add("$network_interface_name", SqliteType.Text);
            var pCollected = cmd.Parameters.Add("$collected_at", SqliteType.Text);
            var pFirstSeen = cmd.Parameters.Add("$first_seen_at", SqliteType.Text);
            var pLastSeen = cmd.Parameters.Add("$last_seen_at", SqliteType.Text);
            var pIsCurrent = cmd.Parameters.Add("$is_current", SqliteType.Integer);

            await using var tx = await _connection.BeginTransactionAsync(ct);
            ((DbCommand)cmd).Transaction = tx;
            foreach (var e in entries)
            {
                pId.Value = e.Id;
                pPubIp.Value = e.PublicIp;
                pPrivIp.Value = e.PrivateIp;
                pIfName.Value = e.NetworkInterfaceName;
                pCollected.Value = e.CollectedAt.ToString("O");
                pFirstSeen.Value = e.FirstSeenAt.HasValue ? e.FirstSeenAt.Value.ToString("O") : (object)DBNull.Value;
                pLastSeen.Value = e.LastSeenAt.HasValue ? e.LastSeenAt.Value.ToString("O") : (object)DBNull.Value;
                pIsCurrent.Value = e.IsCurrent ? 1 : 0;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<NetworkInfo>> GetUnsentNetworkInfoAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<NetworkInfo>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM network_info WHERE is_synced = 0 ORDER BY collected_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<NetworkInfo>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapNetworkInfoReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkNetworkInfoSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("network_info", "id", ids, ct);
    }

    public async Task<NetworkInfo?> GetLastNetworkInfoAsync(CancellationToken ct)
    {
        if (_connection == null) return null;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.GetLastNetworkInfoSql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) return MapNetworkInfoReader(reader);
            return null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkNetworkInfoNotCurrentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            await using var tx = await _connection.BeginTransactionAsync(ct);
            var cmd = _connection.CreateCommand();
            // is_synced = 0: an already-synced row must re-sync so the server never
            // shows a superseded network row as current (user rule 2026-08-12).
            cmd.CommandText = "UPDATE network_info SET is_current = 0, is_synced = 0 WHERE id = $id";
            var p = cmd.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in ids) { p.Value = id; await cmd.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task TouchNetworkInfoAsync(string id, DateTime lastSeenAt, CancellationToken ct)
    {
        if (_connection == null) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE network_info SET last_seen_at = $last_seen, is_synced = 0 WHERE id = $id";
            cmd.Parameters.AddWithValue("$last_seen", lastSeenAt.ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkAllNetworkInfoNotCurrentAsync(CancellationToken ct)
    {
        if (_connection == null) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            // is_synced = 0: every demoted row re-syncs once so the server can never
            // show a superseded row as current (user rule 2026-08-12).
            cmd.CommandText = "UPDATE network_info SET is_current = 0, is_synced = 0 WHERE is_current = 1";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task TouchCurrentNetworkInfoAsync(DateTime lastSeenAt, CancellationToken ct)
    {
        if (_connection == null) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE network_info SET last_seen_at = $last_seen, is_synced = 0 WHERE is_current = 1";
            cmd.Parameters.AddWithValue("$last_seen", lastSeenAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    // ────────────────────────────────────────
    // Session Events
    // ────────────────────────────────────────

    public async Task StoreSessionEventsAsync(IReadOnlyList<SessionEvent> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.InsertSessionEventSql;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pType = cmd.Parameters.Add("$event_type", SqliteType.Text);
            var pUser = cmd.Parameters.Add("$os_username", SqliteType.Text);
            var pEventAt = cmd.Parameters.Add("$event_at", SqliteType.Text);
            var pCount = cmd.Parameters.Add("$event_count", SqliteType.Integer);
            var pFirstAt = cmd.Parameters.Add("$first_at", SqliteType.Text);
            var pLastAt = cmd.Parameters.Add("$last_at", SqliteType.Text);

            await using var tx = await _connection.BeginTransactionAsync(ct);
            ((DbCommand)cmd).Transaction = tx;
            foreach (var e in entries)
            {
                pId.Value = e.Id;
                pType.Value = e.EventType;
                pUser.Value = e.OsUsername;
                pEventAt.Value = e.EventAt.ToString("O");
                pCount.Value = e.EventCount.HasValue ? e.EventCount.Value : DBNull.Value;
                pFirstAt.Value = e.FirstAt.HasValue ? e.FirstAt.Value.ToString("O") : DBNull.Value;
                pLastAt.Value = e.LastAt.HasValue ? e.LastAt.Value.ToString("O") : DBNull.Value;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<SessionEvent>> GetUnsentSessionEventsAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<SessionEvent>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM session_events WHERE is_synced = 0 ORDER BY event_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<SessionEvent>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapSessionEventReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkSessionEventsSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("session_events", "id", ids, ct);
    }

    public async Task<int> CountUnsentSessionEventsAsync(CancellationToken ct)
    {
        if (_connection == null) return 0;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM session_events WHERE is_synced = 0";
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<int> RollupExcessUnsentSessionEventsAsync(int maxRows, CancellationToken ct)
    {
        if (_connection == null || maxRows < 1) return 0;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var countCmd = _connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM session_events WHERE is_synced = 0";
            var unsynced = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
            if (unsynced <= maxRows) return 0;

            var excess = unsynced - maxRows;
            var fetchCmd = _connection.CreateCommand();
            fetchCmd.CommandText = @"SELECT * FROM session_events
                WHERE is_synced = 0
                ORDER BY event_at ASC
                LIMIT $limit";
            fetchCmd.Parameters.AddWithValue("$limit", excess);

            var toRoll = new List<SessionEvent>();
            await using (var reader = await fetchCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    toRoll.Add(MapSessionEventReader(reader));
            }

            if (toRoll.Count == 0) return 0;

            var firstAt = toRoll.Min(e => e.EventAt);
            var lastAt = toRoll.Max(e => e.EventAt);
            var sentinel = new SessionEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                EventType = SessionEventTypes.OldDataDropped,
                OsUsername = toRoll[0].OsUsername,
                EventAt = firstAt,
                EventCount = toRoll.Count,
                FirstAt = firstAt,
                LastAt = lastAt,
            };

            await using var tx = await _connection.BeginTransactionAsync(ct);

            var deleteCmd = _connection.CreateCommand();
            ((DbCommand)deleteCmd).Transaction = tx;
            deleteCmd.CommandText = $"DELETE FROM session_events WHERE id IN ({string.Join(",", toRoll.Select((_, i) => $"$id{i}"))})";
            for (var i = 0; i < toRoll.Count; i++)
                deleteCmd.Parameters.AddWithValue($"$id{i}", toRoll[i].Id);
            await deleteCmd.ExecuteNonQueryAsync(ct);

            var insertCmd = _connection.CreateCommand();
            ((DbCommand)insertCmd).Transaction = tx;
            insertCmd.CommandText = DatabaseSchema.InsertSessionEventSql;
            insertCmd.Parameters.AddWithValue("$id", sentinel.Id);
            insertCmd.Parameters.AddWithValue("$event_type", sentinel.EventType);
            insertCmd.Parameters.AddWithValue("$os_username", sentinel.OsUsername);
            insertCmd.Parameters.AddWithValue("$event_at", sentinel.EventAt.ToString("O"));
            insertCmd.Parameters.AddWithValue("$event_count", sentinel.EventCount!.Value);
            insertCmd.Parameters.AddWithValue("$first_at", sentinel.FirstAt!.Value.ToString("O"));
            insertCmd.Parameters.AddWithValue("$last_at", sentinel.LastAt!.Value.ToString("O"));
            await insertCmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);

            return toRoll.Count;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<SessionEvent?> GetLastSessionEventAsync(CancellationToken ct)
    {
        if (_connection == null) return null;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM session_events ORDER BY event_at DESC, created_at DESC LIMIT 1";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) return MapSessionEventReader(reader);
            return null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Time and Attendance CRUD (Phase 1, finalplan section 2.2)
    // Schedule + holidays are PURE MIRROR data from the server (ScheduleCacheService
    // pulls them every 6h in A.6). daily_attendance_cache + local_time_skew are
    // CLIENT-OWNED: written by AttendanceAggregator (A.8) and LocalTimeSkewService
    // (A.7) respectively. None of these are sent to the server in Phase 1.
    // ════════════════════════════════════════════════════════════════════════

    public async Task UpsertEmployeeScheduleAsync(
        string employeeId, string timezone, string weeklyPatternJson,
        int graceMinutes, string? validFrom, string? validTo, string? serverId,
        CancellationToken ct)
    {
        if (_connection == null) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO employee_schedule
                    (employee_id, timezone, weekly_pattern, grace_minutes,
                     valid_from, valid_to, server_id, is_synced, synced_at, updated_at)
                VALUES
                    ($eid, $tz, $pattern, $grace, $vfrom, $vto, $sid, 1,
                     strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'),
                     strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
                ON CONFLICT(employee_id) DO UPDATE SET
                    timezone = excluded.timezone,
                    weekly_pattern = excluded.weekly_pattern,
                    grace_minutes = excluded.grace_minutes,
                    valid_from = excluded.valid_from,
                    valid_to = excluded.valid_to,
                    server_id = excluded.server_id,
                    is_synced = 1,
                    synced_at = excluded.synced_at,
                    updated_at = excluded.updated_at
            ";
            cmd.Parameters.AddWithValue("$eid", employeeId);
            cmd.Parameters.AddWithValue("$tz", timezone);
            cmd.Parameters.AddWithValue("$pattern", weeklyPatternJson);
            cmd.Parameters.AddWithValue("$grace", graceMinutes);
            cmd.Parameters.AddWithValue("$vfrom", (object?)validFrom ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$vto", (object?)validTo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sid", (object?)serverId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<(string EmployeeId, string Timezone, string WeeklyPattern, int GraceMinutes)>>
        ListEmployeeSchedulesAsync(CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<(string, string, string, int)>();
        // Use the READ connection - this is a pure read, doesn't need to serialize
        // with writers. The reader callback owns its own DataReader lifetime.
        return await WithReadConnectionAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT employee_id, timezone, weekly_pattern, grace_minutes FROM employee_schedule";
            var results = new List<(string, string, string, int)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3)));
            }
            return (IReadOnlyList<(string, string, string, int)>)results;
        }, ct);
    }

    public async Task UpsertCompanyHolidayAsync(string date, string label, string? serverId, CancellationToken ct)
    {
        if (_connection == null) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO company_holidays
                    (holiday_date, label, server_id, is_synced, synced_at)
                VALUES
                    ($date, $label, $sid, 1, strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
                ON CONFLICT(holiday_date) DO UPDATE SET
                    label = excluded.label,
                    server_id = excluded.server_id,
                    is_synced = 1,
                    synced_at = excluded.synced_at
            ";
            cmd.Parameters.AddWithValue("$date", date);
            cmd.Parameters.AddWithValue("$label", label);
            cmd.Parameters.AddWithValue("$sid", (object?)serverId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<(string Date, string Label)>> ListCompanyHolidaysAsync(CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<(string, string)>();
        return await WithReadConnectionAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT holiday_date, label FROM company_holidays";
            var results = new List<(string, string)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add((reader.GetString(0), reader.GetString(1)));
            }
            return (IReadOnlyList<(string, string)>)results;
        }, ct);
    }

    public async Task UpsertDailyAttendanceAsync(
        string employeeId, string workDate, DateTime? firstActiveAt, DateTime? lastActiveAt,
        int activeSeconds, int idleSeconds, int offShiftSeconds, string status, int lateMinutes,
        CancellationToken ct)
    {
        if (_connection == null) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO daily_attendance_cache
                    (employee_id, machine_id, work_date, first_active_at, last_active_at,
                     active_seconds, idle_seconds, off_shift_seconds, status, late_minutes, updated_at)
                VALUES
                    ($eid, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $date, $fa, $la, $a, $i, $o, $st, $lm,
                     strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
                ON CONFLICT(employee_id, work_date) DO UPDATE SET
                    machine_id = COALESCE(NULLIF(excluded.machine_id, ''), daily_attendance_cache.machine_id),
                    first_active_at = COALESCE(NULLIF(excluded.first_active_at, ''), daily_attendance_cache.first_active_at),
                    last_active_at = excluded.last_active_at,
                    active_seconds = excluded.active_seconds,
                    idle_seconds = excluded.idle_seconds,
                    off_shift_seconds = excluded.off_shift_seconds,
                    status = excluded.status,
                    late_minutes = excluded.late_minutes,
                    updated_at = excluded.updated_at
            ";
            cmd.Parameters.AddWithValue("$eid", employeeId);
            cmd.Parameters.AddWithValue("$date", workDate);
            cmd.Parameters.AddWithValue("$fa", firstActiveAt?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$la", lastActiveAt?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$a", activeSeconds);
            cmd.Parameters.AddWithValue("$i", idleSeconds);
            cmd.Parameters.AddWithValue("$o", offShiftSeconds);
            cmd.Parameters.AddWithValue("$st", status);
            cmd.Parameters.AddWithValue("$lm", lateMinutes);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<(int ActiveSeconds, int IdleSeconds, int OffShiftSeconds, DateTime? FirstActiveAt)?>
        GetDailyAttendanceAsync(string employeeId, string workDate, CancellationToken ct)
    {
        if (_connection == null) return null;
        return await WithReadConnectionAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT active_seconds, idle_seconds, off_shift_seconds, first_active_at
                                FROM daily_attendance_cache
                                WHERE employee_id = $eid AND work_date = $date";
            cmd.Parameters.AddWithValue("$eid", employeeId);
            cmd.Parameters.AddWithValue("$date", workDate);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            DateTime? fa = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
            return ((int, int, int, DateTime?)?)(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), fa);
        }, ct);
    }

    public async Task UpsertTimeSkewAsync(string serverUrl, DateTime measuredAt, double skewSeconds, CancellationToken ct)
    {
        if (_connection == null) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO local_time_skew (server_url, last_measured_at, skew_seconds)
                VALUES ($url, $at, $skew)
                ON CONFLICT(server_url) DO UPDATE SET
                    last_measured_at = excluded.last_measured_at,
                    skew_seconds = excluded.skew_seconds
            ";
            cmd.Parameters.AddWithValue("$url", serverUrl);
            cmd.Parameters.AddWithValue("$at", measuredAt.ToString("O"));
            cmd.Parameters.AddWithValue("$skew", skewSeconds);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<(DateTime MeasuredAt, double SkewSeconds)?> GetLatestTimeSkewAsync(string serverUrl, CancellationToken ct)
    {
        if (_connection == null) return null;
        return await WithReadConnectionAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT last_measured_at, skew_seconds FROM local_time_skew WHERE server_url = $url";
            cmd.Parameters.AddWithValue("$url", serverUrl);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var at = DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind);
            return ((DateTime, double)?)(at, reader.GetDouble(1));
        }, ct);
    }

    /// <summary>
    /// Time and Attendance (finalplan section 2.5 + section 5 S1): read session_events
    /// in a [from, to) window. Used by AttendanceAggregator (A.8) for the daily
    /// window read. Uses the read-only connection - non-blocking with writers.
    /// </summary>
    public async Task<IReadOnlyList<SessionEvent>> GetSessionEventsInRangeAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<SessionEvent>();
        return await WithReadConnectionAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT * FROM session_events
                                WHERE event_at >= $from AND event_at < $to
                                ORDER BY event_at ASC";
            cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));
            var results = new List<SessionEvent>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapSessionEventReader(reader));
            return (IReadOnlyList<SessionEvent>)results;
        }, ct);
    }

    // ────────────────────────────────────────
    // App Sessions
    // ────────────────────────────────────────

    public async Task StoreAppSessionsAsync(IReadOnlyList<AppSession> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var closeSessions = entries.Where(e => e.EndedAt.HasValue &&
                string.IsNullOrWhiteSpace(e.ProcessName)).ToList();
            var newSessions = entries.Where(e => !closeSessions.Contains(e)).ToList();

            await using var tx = await _connection.BeginTransactionAsync(ct);

            if (closeSessions.Count > 0)
            {
                var updateCmd = _connection.CreateCommand();
                updateCmd.CommandText = DatabaseSchema.UpdateAppSessionEndedSql;
                ((DbCommand)updateCmd).Transaction = tx;
                var pId = updateCmd.Parameters.Add("$id", SqliteType.Text);
                var pEnd = updateCmd.Parameters.Add("$ended_at", SqliteType.Text);
                var pFg = updateCmd.Parameters.Add("$foreground_seconds", SqliteType.Real);
                var pBg = updateCmd.Parameters.Add("$background_seconds", SqliteType.Real);
                foreach (var e in closeSessions)
                {
                    pId.Value = e.Id;
                    pEnd.Value = e.EndedAt!.Value.ToString("O");
                    // NULL keeps the last flushed value when this close path has no
                    // focus data (boot reconciliation, browser tracker closes).
                    pFg.Value = e.ForegroundSeconds.HasValue ? e.ForegroundSeconds.Value : (object)DBNull.Value;
                    pBg.Value = e.BackgroundSeconds.HasValue ? e.BackgroundSeconds.Value : (object)DBNull.Value;
                    await updateCmd.ExecuteNonQueryAsync(ct);
                }
            }

            if (newSessions.Count > 0)
            {
                var cmd = _connection.CreateCommand();
                cmd.CommandText = DatabaseSchema.InsertAppSessionSql;
                ((DbCommand)cmd).Transaction = tx;
                var pId = cmd.Parameters.Add("$id", SqliteType.Text);
                var pProc = cmd.Parameters.Add("$process_name", SqliteType.Text);
                var pDisp = cmd.Parameters.Add("$app_display_name", SqliteType.Text);
                var pStart = cmd.Parameters.Add("$started_at", SqliteType.Text);
                var pEnd = cmd.Parameters.Add("$ended_at", SqliteType.Text);
                var pMac = cmd.Parameters.Add("$machine_id", SqliteType.Text);
                var pEmpId = cmd.Parameters.Add("$employee_id", SqliteType.Text);
                var pEmpName = cmd.Parameters.Add("$employee_name", SqliteType.Text);
                var pSessId = cmd.Parameters.Add("$session_id", SqliteType.Text);
                var pPlat = cmd.Parameters.Add("$platform", SqliteType.Text);
                var pAppId = cmd.Parameters.Add("$installed_app_id", SqliteType.Text);
                var pPkgId = cmd.Parameters.Add("$installed_package_id", SqliteType.Text);
                var pPid = cmd.Parameters.Add("$process_id", SqliteType.Integer);
                var pPPid = cmd.Parameters.Add("$parent_process_id", SqliteType.Integer);
                var pGroupedBy = cmd.Parameters.Add("$grouped_by", SqliteType.Text);
                var pCgroupScope = cmd.Parameters.Add("$cgroup_scope", SqliteType.Text);
                var pContextLabel = cmd.Parameters.Add("$context_label", SqliteType.Text);
                var pFg = cmd.Parameters.Add("$foreground_seconds", SqliteType.Real);
                var pBg = cmd.Parameters.Add("$background_seconds", SqliteType.Real);
                var pLastActivity = cmd.Parameters.Add("$last_activity_at", SqliteType.Text);

                foreach (var e in newSessions)
                {
                    pId.Value = e.Id;
                    pProc.Value = e.ProcessName;
                    pDisp.Value = e.AppDisplayName;
                    pStart.Value = e.StartedAt.ToString("O");
                    pEnd.Value = e.EndedAt?.ToString("O") ?? (object)DBNull.Value;
                    pMac.Value = e.MachineId;
                    pEmpId.Value = (object?)e.EmployeeId ?? DBNull.Value;
                    pEmpName.Value = (object?)e.EmployeeName ?? DBNull.Value;
                    pSessId.Value = e.SessionId;
                    pPlat.Value = e.Platform;
                    pAppId.Value = (object?)e.InstalledAppId ?? DBNull.Value;
                    pPkgId.Value = (object?)e.InstalledPackageId ?? DBNull.Value;
                    pPid.Value = e.ProcessId.HasValue ? e.ProcessId.Value : DBNull.Value;
                    pPPid.Value = e.ParentProcessId.HasValue ? e.ParentProcessId.Value : DBNull.Value;
                    pGroupedBy.Value = (object?)e.GroupedBy ?? DBNull.Value;
                    pCgroupScope.Value = (object?)e.CgroupScope ?? DBNull.Value;
                    pContextLabel.Value = (object?)e.ContextLabel ?? DBNull.Value;
                    pFg.Value = e.ForegroundSeconds ?? 0;
                    pBg.Value = e.BackgroundSeconds ?? 0;
                    pLastActivity.Value = e.LastActivityAt?.ToString("O") ?? (object)DBNull.Value;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<AppSession>> GetUnsentAppSessionsAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<AppSession>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            // is_synced is the sole queue marker. A previously synced session
            // can become unsynced when it is closed, so it must be returned
            // even when ended_at and synced_at are both populated. Filtering
            // closed rows here strands the final close update permanently.
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM app_sessions
                 WHERE is_synced = 0
                 ORDER BY started_at ASC
                 LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<AppSession>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapAppSessionReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkAppSessionsSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("app_sessions", "id", ids, ct);
    }

    /// <summary>
    /// Reset specific sessions to is_synced=0 so they are re-sent on the next sync pass.
    /// Called when the server reports missing session IDs during the orphan preflight
    /// (Bug #9 follow-up — breaks the permanent orphan deadlock).
    /// </summary>
    public async Task MarkAppSessionsUnsyncedByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            foreach (var id in ids)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = "UPDATE app_sessions SET is_synced = 0, synced_at = NULL WHERE id = $id AND is_synced = 1";
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// Persist accumulated foreground/background focus durations for open sessions and
    /// re-queue each row (is_synced = 0) so SyncService re-sends it and the server learns
    /// the growing totals. One UPDATE per row in a single transaction.
    /// </summary>
    public async Task UpdateAppSessionFocusAsync(
        IReadOnlyList<(string Id, double ForegroundSeconds, double BackgroundSeconds)> updates,
        CancellationToken ct)
    {
        if (_connection == null || updates.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            await using var tx = await _connection.BeginTransactionAsync(ct);
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.UpdateAppSessionFocusSql;
            ((DbCommand)cmd).Transaction = tx;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pFg = cmd.Parameters.Add("$foreground_seconds", SqliteType.Real);
            var pBg = cmd.Parameters.Add("$background_seconds", SqliteType.Real);
            foreach (var u in updates)
            {
                pId.Value = u.Id;
                pFg.Value = u.ForegroundSeconds;
                pBg.Value = u.BackgroundSeconds;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    // ────────────────────────────────────────
    // App Items (generic child of app_sessions)
    // ────────────────────────────────────────

    public async Task StoreAppItemsAsync(IReadOnlyList<AppItem> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.InsertAppItemSql;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pAppSess = cmd.Parameters.Add("$app_session_id", SqliteType.Text);
            var pParent = cmd.Parameters.Add("$parent_item_id", SqliteType.Text);
            var pType = cmd.Parameters.Add("$item_type", SqliteType.Text);
            var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
            var pIdent = cmd.Parameters.Add("$identifier", SqliteType.Text);
            var pUrl = cmd.Parameters.Add("$url", SqliteType.Text);
            var pDomain = cmd.Parameters.Add("$domain", SqliteType.Text);
            var pOpened = cmd.Parameters.Add("$opened_at", SqliteType.Text);
            var pClosed = cmd.Parameters.Add("$closed_at", SqliteType.Text);
            var pProcId = cmd.Parameters.Add("$process_id", SqliteType.Integer);
            var pObjType = cmd.Parameters.Add("$object_type", SqliteType.Text);
            var pAction = cmd.Parameters.Add("$action", SqliteType.Text);
            var pJourneyId = cmd.Parameters.Add("$journey_id", SqliteType.Text);
            var pSequence = cmd.Parameters.Add("$sequence", SqliteType.Integer);
            var pPrevPath = cmd.Parameters.Add("$previous_path", SqliteType.Text);
            var pCurPath = cmd.Parameters.Add("$current_path", SqliteType.Text);
            var pWinId = cmd.Parameters.Add("$window_id", SqliteType.Integer);
            var pTabId = cmd.Parameters.Add("$tab_id", SqliteType.Integer);
            var pMetaJson = cmd.Parameters.Add("$metadata_json", SqliteType.Text);

            await using var tx = await _connection.BeginTransactionAsync(ct);
            ((DbCommand)cmd).Transaction = tx;
            foreach (var e in entries)
            {
                pId.Value = e.Id;
                pAppSess.Value = e.AppSessionId;
                pParent.Value = (object?)e.ParentItemId ?? DBNull.Value;
                pType.Value = e.ItemType;
                pTitle.Value = e.Title;
                pIdent.Value = e.Identifier;
                pUrl.Value = e.Url;
                pDomain.Value = e.Domain;
                pOpened.Value = e.OpenedAt.ToString("O");
                pClosed.Value = e.ClosedAt?.ToString("O") ?? (object)DBNull.Value;
                pProcId.Value = e.ProcessId.HasValue ? e.ProcessId.Value : DBNull.Value;
                pObjType.Value = e.ObjectType;
                pAction.Value = e.Action;
                pJourneyId.Value = e.JourneyId;
                pSequence.Value = e.Sequence;
                pPrevPath.Value = e.PreviousPath;
                pCurPath.Value = e.CurrentPath;
                pWinId.Value = e.WindowId.HasValue ? e.WindowId.Value : DBNull.Value;
                pTabId.Value = e.TabId.HasValue ? e.TabId.Value : DBNull.Value;
                pMetaJson.Value = e.MetadataJson;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<AppItem>> GetUnsentAppItemsAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<AppItem>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM app_items WHERE is_synced = 0 ORDER BY opened_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<AppItem>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapAppItemReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkAppItemsSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("app_items", "id", ids, ct);
    }

    public async Task UpdateAppItemParentAsync(string itemId, string parentItemId, CancellationToken ct)
    {
        if (_connection == null) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE app_items SET parent_item_id = $parent_item_id, is_synced = 0 WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$parent_item_id", parentItemId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<OpenSessionRecord>> GetOpenSessionRecordsAsync(CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<OpenSessionRecord>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT s.id, s.process_name, COALESCE(s.process_id, 0), i.id AS item_id, i.item_type, s.installed_app_id, i.title, i.url
                FROM app_sessions s
                INNER JOIN app_items i ON i.app_session_id = s.id AND i.parent_item_id IS NULL
                WHERE s.ended_at IS NULL AND s.process_id IS NOT NULL
                ORDER BY s.started_at ASC";
            var results = new List<OpenSessionRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new OpenSessionRecord
                {
                    AppSessionId = reader.GetString(0),
                    ProcessName = reader.GetString(1),
                    ProcessId = reader.GetInt32(2),
                    RootItemId = reader.GetString(3),
                    ItemType = reader.GetString(4),
                    InstalledAppId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    RootItemTitle = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    RootItemUrl = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                });
            }
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<OpenSessionRecord>> GetAllOpenSessionRecordsAsync(CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<OpenSessionRecord>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT s.id, s.process_name, COALESCE(s.process_id, 0), '' AS item_id, '' AS item_type, s.installed_app_id
                FROM app_sessions s
                WHERE s.ended_at IS NULL
                ORDER BY s.started_at ASC";
            var results = new List<OpenSessionRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new OpenSessionRecord
                {
                    AppSessionId = reader.GetString(0),
                    ProcessName = reader.GetString(1),
                    ProcessId = reader.GetInt32(2),
                    RootItemId = reader.GetString(3),
                    ItemType = reader.GetString(4),
                    InstalledAppId = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task CloseAppItemAsync(string itemId, DateTime closedAt, CancellationToken ct)
    {
        if (_connection == null || string.IsNullOrWhiteSpace(itemId)) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE app_items SET closed_at = $closed_at, is_synced = 0 WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$closed_at", closedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task CloseAppItemsBySessionIdsAsync(IReadOnlyList<string> sessionIds, DateTime closedAt, CancellationToken ct)
    {
        if (_connection == null || sessionIds.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            await using var tx = await _connection.BeginTransactionAsync(ct);
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE app_items SET closed_at = $closed_at, is_synced = 0
                WHERE app_session_id = $session_id AND closed_at IS NULL";
            ((DbCommand)cmd).Transaction = tx;
            var pSessionId = cmd.Parameters.Add("$session_id", SqliteType.Text);
            var pClosed = cmd.Parameters.Add("$closed_at", SqliteType.Text);
            foreach (var sid in sessionIds)
            {
                pSessionId.Value = sid;
                pClosed.Value = closedAt.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// Atomically close a set of sessions AND their still-open app_items in ONE
    /// transaction, under ONE gate acquisition. A crash between the two writes is
    /// structurally impossible — you never get closed sessions with orphaned open items.
    /// </summary>
    public async Task CloseSessionsAndAppItemsAsync(IReadOnlyList<AppSession> closeSessions, DateTime closedAt, CancellationToken ct)
    {
        if (_connection == null || closeSessions.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            await using var tx = await _connection.BeginTransactionAsync(ct);

            // 1) Close the sessions themselves (ended_at)
            var updateCmd = _connection.CreateCommand();
            updateCmd.CommandText = DatabaseSchema.UpdateAppSessionEndedSql;
            ((DbCommand)updateCmd).Transaction = tx;
            var pId = updateCmd.Parameters.Add("$id", SqliteType.Text);
            var pEnd = updateCmd.Parameters.Add("$ended_at", SqliteType.Text);
            var pFg = updateCmd.Parameters.Add("$foreground_seconds", SqliteType.Real);
            var pBg = updateCmd.Parameters.Add("$background_seconds", SqliteType.Real);
            foreach (var e in closeSessions)
            {
                pId.Value = e.Id;
                pEnd.Value = (e.EndedAt ?? closedAt).ToString("O");
                // NULL keeps the last flushed value when the caller has no focus data.
                pFg.Value = e.ForegroundSeconds.HasValue ? e.ForegroundSeconds.Value : (object)DBNull.Value;
                pBg.Value = e.BackgroundSeconds.HasValue ? e.BackgroundSeconds.Value : (object)DBNull.Value;
                await updateCmd.ExecuteNonQueryAsync(ct);
            }

            // 2) Cascade-close every still-open app_item of those sessions
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE app_items SET closed_at = $closed_at, is_synced = 0
                WHERE app_session_id = $session_id AND closed_at IS NULL";
            ((DbCommand)cmd).Transaction = tx;
            var pSessionId = cmd.Parameters.Add("$session_id", SqliteType.Text);
            var pClosed = cmd.Parameters.Add("$closed_at", SqliteType.Text);
            foreach (var e in closeSessions)
            {
                pSessionId.Value = e.Id;
                pClosed.Value = closedAt.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<AppItem?> GetOpenAppItemAsync(string appSessionId, string itemType, string identifier, CancellationToken ct)
    {
        if (_connection == null) return null;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM app_items
                WHERE app_session_id = $session_id AND item_type = $item_type
                  AND identifier = $identifier AND closed_at IS NULL
                LIMIT 1";
            cmd.Parameters.AddWithValue("$session_id", appSessionId);
            cmd.Parameters.AddWithValue("$item_type", itemType);
            cmd.Parameters.AddWithValue("$identifier", identifier);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? MapAppItemReader(reader) : null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<AppItem?> GetOpenRootItemAsync(string appSessionId, string itemType, CancellationToken ct)
    {
        if (_connection == null) return null;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM app_items
                WHERE app_session_id = $session_id AND item_type = $item_type
                  AND parent_item_id IS NULL AND closed_at IS NULL
                ORDER BY opened_at DESC
                LIMIT 1";
            cmd.Parameters.AddWithValue("$session_id", appSessionId);
            cmd.Parameters.AddWithValue("$item_type", itemType);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? MapAppItemReader(reader) : null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<AppItem?> GetOpenJourneyEventAsync(string journeyId, string objectType, string action, string currentPath, CancellationToken ct)
    {
        if (_connection == null) return null;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM app_items
                WHERE journey_id = $journey_id AND object_type = $object_type
                  AND action = $action AND current_path = $current_path AND closed_at IS NULL
                LIMIT 1";
            cmd.Parameters.AddWithValue("$journey_id", journeyId);
            cmd.Parameters.AddWithValue("$object_type", objectType);
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$current_path", currentPath);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? MapAppItemReader(reader) : null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<int> GetNextSequenceAsync(string journeyId, CancellationToken ct)
    {
        if (_connection == null) return 1;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(sequence), 0) + 1 FROM app_items WHERE journey_id = $journey_id";
            cmd.Parameters.AddWithValue("$journey_id", journeyId);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long l ? (int)l : 1;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task UpdateAppItemContextAsync(string itemId, string title, string identifier, CancellationToken ct)
    {
        if (_connection == null) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE app_items SET title = $title, identifier = $identifier, is_synced = 0
                WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$identifier", identifier);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<bool> HasStorageDevicesAsync(CancellationToken ct)
    {
        if (_connection == null) return false;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM storage_devices";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            return count > 0;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    // ────────────────────────────────────────
    // Storage Devices (relational child of device_hardware_info)
    // ────────────────────────────────────────

    public async Task StoreStorageDevicesAsync(IReadOnlyList<StorageDevice> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.InsertStorageDeviceSql;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pHwId = cmd.Parameters.Add("$device_hardware_id", SqliteType.Text);
            var pType = cmd.Parameters.Add("$device_type", SqliteType.Text);
            var pModel = cmd.Parameters.Add("$model", SqliteType.Text);
            var pCap = cmd.Parameters.Add("$capacity_mb", SqliteType.Integer);

            await using var tx = await _connection.BeginTransactionAsync(ct);
            ((DbCommand)cmd).Transaction = tx;
            foreach (var e in entries)
            {
                pId.Value = e.Id;
                pHwId.Value = e.DeviceHardwareId;
                pType.Value = e.DeviceType;
                pModel.Value = e.Model;
                pCap.Value = e.CapacityMb;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<StorageDevice>> GetUnsentStorageDevicesAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<StorageDevice>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM storage_devices WHERE is_synced = 0 ORDER BY created_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<StorageDevice>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapStorageDeviceReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<StorageDevice>> GetLatestStorageDevicesAsync(CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<StorageDevice>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            // Scope to the newest hardware snapshot so re-collections don't stack duplicates
            // in the UI. Falls back to nothing when no hardware row exists yet.
            var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM storage_devices
                WHERE device_hardware_id = (
                    SELECT id FROM device_hardware_info ORDER BY collected_at DESC LIMIT 1
                )
                ORDER BY capacity_mb DESC
                """;
            var results = new List<StorageDevice>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapStorageDeviceReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkStorageDevicesSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("storage_devices", "id", ids, ct);
    }

    // ────────────────────────────────────────
    // Hardware Devices (USB / peripheral hotplug)
    // ────────────────────────────────────────

    public async Task StoreHardwareDevicesAsync(IReadOnlyList<HardwareDevice> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.InsertHardwareDeviceSql;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pClass = cmd.Parameters.Add("$device_class", SqliteType.Text);
            var pVendor = cmd.Parameters.Add("$vendor", SqliteType.Text);
            var pProduct = cmd.Parameters.Add("$product", SqliteType.Text);
            var pSerial = cmd.Parameters.Add("$serial", SqliteType.Text);
            var pBusPath = cmd.Parameters.Add("$bus_path", SqliteType.Text);
            var pNode = cmd.Parameters.Add("$device_node", SqliteType.Text);
            var pPluggedAt = cmd.Parameters.Add("$plugged_at", SqliteType.Text);

            await using var tx = await _connection.BeginTransactionAsync(ct);
            ((DbCommand)cmd).Transaction = tx;
            foreach (var e in entries)
            {
                pId.Value = e.Id;
                pClass.Value = e.DeviceClass;
                pVendor.Value = e.Vendor;
                pProduct.Value = e.Product;
                pSerial.Value = e.Serial;
                pBusPath.Value = e.BusPath;
                pNode.Value = e.DeviceNode;
                pPluggedAt.Value = e.PluggedAt.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<HardwareDevice>> GetOpenHardwareDevicesAsync(CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<HardwareDevice>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM hardware_devices WHERE unplugged_at IS NULL ORDER BY plugged_at ASC";
            var results = new List<HardwareDevice>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapHardwareDeviceReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<HardwareDevice?> GetOpenHardwareDeviceByBusPathAsync(string busPath, CancellationToken ct)
    {
        if (_connection == null || string.IsNullOrWhiteSpace(busPath)) return null;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM hardware_devices WHERE bus_path = $path AND unplugged_at IS NULL LIMIT 1";
            cmd.Parameters.AddWithValue("$path", busPath);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) return MapHardwareDeviceReader(reader);
            return null;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task CloseHardwareDeviceAsync(string id, DateTime unpluggedAt, CancellationToken ct)
    {
        if (_connection == null) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            // is_synced = 0: an already-synced device must re-sync its unplug so the
            // server learns the device left (user rule 2026-08-12).
            cmd.CommandText = "UPDATE hardware_devices SET unplugged_at = $at, is_synced = 0 WHERE id = $id";
            cmd.Parameters.AddWithValue("$at", unpluggedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }
    // ────────────────────────────────────────
    // Sync: hardware devices (sent to server; never deleted client-side)
    // ────────────────────────────────────────

    public async Task<IReadOnlyList<HardwareDevice>> GetUnsentHardwareDevicesAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<HardwareDevice>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM hardware_devices WHERE is_synced = 0 ORDER BY plugged_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<HardwareDevice>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(MapHardwareDeviceReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkHardwareDevicesSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("hardware_devices", "id", ids, ct);
    }

    // ────────────────────────────────────────
    // Hardware & Port Control — full scanned inventory (synced; client owns present)
    // ────────────────────────────────────────

    /// <summary>
    /// Upsert the full scan result, then mark any previously-present device that
    /// vanished from this scan as present=0 (is_synced=0) so the removal syncs.
    /// A bad/empty scan is a no-op so it can never wipe the inventory.
    /// </summary>
    public async Task ReplaceMachinePortScanAsync(IReadOnlyList<MachinePort> scanned, CancellationToken ct)
    {
        if (_connection == null || scanned.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            await using var tx = await _connection.BeginTransactionAsync(ct);

            var cmd = _connection.CreateCommand();
            ((DbCommand)cmd).Transaction = (DbTransaction)tx;
            cmd.CommandText = DatabaseSchema.UpsertMachinePortSql;
            var pInstance = cmd.Parameters.Add("$instance_id", SqliteType.Text);
            var pCategory = cmd.Parameters.Add("$category", SqliteType.Integer);
            var pClass = cmd.Parameters.Add("$pnp_class", SqliteType.Text);
            var pGuid = cmd.Parameters.Add("$class_guid", SqliteType.Text);
            var pBus = cmd.Parameters.Add("$bus_type", SqliteType.Text);
            var pName = cmd.Parameters.Add("$friendly_name", SqliteType.Text);
            var pVendor = cmd.Parameters.Add("$manufacturer", SqliteType.Text);
            var pPresent = cmd.Parameters.Add("$present", SqliteType.Integer);
            var pStatus = cmd.Parameters.Add("$status", SqliteType.Text);
            var pBlockable = cmd.Parameters.Add("$blockable", SqliteType.Integer);

            foreach (var p in scanned)
            {
                pInstance.Value = p.InstanceId;
                pCategory.Value = p.Category;
                pClass.Value = p.PnpClass;
                pGuid.Value = p.ClassGuid;
                pBus.Value = p.BusType;
                pName.Value = p.FriendlyName;
                pVendor.Value = p.Manufacturer;
                pPresent.Value = p.Present ? 1 : 0;
                pStatus.Value = p.Status;
                pBlockable.Value = p.Blockable ? 1 : 0;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Mark vanished devices removed. Build a parameterized NOT IN list.
            var absent = _connection.CreateCommand();
            ((DbCommand)absent).Transaction = (DbTransaction)tx;
            var names = new List<string>(scanned.Count);
            for (var i = 0; i < scanned.Count; i++)
            {
                var key = "$k" + i;
                names.Add(key);
                absent.Parameters.AddWithValue(key, scanned[i].InstanceId);
            }
            absent.CommandText =
                "UPDATE machine_ports SET present = 0, is_synced = 0 " +
                "WHERE present = 1 AND instance_id NOT IN (" + string.Join(",", names) + ")";
            await absent.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<MachinePort>> GetUnsentMachinePortsAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<MachinePort>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT instance_id, category, pnp_class, class_guid, bus_type, friendly_name, " +
                "manufacturer, present, status, blockable FROM machine_ports " +
                "WHERE is_synced = 0 ORDER BY scanned_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<MachinePort>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new MachinePort
                {
                    InstanceId = reader.GetString(0),
                    Category = reader.GetInt32(1),
                    PnpClass = reader.GetString(2),
                    ClassGuid = reader.GetString(3),
                    BusType = reader.GetString(4),
                    FriendlyName = reader.GetString(5),
                    Manufacturer = reader.GetString(6),
                    Present = reader.GetInt32(7) != 0,
                    Status = reader.GetString(8),
                    Blockable = reader.GetInt32(9) != 0,
                });
            }
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkMachinePortsSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("machine_ports", "instance_id", ids, ct);
    }

    // ────────────────────────────────────────
    // Location samples (Phase 3 GPS — synced; never deleted client-side)
    // ────────────────────────────────────────

    public async Task StoreLocationSamplesAsync(IReadOnlyList<LocationSample> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.InsertLocationSampleSql;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pLat = cmd.Parameters.Add("$latitude", SqliteType.Real);
            var pLon = cmd.Parameters.Add("$longitude", SqliteType.Real);
            var pAcc = cmd.Parameters.Add("$accuracy_m", SqliteType.Real);
            var pAlt = cmd.Parameters.Add("$altitude_m", SqliteType.Real);
            var pSrc = cmd.Parameters.Add("$source", SqliteType.Text);
            var pAddr = cmd.Parameters.Add("$address", SqliteType.Text);
            var pCap = cmd.Parameters.Add("$captured_at", SqliteType.Text);

            foreach (var e in entries)
            {
                pId.Value = e.Id;
                pLat.Value = e.Latitude;
                pLon.Value = e.Longitude;
                pAcc.Value = e.AccuracyM.HasValue ? e.AccuracyM.Value : DBNull.Value;
                pAlt.Value = e.AltitudeM.HasValue ? e.AltitudeM.Value : DBNull.Value;
                pSrc.Value = e.Source;
                pAddr.Value = string.IsNullOrWhiteSpace(e.Address) ? DBNull.Value : e.Address;
                pCap.Value = e.CapturedAt.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<LocationSample>> GetUnsentLocationSamplesAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<LocationSample>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, latitude, longitude, accuracy_m, altitude_m, source, address,
                       captured_at, is_synced, synced_at, created_at
                FROM location_samples
                WHERE is_synced = 0
                ORDER BY captured_at ASC
                LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<LocationSample>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(MapLocationSampleReader(reader));
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkLocationSamplesSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("location_samples", "id", ids, ct);
    }

    // ────────────────────────────────────────
    // Sync: app_status + permission_status (sent to server; never deleted client-side)
    // ────────────────────────────────────────

    public async Task<IReadOnlyList<AppStatus>> GetUnsentAppStatusAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<AppStatus>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT key, value, updated_at, is_synced, synced_at FROM app_status WHERE is_synced = 0 ORDER BY updated_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<AppStatus>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new AppStatus
                {
                    Key = reader.GetString(reader.GetOrdinal("key")),
                    Value = reader.GetString(reader.GetOrdinal("value")),
                    UpdatedAt = reader.IsDBNull(reader.GetOrdinal("updated_at")) ? "" : reader.GetString(reader.GetOrdinal("updated_at")),
                    IsSynced = reader.GetInt32(reader.GetOrdinal("is_synced")) == 1,
                    SyncedAt = reader.IsDBNull(reader.GetOrdinal("synced_at")) ? null : reader.GetString(reader.GetOrdinal("synced_at")),
                });
            }
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkAppStatusSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("app_status", "key", ids, ct);
    }

    public async Task<IReadOnlyList<PermissionStatus>> GetUnsentPermissionStatusAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<PermissionStatus>();
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM permission_status WHERE is_synced = 0 ORDER BY checked_at ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            var results = new List<PermissionStatus>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new PermissionStatus
                {
                    CheckId = reader.GetString(reader.GetOrdinal("check_id")),
                    SessionId = reader.IsDBNull(reader.GetOrdinal("session_id")) ? "" : reader.GetString(reader.GetOrdinal("session_id")),
                    SessionType = reader.IsDBNull(reader.GetOrdinal("session_type")) ? "" : reader.GetString(reader.GetOrdinal("session_type")),
                    Platform = reader.IsDBNull(reader.GetOrdinal("platform")) ? "" : reader.GetString(reader.GetOrdinal("platform")),
                    CheckedAt = reader.IsDBNull(reader.GetOrdinal("checked_at")) ? "" : reader.GetString(reader.GetOrdinal("checked_at")),
                    Method = reader.IsDBNull(reader.GetOrdinal("method")) ? "" : reader.GetString(reader.GetOrdinal("method")),
                    Works = reader.GetInt32(reader.GetOrdinal("works")) == 1,
                    Details = reader.IsDBNull(reader.GetOrdinal("details")) ? null : reader.GetString(reader.GetOrdinal("details")),
                    EmployeeId = reader.IsDBNull(reader.GetOrdinal("employee_id")) ? null : reader.GetString(reader.GetOrdinal("employee_id")),
                    EmployeeName = reader.IsDBNull(reader.GetOrdinal("employee_name")) ? null : reader.GetString(reader.GetOrdinal("employee_name")),
                    IsSynced = reader.GetInt32(reader.GetOrdinal("is_synced")) == 1,
                });
            }
            return results;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task MarkPermissionStatusSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await MarkSentCoreAsync("permission_status", "check_id", ids, ct);
    }

    /// <summary>
    /// Retention cleanup (24h default): deletes SYNCED rows the server already has that are no
    /// longer needed locally — old app_items, CLOSED old app_sessions (open sessions and sessions
    /// that still have items are never deleted), uninstalled inventory cycles (is_installed = 0),
    /// and superseded network rows (is_current = 0). Everything else is retained forever. Runs in
    /// ONE transaction so a crash mid-cleanup can't partially apply.
    /// </summary>
    public async Task<SyncedDataDeletionCounts> DeleteSyncedDataOlderThanAsync(DateTime cutoff, CancellationToken ct)
    {
        if (_connection == null) return SyncedDataDeletionCounts.Empty;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cutoffIso = cutoff.ToString("O");
            await using var tx = await _connection.BeginTransactionAsync(ct);

            // Children first: app_items.parent_item_id self-FK fails if a parent
            // row is deleted while a (possibly newer) child still references it.
            var childItemsCmd = _connection.CreateCommand();
            ((DbCommand)childItemsCmd).Transaction = tx;
            childItemsCmd.CommandText = @"
                DELETE FROM app_items
                WHERE is_synced = 1
                  AND opened_at < $cutoff
                  AND parent_item_id IS NOT NULL";
            childItemsCmd.Parameters.AddWithValue("$cutoff", cutoffIso);
            var childrenDeleted = await childItemsCmd.ExecuteNonQueryAsync(ct);

            var itemsCmd = _connection.CreateCommand();
            ((DbCommand)itemsCmd).Transaction = tx;
            itemsCmd.CommandText = @"
                DELETE FROM app_items
                WHERE is_synced = 1
                  AND opened_at < $cutoff
                  AND NOT EXISTS (
                      SELECT 1 FROM app_items child
                      WHERE child.parent_item_id = app_items.id
                  )";
            itemsCmd.Parameters.AddWithValue("$cutoff", cutoffIso);
            var itemsDeleted = childrenDeleted + await itemsCmd.ExecuteNonQueryAsync(ct);

            var sessionsCmd = _connection.CreateCommand();
            ((DbCommand)sessionsCmd).Transaction = tx;
            sessionsCmd.CommandText = @"
                DELETE FROM app_sessions
                WHERE is_synced = 1
                  AND ended_at IS NOT NULL
                  AND started_at < $cutoff
                  AND NOT EXISTS (SELECT 1 FROM app_items WHERE app_items.app_session_id = app_sessions.id)";
            sessionsCmd.Parameters.AddWithValue("$cutoff", cutoffIso);
            var sessionsDeleted = await sessionsCmd.ExecuteNonQueryAsync(ct);

            var appsCmd = _connection.CreateCommand();
            ((DbCommand)appsCmd).Transaction = tx;
            appsCmd.CommandText = @"
                DELETE FROM installed_applications
                WHERE is_installed = 0 AND is_synced = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM app_sessions
                      WHERE app_sessions.installed_app_id = installed_applications.id)";
            var appsDeleted = await appsCmd.ExecuteNonQueryAsync(ct);

            var pkgsCmd = _connection.CreateCommand();
            ((DbCommand)pkgsCmd).Transaction = tx;
            pkgsCmd.CommandText = @"
                DELETE FROM installed_packages
                WHERE is_installed = 0 AND is_synced = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM app_sessions
                      WHERE app_sessions.installed_package_id = installed_packages.id)";
            var pkgsDeleted = await pkgsCmd.ExecuteNonQueryAsync(ct);

            var netCmd = _connection.CreateCommand();
            ((DbCommand)netCmd).Transaction = tx;
            netCmd.CommandText = "DELETE FROM network_info WHERE is_current = 0 AND is_synced = 1";
            var netDeleted = await netCmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
            return new SyncedDataDeletionCounts(itemsDeleted, sessionsDeleted, appsDeleted, pkgsDeleted, netDeleted);
        }
        finally
        {
            _connectionGate.Release();
        }
    }


    // ────────────────────────────────────────
    // Status & Employee Info
    // ────────────────────────────────────────

    /// <summary>
    /// Ungated core for SetStatusAsync. Call from composite methods that
    /// already hold _connectionGate to avoid SemaphoreSlim reentrancy deadlock.
    /// </summary>
    private async Task SetStatusCoreAsync(string key, string value, CancellationToken ct)
    {
        if (_connection == null) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.UpsertStatusSql;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetStatusAsync(string key, string value, CancellationToken ct)
    {
        await _connectionGate.WaitAsync(ct);
        try
        {
            await SetStatusCoreAsync(key, value, ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<string?> GetStatusAsync(string key, CancellationToken ct)
    {
        if (_connection == null) return null;
        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM app_status WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// Ungated core for GetEmployeeInfoAsync. Call from composite methods
    /// that already hold _connectionGate to avoid SemaphoreSlim reentrancy.
    /// </summary>
    private async Task<EmployeeInfo?> GetEmployeeInfoCoreAsync(CancellationToken ct)
    {
        if (_connection == null) return null;

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, employee_id, name, email, role, department, shift, avatar, avatar_color, token, device_token, logged_in_at FROM employee_info LIMIT 1";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new EmployeeInfo
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                EmployeeId = reader.GetString(reader.GetOrdinal("employee_id")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                Email = reader.GetString(reader.GetOrdinal("email")),
                Role = reader.GetString(reader.GetOrdinal("role")),
                Department = reader.GetString(reader.GetOrdinal("department")),
                Shift = reader.IsDBNull(reader.GetOrdinal("shift")) ? string.Empty : reader.GetString(reader.GetOrdinal("shift")),
                Avatar = reader.IsDBNull(reader.GetOrdinal("avatar")) ? null : reader.GetString(reader.GetOrdinal("avatar")),
                AvatarColor = reader.IsDBNull(reader.GetOrdinal("avatar_color")) ? null : reader.GetString(reader.GetOrdinal("avatar_color")),
                Token = reader.IsDBNull(reader.GetOrdinal("token")) ? null : reader.GetString(reader.GetOrdinal("token")),
                DeviceToken = reader.IsDBNull(reader.GetOrdinal("device_token")) ? null : reader.GetString(reader.GetOrdinal("device_token")),
                LoggedInAt = reader.IsDBNull(reader.GetOrdinal("logged_in_at")) ? null : reader.GetString(reader.GetOrdinal("logged_in_at")),
            };
        }

        return null;
    }

    public async Task SetPermissionStatusAsync(IReadOnlyDictionary<string, bool> permissions, string sessionType, CancellationToken ct)
    {
        if (_connection == null) return;

        await _connectionGate.WaitAsync(ct);
        try
        {
            // Drop synced permission rows older than 24 hours. Unsynced rows stay until the server accepts them.
            var cleanupCmd = _connection.CreateCommand();
            cleanupCmd.CommandText = "DELETE FROM permission_status WHERE is_synced = 1 AND checked_at < strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now', '-1 day')";
            _ = await cleanupCmd.ExecuteNonQueryAsync(ct);

            await using var tx = await _connection.BeginTransactionAsync(ct);
            var cmd = _connection.CreateCommand();
            cmd.CommandText = DatabaseSchema.InsertPermissionSql;
            ((DbCommand)cmd).Transaction = tx;

            // Dedup fix (2026-08-11): the check_id was "{newGuid}_{method}" — a FRESH GUID on
            // every call (~every 5 min) inserted new rows instead of updating, flooding the
            // table with thousands of duplicates/day. Now keyed on the STABLE "{platform}_{method}"
            // and upserted (ON CONFLICT(check_id) DO UPDATE) — one row per permission method.
            var platform = "Linux";
            if (OperatingSystem.IsWindows()) platform = "Windows";
            else if (OperatingSystem.IsMacOS()) platform = "macOS";

            // Uses ungated private helper — caller already holds _connectionGate
            var empInfo = await GetEmployeeInfoCoreAsync(ct);
            var empId = empInfo?.EmployeeId;
            var empName = empInfo?.Name;

            // Bookmark id for the last-checked status (NOT the row key — rows are keyed on
            // "{platform}_{method}" so they upsert in place instead of duplicating).
            var checkId = Guid.NewGuid().ToString("N");

            foreach (var kvp in permissions)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$check_id", $"{platform}_{kvp.Key}");
                cmd.Parameters.AddWithValue("$session_id", SessionInfo.SessionId);
                cmd.Parameters.AddWithValue("$session_type", sessionType);
                cmd.Parameters.AddWithValue("$platform", platform);
                cmd.Parameters.AddWithValue("$checked_at", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$method", kvp.Key);
                cmd.Parameters.AddWithValue("$works", kvp.Value ? 1 : 0);
                cmd.Parameters.AddWithValue("$details", "");
                cmd.Parameters.AddWithValue("$employee_id", (object?)empId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$employee_name", (object?)empName ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            // Uses ungated private helper — caller already holds _connectionGate
            await SetStatusCoreAsync("permission_check_id", checkId, ct);
            await SetStatusCoreAsync("session_id", SessionInfo.SessionId, ct);
            await SetStatusCoreAsync("session_type", sessionType, ct);
            await SetStatusCoreAsync("last_permission_check", DateTime.UtcNow.ToString("O"), ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// Writes THIS machine's id into the single-row `machine_identity` table — the one
    /// value every telemetry INSERT reads (R2). Called at initialization and on every
    /// login so a re-login after a wipe stores the freshly generated id (R7).
    /// Ungated: callers already hold <see cref="_connectionGate"/>.
    /// </summary>
    private async Task SetMachineIdentityCoreAsync(CancellationToken ct)
    {
        if (_connection == null)
            return;

        var machineId = MachineId;
        if (string.IsNullOrEmpty(machineId))
            return;

        using var identCmd = _connection.CreateCommand();
        identCmd.CommandText = @"
            INSERT INTO machine_identity (id, machine_id, updated_at)
            VALUES (1, $mid, strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
            ON CONFLICT(id) DO UPDATE SET
                machine_id = excluded.machine_id,
                updated_at = excluded.updated_at";
        identCmd.Parameters.AddWithValue("$mid", machineId);
        await identCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveEmployeeInfoAsync(EmployeeInfo employee, CancellationToken ct)
    {
        if (_connection == null) return;

        await _connectionGate.WaitAsync(ct);
        try
        {
            // R7: a login (re-)establishes the machine identity, so the registry row and
            // the account row below always carry the id this login was made with.
            await SetMachineIdentityCoreAsync(ct);

            var clearCmd = _connection.CreateCommand();
            clearCmd.CommandText = "DELETE FROM employee_info";
            await clearCmd.ExecuteNonQueryAsync(ct);

            var cmd = _connection.CreateCommand();
            // machine_id (R2/R7, 2026-10-19): mirrors machine_identity, so the durable
            // local record of the logged-in account also states WHICH machine it was
            // established on. Cleared together with the row by the account wipe, so a
            // re-login after a wipe stores the freshly generated machine id.
            cmd.CommandText = @"
                INSERT INTO employee_info (id, machine_id, employee_id, name, email, role, department, shift, avatar, avatar_color, token, device_token, logged_in_at)
                VALUES ($id, $machine_id, $employee_id, $name, $email, $role, $department, $shift, $avatar, $avatar_color, $token, $device_token, strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
            ";
            cmd.Parameters.AddWithValue("$machine_id", MachineId);
            cmd.Parameters.AddWithValue("$id", employee.Id);
            cmd.Parameters.AddWithValue("$employee_id", employee.EmployeeId);
            cmd.Parameters.AddWithValue("$name", employee.Name);
            cmd.Parameters.AddWithValue("$email", employee.Email);
            cmd.Parameters.AddWithValue("$role", employee.Role);
            cmd.Parameters.AddWithValue("$department", employee.Department);
            cmd.Parameters.AddWithValue("$shift", (object?)employee.Shift ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$avatar", (object?)employee.Avatar ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$avatar_color", (object?)employee.AvatarColor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$token", (object?)employee.Token ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$device_token", (object?)employee.DeviceToken ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);

            // Uses ungated private helper — caller already holds _connectionGate
            await SetStatusCoreAsync("employee_id", employee.EmployeeId, ct);
            await SetStatusCoreAsync("employee_name", employee.Name, ct);
            await SetStatusCoreAsync("is_logged_in", "true", ct);

            // Bridge DeviceAuth to privileged DLP daemon (ProgramData /var/lib device.auth).
            // Always pass server URL — Linux daemon cannot pull policy with token alone.
            var serverUrl = (Environment.GetEnvironmentVariable("ALPHA_SERVER_URL") ?? "").TrimEnd('/');
            DlpDeviceCredentialBridge.Publish(employee.DeviceToken, employee.EmployeeId, serverUrl);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<EmployeeInfo?> GetEmployeeInfoAsync(CancellationToken ct)
    {
        await _connectionGate.WaitAsync(ct);
        try
        {
            return await GetEmployeeInfoCoreAsync(ct);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task ClearEmployeeInfoAsync(CancellationToken ct)
    {
        if (_connection == null) return;

        await _connectionGate.WaitAsync(ct);
        try
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM employee_info";
            await cmd.ExecuteNonQueryAsync(ct);

            // Uses ungated private helper — caller already holds _connectionGate
            await SetStatusCoreAsync("is_logged_in", "false", ct);
            await SetStatusCoreAsync("employee_id", "", ct);
            await SetStatusCoreAsync("employee_name", "", ct);

            DlpDeviceCredentialBridge.Clear();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <inheritdoc cref="ILogStore.PurgeSyncedAsync"/>
    public async Task<int> PurgeSyncedAsync(CancellationToken ct)
    {
        if (_connection == null) return 0;

        // Identity / config — never purged on Roaming Stop (newplan §5.2 / §5.4).
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "employee_info", "machine_identity",
            "employee_schedule", "company_holidays", "local_time_skew",
            "daily_attendance_cache",
        };

        await _connectionGate.WaitAsync(ct);
        try
        {
            var total = 0;
            await using var tx = await _connection.BeginTransactionAsync(ct);

            // Children before parents (FK-safe).
            async Task<int> Exec(string sql)
            {
                var cmd = _connection.CreateCommand();
                ((DbCommand)cmd).Transaction = tx;
                cmd.CommandText = sql;
                return await cmd.ExecuteNonQueryAsync(ct);
            }

            total += await Exec(@"
                DELETE FROM app_items
                WHERE is_synced = 1 AND parent_item_id IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM app_items child
                      WHERE child.parent_item_id = app_items.id AND child.is_synced = 0)");
            total += await Exec(@"
                DELETE FROM app_items
                WHERE is_synced = 1 AND parent_item_id IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM app_items child
                      WHERE child.parent_item_id = app_items.id)");
            total += await Exec(@"
                DELETE FROM app_sessions
                WHERE is_synced = 1
                  AND NOT EXISTS (SELECT 1 FROM app_items WHERE app_items.app_session_id = app_sessions.id)");

            foreach (var table in new[]
                     {
                         "session_events", "app_status", "permission_status",
                         "location_samples", "device_hardware_info", "storage_devices",
                         "hardware_devices", "network_info",
                     })
            {
                if (keep.Contains(table)) continue;
                total += await Exec($"DELETE FROM \"{table}\" WHERE is_synced = 1");
            }

            // Catalog rows only when uninstalled and unused by remaining sessions.
            total += await Exec(@"
                DELETE FROM installed_applications
                WHERE is_installed = 0 AND is_synced = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM app_sessions
                      WHERE app_sessions.installed_app_id = installed_applications.id)");
            total += await Exec(@"
                DELETE FROM installed_packages
                WHERE is_installed = 0 AND is_synced = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM app_sessions
                      WHERE app_sessions.installed_package_id = installed_packages.id)");

            await tx.CommitAsync(ct);

            if (total > 0)
            {
                try
                {
                    var vacuum = _connection.CreateCommand();
                    vacuum.CommandText = "VACUUM";
                    await vacuum.ExecuteNonQueryAsync(ct);
                }
                catch (SqliteException)
                {
                    /* compaction best-effort */
                }
            }

            return total;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task WipeLocalAccountDataAsync(CancellationToken ct)
    {
        if (_connection == null) return;

        await _connectionGate.WaitAsync(ct);
        try
        {
            // The table list is read from the database itself (sqlite_master) instead of a
            // hardcoded array, so a table added in a later release is wiped automatically —
            // a hardcoded list would silently leak the new table's rows past the removal.
            var tables = new List<string>();
            var listCmd = _connection.CreateCommand();
            listCmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            await using (var reader = await listCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    tables.Add(reader.GetString(0));
                }
            }

            // Foreign-key enforcement is turned OFF for the sweep. sqlite_master returns
            // tables in CREATION order, not dependency order, so deleting a parent
            // (app_sessions) before its children (app_items) aborts the whole transaction
            // with SQLITE_CONSTRAINT_FOREIGNKEY — which left the account on disk. Every
            // table is emptied in ONE transaction, so the database still lands consistent:
            // a fully empty database has no row left that could dangle. The pragma is a
            // no-op inside a transaction, so it must be set before BEGIN and restored
            // after COMMIT.
            var foreignKeysWereEnforced = await ReadForeignKeysPragmaAsync(ct);
            await WriteForeignKeysPragmaAsync(false, ct);
            try
            {
                await using (var tx = await _connection.BeginTransactionAsync(ct))
                {
                    foreach (var table in tables)
                    {
                        var cmd = _connection.CreateCommand();
                        cmd.Transaction = (SqliteTransaction)tx;
                        // Table names come from sqlite_master (never caller input); quoted so a
                        // name that collides with a keyword can never break the statement.
                        cmd.CommandText = $"DELETE FROM \"{table}\"";
                        await cmd.ExecuteNonQueryAsync(ct);
                    }

                    await tx.CommitAsync(ct);
                }
            }
            finally
            {
                if (foreignKeysWereEnforced)
                    await WriteForeignKeysPragmaAsync(true, ct);
            }

            // Physically reclaim the freed pages so nothing from the removed account is
            // left readable in the file. Deliberately best-effort: VACUUM needs exclusive
            // access, and the read-only connection used by the attendance rollup may be
            // mid-query — the DELETEs above ARE the removal, this only compacts the file.
            try
            {
                var vacuum = _connection.CreateCommand();
                vacuum.CommandText = "VACUUM";
                await vacuum.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException)
            {
                // Data is already gone; a failed compaction must not fail the removal.
            }

            DlpDeviceCredentialBridge.Clear();
        }
        finally
        {
            _connectionGate.Release();
        }

        // Remove .machine-id and device_id files from disk
        AppConfig.RemoveMachineAndDeviceIdentifiers();
    }

    /// <summary>
    /// Reads <c>PRAGMA foreign_keys</c> for the current connection (true = enforced).
    /// Used to restore the connection's setting after the wipe disables it.
    /// </summary>
    private async Task<bool> ReadForeignKeysPragmaAsync(CancellationToken ct)
    {
        if (_connection == null) return false;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys";
        var value = await cmd.ExecuteScalarAsync(ct);
        return value != null && Convert.ToInt64(value) != 0;
    }

    /// <summary>
    /// Toggles <c>PRAGMA foreign_keys</c> on the shared connection. SQLite silently
    /// ignores the pragma inside a transaction, so callers must invoke it outside one
    /// (see <see cref="WipeLocalAccountDataAsync"/>).
    /// </summary>
    private async Task WriteForeignKeysPragmaAsync(bool enforced, CancellationToken ct)
    {
        if (_connection == null) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = enforced ? "PRAGMA foreign_keys = ON" : "PRAGMA foreign_keys = OFF";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose()
    {
        _connectionGate.Dispose();
        _connection?.Close();
        _connection?.Dispose();
    }

    // ────────────────────────────────────────
    // Readers
    // ────────────────────────────────────────

    // (MapXxxReader methods and TryGetInt unchanged)
    private static DeviceHardwareInfo MapDeviceHardwareReader(SqliteDataReader r)
    {
        return new DeviceHardwareInfo
        {
            Id = r.GetString(r.GetOrdinal("id")),
            MacAddress = r.GetString(r.GetOrdinal("mac_address")),
            Hostname = r.GetString(r.GetOrdinal("hostname")),
            OsName = r.GetString(r.GetOrdinal("os_name")),
            OsVersion = r.GetString(r.GetOrdinal("os_version")),
            CpuModel = r.GetString(r.GetOrdinal("cpu_model")),
            CpuCores = r.GetInt32(r.GetOrdinal("cpu_cores")),
            RamTotalMb = r.GetInt64(r.GetOrdinal("ram_total_mb")),
            GpuModel = r.GetString(r.GetOrdinal("gpu_model")),
            GpuVramMb = r.GetInt64(r.GetOrdinal("gpu_vram_mb")),
            CollectedAt = DateTime.Parse(r.GetString(r.GetOrdinal("collected_at"))),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static InstalledApplication MapInstalledAppReader(SqliteDataReader r)
    {
        return new InstalledApplication
        {
            Id = r.GetString(r.GetOrdinal("id")),
            AppName = r.GetString(r.GetOrdinal("app_name")),
            BinaryName = r.IsDBNull(r.GetOrdinal("binary_name")) ? string.Empty : r.GetString(r.GetOrdinal("binary_name")),
            AppVersion = r.GetString(r.GetOrdinal("app_version")),
            Publisher = r.GetString(r.GetOrdinal("publisher")),
            InstallPath = r.GetString(r.GetOrdinal("install_path")),
            InstallDate = r.IsDBNull(r.GetOrdinal("install_date")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("install_date"))),
            UninstallString = r.GetString(r.GetOrdinal("uninstall_string")),
            ChangeType = r.GetString(r.GetOrdinal("change_type")),
            IsInstalled = TryGetInt(r, "is_installed") == 1,
            UninstallDate = r.IsDBNull(r.GetOrdinal("uninstall_date")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("uninstall_date"))),
            IsBrowser = r.GetInt32(r.GetOrdinal("is_browser")) == 1,
            DesktopId = r.IsDBNull(r.GetOrdinal("desktop_id")) ? string.Empty : r.GetString(r.GetOrdinal("desktop_id")),
            Categories = r.IsDBNull(r.GetOrdinal("categories")) ? string.Empty : r.GetString(r.GetOrdinal("categories")),
            DetectedAt = DateTime.Parse(r.GetString(r.GetOrdinal("detected_at"))),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static InstalledPackage MapInstalledPackageReader(SqliteDataReader r)
    {
        return new InstalledPackage
        {
            Id = r.GetString(r.GetOrdinal("id")),
            PackageName = r.GetString(r.GetOrdinal("package_name")),
            Version = r.GetString(r.GetOrdinal("version")),
            Category = r.GetString(r.GetOrdinal("category")),
            SourceManager = r.GetString(r.GetOrdinal("source_manager")),
            InstallPath = r.GetString(r.GetOrdinal("install_path")),
            Publisher = r.GetString(r.GetOrdinal("publisher")),
            Description = r.GetString(r.GetOrdinal("description")),
            InstallDate = r.IsDBNull(r.GetOrdinal("install_date")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("install_date"))),
            IsInstalled = TryGetInt(r, "is_installed") == 1,
            UninstallDate = r.IsDBNull(r.GetOrdinal("uninstall_date")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("uninstall_date"))),
            DetectedAt = DateTime.Parse(r.GetString(r.GetOrdinal("detected_at"))),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static NetworkInfo MapNetworkInfoReader(SqliteDataReader r)
    {
        return new NetworkInfo
        {
            Id = r.GetString(r.GetOrdinal("id")),
            PublicIp = r.GetString(r.GetOrdinal("public_ip")),
            PrivateIp = r.GetString(r.GetOrdinal("private_ip")),
            NetworkInterfaceName = r.GetString(r.GetOrdinal("network_interface_name")),
            CollectedAt = DateTime.Parse(r.GetString(r.GetOrdinal("collected_at"))),
            FirstSeenAt = r.IsDBNull(r.GetOrdinal("first_seen_at")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("first_seen_at"))),
            LastSeenAt = r.IsDBNull(r.GetOrdinal("last_seen_at")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("last_seen_at"))),
            IsCurrent = TryGetInt(r, "is_current") == 1,
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static HardwareDevice MapHardwareDeviceReader(SqliteDataReader r)
    {
        return new HardwareDevice
        {
            Id = r.GetString(r.GetOrdinal("id")),
            DeviceClass = r.GetString(r.GetOrdinal("device_class")),
            Vendor = r.GetString(r.GetOrdinal("vendor")),
            Product = r.GetString(r.GetOrdinal("product")),
            Serial = r.GetString(r.GetOrdinal("serial")),
            BusPath = r.GetString(r.GetOrdinal("bus_path")),
            DeviceNode = r.GetString(r.GetOrdinal("device_node")),
            PluggedAt = DateTime.Parse(r.GetString(r.GetOrdinal("plugged_at"))),
            UnpluggedAt = r.IsDBNull(r.GetOrdinal("unplugged_at")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("unplugged_at"))),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static LocationSample MapLocationSampleReader(SqliteDataReader r)
    {
        return new LocationSample
        {
            Id = r.GetString(r.GetOrdinal("id")),
            Latitude = r.GetDouble(r.GetOrdinal("latitude")),
            Longitude = r.GetDouble(r.GetOrdinal("longitude")),
            AccuracyM = r.IsDBNull(r.GetOrdinal("accuracy_m")) ? null : r.GetDouble(r.GetOrdinal("accuracy_m")),
            AltitudeM = r.IsDBNull(r.GetOrdinal("altitude_m")) ? null : r.GetDouble(r.GetOrdinal("altitude_m")),
            Source = r.GetString(r.GetOrdinal("source")),
            Address = r.IsDBNull(r.GetOrdinal("address")) ? null : r.GetString(r.GetOrdinal("address")),
            CapturedAt = DateTime.Parse(r.GetString(r.GetOrdinal("captured_at"))),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static SessionEvent MapSessionEventReader(SqliteDataReader r)
    {
        return new SessionEvent
        {
            Id = r.GetString(r.GetOrdinal("id")),
            EventType = r.GetString(r.GetOrdinal("event_type")),
            OsUsername = r.GetString(r.GetOrdinal("os_username")),
            // Legacy rows may carry a local offset while current writers use Z.
            // Normalize at the storage boundary so arithmetic never subtracts
            // wall-clock ticks from UTC ticks and silently shifts durations.
            EventAt = DateTimeOffset
                .Parse(
                    r.GetString(r.GetOrdinal("event_at")),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind)
                .UtcDateTime,
            EventCount = TryGetInt(r, "event_count"),
            FirstAt = TryGetDateTime(r, "first_at"),
            LastAt = TryGetDateTime(r, "last_at"),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static AppSession MapAppSessionReader(SqliteDataReader r)
    {
        return new AppSession
        {
            Id = r.GetString(r.GetOrdinal("id")),
            ProcessName = r.GetString(r.GetOrdinal("process_name")),
            AppDisplayName = r.GetString(r.GetOrdinal("app_display_name")),
            StartedAt = DateTime.Parse(r.GetString(r.GetOrdinal("started_at"))),
            EndedAt = r.IsDBNull(r.GetOrdinal("ended_at")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("ended_at"))),
            MachineId = r.GetString(r.GetOrdinal("machine_id")),
            EmployeeId = r.IsDBNull(r.GetOrdinal("employee_id")) ? null : r.GetString(r.GetOrdinal("employee_id")),
            EmployeeName = r.IsDBNull(r.GetOrdinal("employee_name")) ? null : r.GetString(r.GetOrdinal("employee_name")),
            SessionId = r.GetString(r.GetOrdinal("session_id")),
            Platform = r.GetString(r.GetOrdinal("platform")),
            InstalledAppId = r.IsDBNull(r.GetOrdinal("installed_app_id")) ? null : r.GetString(r.GetOrdinal("installed_app_id")),
            InstalledPackageId = r.IsDBNull(r.GetOrdinal("installed_package_id")) ? null : r.GetString(r.GetOrdinal("installed_package_id")),
            ProcessId = TryGetInt(r, "process_id"),
            ParentProcessId = TryGetInt(r, "parent_process_id"),
            GroupedBy = TryGetString(r, "grouped_by"),
            CgroupScope = TryGetString(r, "cgroup_scope"),
            ContextLabel = TryGetString(r, "context_label"),
            ForegroundSeconds = TryGetDouble(r, "foreground_seconds"),
            BackgroundSeconds = TryGetDouble(r, "background_seconds"),
            LastActivityAt = r.IsDBNull(r.GetOrdinal("last_activity_at"))
                ? null
                : DateTime.Parse(r.GetString(r.GetOrdinal("last_activity_at"))),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static StorageDevice MapStorageDeviceReader(SqliteDataReader r)
    {
        return new StorageDevice
        {
            Id = r.GetString(r.GetOrdinal("id")),
            DeviceHardwareId = r.GetString(r.GetOrdinal("device_hardware_id")),
            DeviceType = r.GetString(r.GetOrdinal("device_type")),
            Model = r.GetString(r.GetOrdinal("model")),
            CapacityMb = r.GetInt64(r.GetOrdinal("capacity_mb")),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static AppItem MapAppItemReader(SqliteDataReader r)
    {
        return new AppItem
        {
            Id = r.GetString(r.GetOrdinal("id")),
            AppSessionId = r.GetString(r.GetOrdinal("app_session_id")),
            ParentItemId = r.IsDBNull(r.GetOrdinal("parent_item_id")) ? null : r.GetString(r.GetOrdinal("parent_item_id")),
            ItemType = r.GetString(r.GetOrdinal("item_type")),
            Title = r.GetString(r.GetOrdinal("title")),
            Identifier = r.GetString(r.GetOrdinal("identifier")),
            Url = r.IsDBNull(r.GetOrdinal("url")) ? string.Empty : r.GetString(r.GetOrdinal("url")),
            Domain = r.IsDBNull(r.GetOrdinal("domain")) ? string.Empty : r.GetString(r.GetOrdinal("domain")),
            OpenedAt = DateTime.Parse(r.GetString(r.GetOrdinal("opened_at"))),
            ClosedAt = r.IsDBNull(r.GetOrdinal("closed_at")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("closed_at"))),
            ProcessId = TryGetInt(r, "process_id"),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
            ObjectType = r.IsDBNull(r.GetOrdinal("object_type")) ? string.Empty : r.GetString(r.GetOrdinal("object_type")),
            Action = r.IsDBNull(r.GetOrdinal("action")) ? string.Empty : r.GetString(r.GetOrdinal("action")),
            JourneyId = r.IsDBNull(r.GetOrdinal("journey_id")) ? string.Empty : r.GetString(r.GetOrdinal("journey_id")),
            Sequence = r.GetInt32(r.GetOrdinal("sequence")),
            PreviousPath = r.IsDBNull(r.GetOrdinal("previous_path")) ? string.Empty : r.GetString(r.GetOrdinal("previous_path")),
            CurrentPath = r.IsDBNull(r.GetOrdinal("current_path")) ? string.Empty : r.GetString(r.GetOrdinal("current_path")),
            WindowId = TryGetInt(r, "window_id"),
            TabId = TryGetInt(r, "tab_id"),
            MetadataJson = r.IsDBNull(r.GetOrdinal("metadata_json")) ? "{}" : r.GetString(r.GetOrdinal("metadata_json")),
        };
    }

    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct)
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized");
        await _connectionGate.WaitAsync(ct);
        // Note: caller must release the gate after committing/rolling back the transaction.
        // This is a leaky abstraction — prefer using the typed Store* methods which manage
        // the gate internally. BeginTransactionAsync exists for callers that need fine-grained
        // control (e.g., wrapping multiple operations in one tx).
        try
        {
            var tx = await _connection.BeginTransactionAsync(ct);
            return new GatedTransaction(tx, _connectionGate);
        }
        catch
        {
            // If BeginTransactionAsync itself throws (e.g. SQLITE_BUSY), release the gate
            // immediately — otherwise it stays locked forever and every other caller hangs.
            _connectionGate.Release();
            throw;
        }
    }

    /// <summary>
    /// Wraps a DbTransaction + releases the SemaphoreSlim gate on DisposeAsync.
    /// Uses DbTransaction (base class) because SqliteConnection.BeginTransactionAsync
    /// returns ValueTask&lt;DbTransaction&gt; from the base DbConnection override, not
    /// SqliteTransaction directly. DbTransaction.DisposeAsync is sufficient.
    /// </summary>
    private sealed class GatedTransaction : IAsyncDisposable
    {
        private readonly DbTransaction _tx;
        private readonly SemaphoreSlim _gate;
        private bool _disposed;

        public GatedTransaction(DbTransaction tx, SemaphoreSlim gate)
        {
            _tx = tx;
            _gate = gate;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _tx.DisposeAsync();
            _gate.Release();
        }
    }

    private static int? TryGetInt(SqliteDataReader r, string column)
    {
        try
        {
            var ordinal = r.GetOrdinal(column);
            return r.IsDBNull(ordinal) ? null : r.GetInt32(ordinal);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static double? TryGetDouble(SqliteDataReader r, string column)
    {
        try
        {
            var ordinal = r.GetOrdinal(column);
            return r.IsDBNull(ordinal) ? (double?)null : r.GetDouble(ordinal);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTime? TryGetDateTime(SqliteDataReader r, string column)
    {
        try
        {
            var ordinal = r.GetOrdinal(column);
            if (r.IsDBNull(ordinal)) return null;
            return DateTimeOffset.Parse(
                r.GetString(ordinal),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind).UtcDateTime;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string? TryGetString(SqliteDataReader r, string column)
    {
        try
        {
            var ordinal = r.GetOrdinal(column);
            return r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }
}
