namespace client.Storage;

internal static class DatabaseSchema
{
    internal const string CreateTableSql = @"
        -- ── MACHINE IDENTITY (R2, 2026-10-19) ─────────────────────────────────
        -- Single-row table holding THIS machine's identity (AppConfig.ClientId).
        -- Every telemetry table carries `machine_id` and its INSERT reads the value
        -- from here, so the identity is stamped in SQL without threading a parameter
        -- through every store method. The row is (re)written on every InitializeAsync
        -- and cleared together with the local DB on account removal, so a wipe always
        -- yields a brand-new machine id on the next login (R7).
        CREATE TABLE IF NOT EXISTS machine_identity (
            id          INTEGER PRIMARY KEY CHECK (id = 1),
            machine_id  TEXT NOT NULL DEFAULT '',
            updated_at  TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        -- DEVICE & SYSTEM INFO TABLES

        CREATE TABLE IF NOT EXISTS device_hardware_info (
            id               TEXT PRIMARY KEY,
            machine_id       TEXT NOT NULL DEFAULT '',
            mac_address      TEXT NOT NULL DEFAULT '',
            hostname         TEXT NOT NULL DEFAULT '',
            os_name          TEXT NOT NULL DEFAULT '',
            os_version       TEXT NOT NULL DEFAULT '',
            cpu_model        TEXT NOT NULL DEFAULT '',
            cpu_cores        INTEGER NOT NULL DEFAULT 0,
            ram_total_mb     INTEGER NOT NULL DEFAULT 0,
            gpu_model        TEXT NOT NULL DEFAULT '',
            gpu_vram_mb      INTEGER NOT NULL DEFAULT 0,
            collected_at     TEXT NOT NULL,
            is_synced        INTEGER NOT NULL DEFAULT 0,
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        -- Relational storage devices (replaces JSON storage_devices column from device_hardware_info)
        CREATE TABLE IF NOT EXISTS storage_devices (
            id                  TEXT PRIMARY KEY,
            machine_id          TEXT NOT NULL DEFAULT '',
            device_hardware_id  TEXT NOT NULL REFERENCES device_hardware_info(id),
            device_type         TEXT NOT NULL DEFAULT '',
            model               TEXT NOT NULL DEFAULT '',
            capacity_mb         INTEGER NOT NULL DEFAULT 0,
            is_synced           INTEGER NOT NULL DEFAULT 0,
            synced_at           TEXT,
            created_at          TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_storage_devices_hw
            ON storage_devices(device_hardware_id);

        CREATE INDEX IF NOT EXISTS idx_device_hw_unsent
            ON device_hardware_info(is_synced, collected_at);

        -- Inventory lifecycle v2 (2026-08-10): ONE ROW PER INSTALL CYCLE.
        -- app_name is NOT unique — a reinstall opens a NEW row so install→uninstall→reinstall
        -- history is visible as multiple records. A row with uninstall_date IS NULL is the
        -- currently-installed cycle; install_date is NULL when the OS did not report it
        -- (Linux) and only newly detected installs get stamped with the detection time.
        CREATE TABLE IF NOT EXISTS installed_applications (
            id               TEXT PRIMARY KEY,
            machine_id       TEXT NOT NULL DEFAULT '',
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
        );

        CREATE INDEX IF NOT EXISTS idx_installed_apps_unsent
            ON installed_applications(is_synced, detected_at);

        CREATE INDEX IF NOT EXISTS idx_installed_apps_name
            ON installed_applications(app_name);

        CREATE INDEX IF NOT EXISTS idx_installed_apps_binary
            ON installed_applications(binary_name);

        CREATE INDEX IF NOT EXISTS idx_installed_apps_desktop_id
            ON installed_applications(desktop_id);

        CREATE TABLE IF NOT EXISTS installed_packages (
            id               TEXT PRIMARY KEY,
            machine_id       TEXT NOT NULL DEFAULT '',
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
        );

        CREATE INDEX IF NOT EXISTS idx_installed_packages_unsent
            ON installed_packages(is_synced, detected_at);

        CREATE INDEX IF NOT EXISTS idx_installed_packages_source
            ON installed_packages(source_manager);

        CREATE INDEX IF NOT EXISTS idx_installed_packages_category
            ON installed_packages(category);

        CREATE TABLE IF NOT EXISTS network_info (
            id                   TEXT PRIMARY KEY,
            machine_id           TEXT NOT NULL DEFAULT '',
            public_ip            TEXT NOT NULL DEFAULT '',
            private_ip           TEXT NOT NULL DEFAULT '',
            network_interface_name TEXT NOT NULL DEFAULT '',
            collected_at         TEXT NOT NULL,
            first_seen_at        TEXT,
            last_seen_at         TEXT,
            is_current           INTEGER NOT NULL DEFAULT 1,
            is_synced            INTEGER NOT NULL DEFAULT 0,
            synced_at            TEXT,
            created_at           TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_network_unsent
            ON network_info(is_synced, collected_at);

        -- USB / peripheral / storage hotplug tracking (plug-in & plug-out history).
        -- device_class: 'storage' | 'input' | 'audio' | 'display' | 'usb' | 'power' | 'other'
        -- bus_path = udev DEVPATH (stable identity per physical plug), unplugged_at NULL while plugged.
        CREATE TABLE IF NOT EXISTS hardware_devices (
            id               TEXT PRIMARY KEY,
            machine_id       TEXT NOT NULL DEFAULT '',
            device_class     TEXT NOT NULL DEFAULT 'other',
            vendor           TEXT NOT NULL DEFAULT '',
            product          TEXT NOT NULL DEFAULT '',
            serial           TEXT NOT NULL DEFAULT '',
            bus_path         TEXT NOT NULL DEFAULT '',
            device_node      TEXT NOT NULL DEFAULT '',
            plugged_at       TEXT NOT NULL,
            unplugged_at     TEXT,
            is_synced        INTEGER NOT NULL DEFAULT 0,
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_hardware_devices_class
            ON hardware_devices(device_class, plugged_at);

        CREATE INDEX IF NOT EXISTS idx_hardware_devices_open
            ON hardware_devices(unplugged_at, bus_path);

        CREATE TABLE IF NOT EXISTS session_events (
            id               TEXT PRIMARY KEY,
            machine_id       TEXT NOT NULL DEFAULT '',
            event_type       TEXT NOT NULL,
            os_username      TEXT NOT NULL DEFAULT '',
            event_at         TEXT NOT NULL,
            event_count      INTEGER,
            first_at         TEXT,
            last_at          TEXT,
            is_synced        INTEGER NOT NULL DEFAULT 0,
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_session_events_unsent
            ON session_events(is_synced, event_at);

        -- Time & Attendance (Phase 1, finalplan §5 S5): the AttendanceAggregator's
        -- daily-window read uses (event_type, event_at) and the per-employee daily
        -- rollup uses (event_type, event_at) for a window scan. Both are O(log n)
        -- reads on this index. The unsent-drain index above is preserved because
        -- the /session-events/sync pull still uses (is_synced, event_at).
        CREATE INDEX IF NOT EXISTS idx_session_events_type_at
            ON session_events(event_type, event_at);

        -- APPLICATION LOGS

        CREATE TABLE IF NOT EXISTS app_sessions (
            id                  TEXT PRIMARY KEY,
            process_name        TEXT NOT NULL,
            app_display_name    TEXT NOT NULL DEFAULT '',
            started_at          TEXT NOT NULL,
            ended_at            TEXT,
            machine_id          TEXT NOT NULL DEFAULT '',
            employee_id         TEXT,
            employee_name       TEXT,
            session_id          TEXT NOT NULL DEFAULT '',
            platform            TEXT NOT NULL DEFAULT '',
            installed_app_id    TEXT REFERENCES installed_applications(id),
            installed_package_id TEXT REFERENCES installed_packages(id),
            process_id          INTEGER,
            parent_process_id   INTEGER,
            is_synced           INTEGER NOT NULL DEFAULT 0,
            synced_at           TEXT,
            created_at          TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')),
            last_activity_at    TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_app_sessions_unsent
            ON app_sessions(is_synced, started_at);

        CREATE INDEX IF NOT EXISTS idx_app_sessions_employee
            ON app_sessions(employee_id, started_at);

        CREATE INDEX IF NOT EXISTS idx_app_sessions_process_id
            ON app_sessions(process_id);

        CREATE INDEX IF NOT EXISTS idx_app_sessions_open
            ON app_sessions(ended_at, process_id);

        -- GENERIC APP ITEMS (replaces browser_contexts, file_explorer_contexts, urls, url_visits)
        -- Self-referencing via parent_item_id for nesting: app_session -> tab -> terminal/browser_navigation
        -- item_type: 'tab', 'browser_tab', 'browser_navigation', 'terminal', 'folder', 'file', etc.
        -- url/domain stored separately from identifier for proper querying

        CREATE TABLE IF NOT EXISTS app_items (
            id                TEXT PRIMARY KEY,
            machine_id        TEXT NOT NULL DEFAULT '',
            app_session_id    TEXT NOT NULL REFERENCES app_sessions(id),
            parent_item_id    TEXT REFERENCES app_items(id),
            item_type         TEXT NOT NULL DEFAULT '',
            title             TEXT NOT NULL DEFAULT '',
            identifier        TEXT NOT NULL DEFAULT '',
            url               TEXT NOT NULL DEFAULT '',
            domain            TEXT NOT NULL DEFAULT '',
            opened_at         TEXT NOT NULL,
            closed_at         TEXT,
            process_id        INTEGER,
            is_synced         INTEGER NOT NULL DEFAULT 0,
            synced_at         TEXT,
            created_at        TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')),
            object_type       TEXT NOT NULL DEFAULT '',
            action            TEXT NOT NULL DEFAULT '',
            journey_id        TEXT NOT NULL DEFAULT '',
            sequence          INTEGER NOT NULL DEFAULT 0,
            previous_path     TEXT NOT NULL DEFAULT '',
            current_path      TEXT NOT NULL DEFAULT '',
            window_id         INTEGER,
            tab_id            INTEGER,
            metadata_json     TEXT NOT NULL DEFAULT '{}'
        );

        CREATE INDEX IF NOT EXISTS idx_app_items_unsent
            ON app_items(is_synced, opened_at);

        CREATE INDEX IF NOT EXISTS idx_app_items_app_session
            ON app_items(app_session_id);

        CREATE INDEX IF NOT EXISTS idx_app_items_parent
            ON app_items(parent_item_id);

        CREATE INDEX IF NOT EXISTS idx_app_items_context
            ON app_items(app_session_id, item_type, identifier);

        CREATE INDEX IF NOT EXISTS idx_app_items_journey
            ON app_items(journey_id, sequence);

        CREATE INDEX IF NOT EXISTS idx_app_items_object_action
            ON app_items(object_type, action);

        -- APP STATUS & PERMISSIONS

        CREATE TABLE IF NOT EXISTS app_status (
            key             TEXT PRIMARY KEY,
            machine_id      TEXT NOT NULL DEFAULT '',
            value           TEXT NOT NULL,
            updated_at      TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')),
            is_synced       INTEGER NOT NULL DEFAULT 0,
            synced_at       TEXT
        );

        CREATE TABLE IF NOT EXISTS permission_status (
            check_id        TEXT PRIMARY KEY,
            machine_id      TEXT NOT NULL DEFAULT '',
            session_id      TEXT NOT NULL,
            session_type    TEXT NOT NULL,
            platform        TEXT NOT NULL,
            checked_at      TEXT NOT NULL,
            method          TEXT NOT NULL,
            works           INTEGER NOT NULL DEFAULT 0,
            details         TEXT,
            employee_id     TEXT,
            employee_name   TEXT,
            is_synced       INTEGER NOT NULL DEFAULT 0,
            synced_at       TEXT
        );

        CREATE TABLE IF NOT EXISTS employee_info (
            id              TEXT PRIMARY KEY,
            machine_id      TEXT NOT NULL DEFAULT '',
            employee_id     TEXT NOT NULL,
            name            TEXT NOT NULL,
            email           TEXT NOT NULL,
            role            TEXT NOT NULL,
            department      TEXT NOT NULL,
            shift           TEXT,
            avatar          TEXT,
            avatar_color    TEXT,
            token           TEXT,
            device_token    TEXT,
            logged_in_at    TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        -- shell_commands table intentionally removed — no longer collected

        -- ════════════════════════════════════════════════════════════════════════
        -- v12 / v13 — Time & Attendance (Phase 1, finalplan §2.2)
        -- All four tables are pure client mirror / derived data; the server
        -- (Phase 2) NEVER receives the daily_attendance_cache rows. The
        -- schedule/holiday tables are populated by the NEW ScheduleCacheService
        -- (A.6) which PULLs GET /api/v1/schedules/me every 6h.
        -- ════════════════════════════════════════════════════════════════════════

        -- Per-employee shift assignment, mirrored from the server's shifts catalog.
        -- weekly_pattern is a JSON string of {mon:HH:MM-HH:MM, tue:..., ...} so the
        -- client never has to know about the server's normalized shift schema.
        -- is_synced is the initial-pull ack: 1 = server has confirmed this row.
        CREATE TABLE IF NOT EXISTS employee_schedule (
            employee_id        TEXT PRIMARY KEY,
            timezone           TEXT NOT NULL DEFAULT 'UTC',
            weekly_pattern     TEXT NOT NULL DEFAULT '{}',
            grace_minutes      INTEGER NOT NULL DEFAULT 10,
            valid_from         TEXT,
            valid_to           TEXT,
            server_id          TEXT,
            is_synced          INTEGER NOT NULL DEFAULT 0,
            synced_at          TEXT,
            updated_at         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        -- Company-wide holiday calendar. The client uses it to bucket the daily
        -- attendance status (present/late/absent/half_day) — a day on this list
        -- is automatically off-shift.
        CREATE TABLE IF NOT EXISTS company_holidays (
            holiday_date       TEXT PRIMARY KEY,    -- ISO date 'YYYY-MM-DD'
            label              TEXT NOT NULL DEFAULT '',
            server_id          TEXT,
            is_synced          INTEGER NOT NULL DEFAULT 0,
            synced_at          TEXT
        );

        -- Derived per-day attendance roll-up (Phase 1: client-owned; server has
        -- its own aggregator for the public T&A view in Phase 2). Never sent to
        -- the server; never deleted client-side. Refreshed every 5 minutes by
        -- AttendanceAggregator (A.8).
        CREATE TABLE IF NOT EXISTS daily_attendance_cache (
            employee_id        TEXT NOT NULL,
            machine_id         TEXT NOT NULL DEFAULT '',
            work_date          TEXT NOT NULL,        -- ISO date 'YYYY-MM-DD' in the employee's tz
            first_active_at    TEXT,
            last_active_at     TEXT,
            active_seconds     INTEGER NOT NULL DEFAULT 0,
            idle_seconds       INTEGER NOT NULL DEFAULT 0,
            off_shift_seconds  INTEGER NOT NULL DEFAULT 0,
            status             TEXT NOT NULL DEFAULT 'unknown',  -- present|late|absent|half_day|unknown
            late_minutes       INTEGER NOT NULL DEFAULT 0,
            updated_at         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')),
            PRIMARY KEY (employee_id, work_date)
        );

        -- (employee_id, work_date) — the AttendanceAggregator's daily-window read
        CREATE INDEX IF NOT EXISTS idx_daily_attendance_cache_employee_date
            ON daily_attendance_cache(employee_id, work_date);

        -- Local clock skew measurement. One row per server URL so a machine that
        -- ever points at multiple server hosts (lab/staging/prod) keeps separate
        -- measurements. Written by LocalTimeSkewService (A.7) on every successful
        -- /auth/check call.
        CREATE TABLE IF NOT EXISTS local_time_skew (
            server_url         TEXT PRIMARY KEY,
            last_measured_at   TEXT NOT NULL,
            skew_seconds       REAL NOT NULL
        );

        -- GPS / location samples (Phase 3, finalplan §16). Synced to server; retained locally.
        CREATE TABLE IF NOT EXISTS location_samples (
            id               TEXT PRIMARY KEY,
            machine_id       TEXT NOT NULL DEFAULT '',
            latitude         REAL NOT NULL,
            longitude        REAL NOT NULL,
            accuracy_m       REAL,
            altitude_m       REAL,
            source           TEXT NOT NULL DEFAULT 'ip',
            address          TEXT,
            captured_at      TEXT NOT NULL,
            is_synced        INTEGER NOT NULL DEFAULT 0,
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );

        CREATE INDEX IF NOT EXISTS idx_location_samples_unsent
            ON location_samples(is_synced, captured_at);

        CREATE INDEX IF NOT EXISTS idx_location_samples_captured
            ON location_samples(captured_at DESC);

        -- Terms & Conditions acceptance gate. One row per (employee, server term).
        -- Equality = (id, terms_version, content_hash): a changed body or bumped version
        -- inserts a NEW pending row so the employee is re-prompted. Accepted rows are
        -- never duplicated and retained as the local audit mirror.
        CREATE TABLE IF NOT EXISTS client_terms (
            id               TEXT NOT NULL,
            feature_id       TEXT NOT NULL DEFAULT '',
            heading          TEXT NOT NULL DEFAULT '',
            body             TEXT NOT NULL DEFAULT '',
            terms_version    TEXT NOT NULL DEFAULT '1.0',
            content_hash     TEXT NOT NULL DEFAULT '',
            sort_order       INTEGER NOT NULL DEFAULT 0,
            is_accepted      INTEGER NOT NULL DEFAULT 0,
            is_user_accepted INTEGER NOT NULL DEFAULT 0,
            accepted_at      TEXT,
            employee_id      TEXT NOT NULL DEFAULT '',
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')),
            updated_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')),
            PRIMARY KEY (id, employee_id)
        );

        CREATE INDEX IF NOT EXISTS idx_client_terms_employee_pending
            ON client_terms(employee_id, is_accepted);
    ";

    internal const string MigrateSql = @"
        -- ── MACHINE_ID COVERAGE (R2, 2026-10-19) ──────────────────────────────
        -- CreateTableSql above only runs on FRESH databases, so every pre-existing
        -- install needs the machine_id columns added here. The single-row
        -- machine_identity table is the source of the value for every INSERT.
        CREATE TABLE IF NOT EXISTS machine_identity (
            id          INTEGER PRIMARY KEY CHECK (id = 1),
            machine_id  TEXT NOT NULL DEFAULT '',
            updated_at  TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );
        ALTER TABLE device_hardware_info ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE storage_devices ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE installed_applications ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE installed_packages ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE network_info ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE hardware_devices ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE session_events ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_status ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE permission_status ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE employee_info ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE location_samples ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE daily_attendance_cache ADD COLUMN machine_id TEXT NOT NULL DEFAULT '';
        -- Backfill every legacy row that already carries a machine id on app_sessions:
        -- its children (app_items) and the session's own sibling telemetry were produced
        -- by the same machine, so the session's machine_id is the honest attribution.
        UPDATE app_items SET machine_id = (
            SELECT s.machine_id FROM app_sessions s WHERE s.id = app_items.app_session_id
        ) WHERE machine_id = '' AND EXISTS (
            SELECT 1 FROM app_sessions s WHERE s.id = app_items.app_session_id AND s.machine_id != ''
        );
        CREATE INDEX IF NOT EXISTS idx_app_items_machine ON app_items(machine_id, opened_at);
        CREATE INDEX IF NOT EXISTS idx_session_events_machine ON session_events(machine_id, event_at);
        ALTER TABLE app_sessions ADD COLUMN process_id INTEGER;
        ALTER TABLE app_sessions ADD COLUMN parent_process_id INTEGER;
        ALTER TABLE app_items ADD COLUMN process_id INTEGER;
        ALTER TABLE installed_applications ADD COLUMN is_browser INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE installed_applications ADD COLUMN desktop_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE installed_applications ADD COLUMN categories TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN url TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN domain TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN object_type TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN action TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN journey_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN sequence INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE app_items ADD COLUMN previous_path TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN current_path TEXT NOT NULL DEFAULT '';
        ALTER TABLE app_items ADD COLUMN window_id INTEGER;
        ALTER TABLE app_items ADD COLUMN tab_id INTEGER;
        ALTER TABLE app_items ADD COLUMN metadata_json TEXT NOT NULL DEFAULT '{}';
        ALTER TABLE app_sessions ADD COLUMN grouped_by TEXT;
        ALTER TABLE app_sessions ADD COLUMN cgroup_scope TEXT;
        ALTER TABLE app_sessions ADD COLUMN context_label TEXT;
        -- Foreground/background focus durations (2026-08-15): the collector knows the
        -- OS foreground window every cycle, so each open session accumulates how long
        -- it held the focus vs ran in the background. Values grow in place and the row
        -- is re-synced so the server learns the totals.
        ALTER TABLE app_sessions ADD COLUMN foreground_seconds REAL NOT NULL DEFAULT 0;
        ALTER TABLE app_sessions ADD COLUMN background_seconds REAL NOT NULL DEFAULT 0;
        -- 3-state lifecycle (2026-09-02): last_activity_at carries the
        -- wall-clock moment of the latest client-side activity on the
        -- session. Refreshed on every collection cycle for open rows;
        -- sent on every sync so the server sweeper can STALE/CLOSED
        -- rows whose trackers stopped reporting.
        ALTER TABLE app_sessions ADD COLUMN last_activity_at TEXT;
        ALTER TABLE network_info ADD COLUMN first_seen_at TEXT;
        ALTER TABLE network_info ADD COLUMN last_seen_at TEXT;
        ALTER TABLE network_info ADD COLUMN is_current INTEGER NOT NULL DEFAULT 1;
        -- Sync of app_status / permission_status to the server (2026-08-11): both tables
        -- gain is_synced so changed rows are re-sent on the next sync roundtrip.
        ALTER TABLE app_status ADD COLUMN is_synced INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE app_status ADD COLUMN synced_at TEXT;
        ALTER TABLE permission_status ADD COLUMN is_synced INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE permission_status ADD COLUMN synced_at TEXT;
        ALTER TABLE employee_info ADD COLUMN device_token TEXT;
        -- Inventory lifecycle v2 is applied by RebuildInventoryTablesIfLegacyAsync (SqliteLogStore):
        -- legacy tables carry install_count / UNIQUE(app_name) from the v1 design and are rebuilt
        -- once into the rows-per-cycle shape (install_date reset to NULL — unknown).
        -- The packages fingerprint index must be NON-unique so a reinstall can open a new row.
        DROP INDEX IF EXISTS idx_installed_packages_fingerprint;
        CREATE INDEX IF NOT EXISTS idx_installed_packages_fingerprint
            ON installed_packages(package_name, source_manager);
        -- Backfill dedup: legacy databases have MULTIPLE is_current=1 rows per IP identity
        -- (one per launch — the pre-fix bug). Collapse them to the most recent per identity
        -- so the current-row contract holds going forward without a manual DB wipe.
        -- Demoted rows re-sync so the server can never show a superseded row as current.
        UPDATE network_info SET is_current = 0, is_synced = 0 WHERE is_current = 1 AND id NOT IN (
            SELECT id FROM (
                SELECT id, ROW_NUMBER() OVER (PARTITION BY public_ip, private_ip ORDER BY collected_at DESC) AS rn
                FROM network_info WHERE is_current = 1
            ) WHERE rn = 1
        );
        CREATE INDEX IF NOT EXISTS idx_network_current
            ON network_info(is_current, collected_at);
        CREATE INDEX IF NOT EXISTS idx_hardware_devices_class
            ON hardware_devices(device_class, plugged_at);
        CREATE INDEX IF NOT EXISTS idx_hardware_devices_open
            ON hardware_devices(unplugged_at, bus_path);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_hardware_devices_open_path
            ON hardware_devices(bus_path) WHERE unplugged_at IS NULL AND bus_path != '';
        -- NOTE: the v1 dedup DELETE + UNIQUE fingerprint index were REMOVED here — the
        -- rows-per-cycle lifecycle model deliberately allows multiple rows per package
        -- (one per install cycle) and a UNIQUE constraint would break reinstall history.
        -- T&A aggregate metadata on session_events (A.9/A.10): used by old_data_dropped
        -- sentinel rows. Normal OS events leave these NULL (aggregation happens at sync).
        ALTER TABLE session_events ADD COLUMN event_count INTEGER;
        ALTER TABLE session_events ADD COLUMN first_at TEXT;
        ALTER TABLE session_events ADD COLUMN last_at TEXT;
        CREATE TABLE IF NOT EXISTS location_samples (
            id               TEXT PRIMARY KEY,
            latitude         REAL NOT NULL,
            longitude        REAL NOT NULL,
            accuracy_m       REAL,
            altitude_m       REAL,
            source           TEXT NOT NULL DEFAULT 'ip',
            address          TEXT,
            captured_at      TEXT NOT NULL,
            is_synced        INTEGER NOT NULL DEFAULT 0,
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
        );
        CREATE INDEX IF NOT EXISTS idx_location_samples_unsent
            ON location_samples(is_synced, captured_at);
        CREATE INDEX IF NOT EXISTS idx_location_samples_captured
            ON location_samples(captured_at DESC);
        -- Terms & Conditions gate (2026-09-16): add the table to EXISTING databases too —
        -- CreateTableSql above only runs on fresh DBs.
        CREATE TABLE IF NOT EXISTS client_terms (
            id               TEXT NOT NULL,
            feature_id       TEXT NOT NULL DEFAULT '',
            heading          TEXT NOT NULL DEFAULT '',
            body             TEXT NOT NULL DEFAULT '',
            terms_version    TEXT NOT NULL DEFAULT '1.0',
            content_hash     TEXT NOT NULL DEFAULT '',
            sort_order       INTEGER NOT NULL DEFAULT 0,
            is_accepted      INTEGER NOT NULL DEFAULT 0,
            is_user_accepted INTEGER NOT NULL DEFAULT 0,
            accepted_at      TEXT,
            employee_id      TEXT NOT NULL DEFAULT '',
            synced_at        TEXT,
            created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')),
            updated_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')),
            PRIMARY KEY (id, employee_id)
        );
        CREATE INDEX IF NOT EXISTS idx_client_terms_employee_pending
            ON client_terms(employee_id, is_accepted);
        -- 2026-09-16 fix - the consent-retry loop treated every is_accepted=0 row as
        -- user agreed but awaiting server ack and auto-accepted terms the user had never
        -- seen. The two states are now separate columns - is_user_accepted=1 (user
        -- clicked agree) is the ONLY state eligible for consent re-sending.
        ALTER TABLE client_terms ADD COLUMN is_user_accepted INTEGER NOT NULL DEFAULT 0;
        -- Data repair (one-time, idempotent): rows the buggy retry loop marked accepted
        -- carry is_accepted=1 with is_user_accepted=0 — the user never clicked agree.
        -- Re-open them so the gate honestly re-prompts.
        UPDATE client_terms SET is_accepted = 0, accepted_at = NULL, synced_at = NULL
        WHERE is_accepted = 1 AND is_user_accepted = 0;
    ";

    // PHASE 1: INSERT STATEMENTS

    internal const string InsertDeviceHardwareInfoSql = @"
        INSERT INTO device_hardware_info
            (id, machine_id, mac_address, hostname, os_name, os_version, cpu_model, cpu_cores,
             ram_total_mb, gpu_model, gpu_vram_mb, collected_at)
        VALUES
            ($id, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $mac_address, $hostname, $os_name, $os_version, $cpu_model, $cpu_cores,
             $ram_total_mb, $gpu_model, $gpu_vram_mb, $collected_at)
    ";

    internal const string InsertStorageDeviceSql = @"
        INSERT INTO storage_devices
            (id, machine_id, device_hardware_id, device_type, model, capacity_mb)
        VALUES
            ($id, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $device_hardware_id, $device_type, $model, $capacity_mb)
    ";

    internal const string MarkDeviceHardwareSentSql = @"
        UPDATE device_hardware_info
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string MarkStorageDevicesSentSql = @"
        UPDATE storage_devices
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string InsertInstalledApplicationSql = @"
        INSERT INTO installed_applications
            (id, machine_id, app_name, binary_name, app_version, publisher, install_path, install_date,
             uninstall_string, change_type, is_installed, uninstall_date,
             is_browser, desktop_id, categories, detected_at)
        VALUES
            ($id, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $app_name, $binary_name, $app_version, $publisher, $install_path, $install_date,
             $uninstall_string, $change_type, $is_installed, $uninstall_date,
             $is_browser, $desktop_id, $categories, $detected_at)
        -- NOTE: app_name is NOT unique (rows-per-cycle model). Upsert-vs-insert is decided in
        -- SqliteLogStore.StoreInstalledApplicationsAsync: an open cycle (uninstall_date IS NULL)
        -- is updated in place; otherwise a NEW cycle row is inserted.
    ";

    internal const string MarkInstalledAppsSentSql = @"
        UPDATE installed_applications
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string InsertInstalledPackageSql = @"
        INSERT INTO installed_packages
            (id, machine_id, package_name, version, category, source_manager, install_path,
             publisher, description, install_date, is_installed, uninstall_date, detected_at)
        VALUES
            ($id, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $package_name, $version, $category, $source_manager, $install_path,
             $publisher, $description, $install_date, $is_installed, $uninstall_date, $detected_at)
        -- NOTE: (package_name, source_manager) is NOT unique (rows-per-cycle model).
    ";

    internal const string MarkInstalledPackagesSentSql = @"
        UPDATE installed_packages
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string InsertNetworkInfoSql = @"
        INSERT INTO network_info
            (id, machine_id, public_ip, private_ip, network_interface_name, collected_at, first_seen_at, last_seen_at, is_current)
        VALUES
            ($id, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $public_ip, $private_ip, $network_interface_name, $collected_at, $first_seen_at, $last_seen_at, $is_current)
    ";

    internal const string MarkNetworkInfoSentSql = @"
        UPDATE network_info
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string InsertHardwareDeviceSql = @"
        INSERT INTO hardware_devices
            (id, machine_id, device_class, vendor, product, serial, bus_path, device_node, plugged_at)
        VALUES
            ($id, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $device_class, $vendor, $product, $serial, $bus_path, $device_node, $plugged_at)
        ON CONFLICT(bus_path) WHERE unplugged_at IS NULL AND bus_path != '' DO NOTHING
    ";

    internal const string InsertSessionEventSql = @"
        INSERT INTO session_events
            (id, machine_id, event_type, os_username, event_at, event_count, first_at, last_at)
        VALUES
            ($id, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $event_type, $os_username, $event_at, $event_count, $first_at, $last_at)
    ";

    internal const string MarkSessionEventsSentSql = @"
        UPDATE session_events
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string InsertLocationSampleSql = @"
        INSERT INTO location_samples
            (id, machine_id, latitude, longitude, accuracy_m, altitude_m, source, address, captured_at)
        VALUES
            ($id, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $latitude, $longitude, $accuracy_m, $altitude_m, $source, $address, $captured_at)
    ";

    internal const string MarkLocationSamplesSentSql = @"
        UPDATE location_samples
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    // PHASE 2: INSERT & QUERY STATEMENTS

    internal const string InsertAppSessionSql = @"
        INSERT INTO app_sessions
            (id, process_name, app_display_name, started_at, ended_at,
             machine_id, employee_id, employee_name, session_id, platform,
             installed_app_id, installed_package_id, process_id, parent_process_id,
             grouped_by, cgroup_scope, context_label, foreground_seconds, background_seconds,
             last_activity_at)
        VALUES
            ($id, $process_name, $app_display_name, $started_at, $ended_at,
             $machine_id, $employee_id, $employee_name, $session_id, $platform,
             $installed_app_id, $installed_package_id, $process_id, $parent_process_id,
             $grouped_by, $cgroup_scope, $context_label, $foreground_seconds, $background_seconds,
             $last_activity_at)
        ON CONFLICT(id) DO UPDATE SET
            ended_at = COALESCE(excluded.ended_at, app_sessions.ended_at),
            parent_process_id = COALESCE(excluded.parent_process_id, app_sessions.parent_process_id),
            -- Refresh last_activity_at on every conflict (the row exists, the
            -- collector re-stored it, the user is still active).
            last_activity_at = COALESCE(excluded.last_activity_at, app_sessions.last_activity_at),
            -- Any re-stored (updated) row must re-sync so the server learns the change
            -- even when the row was already synced (user rule 2026-08-12).
            is_synced = 0
    ";

    internal const string UpdateAppSessionEndedSql = @"
        UPDATE app_sessions
        SET ended_at = $ended_at,
            last_activity_at = $ended_at,
            -- Final focus durations ride along on close when known; NULL keeps the
            -- last flushed value (sessions closed by paths that don't track focus).
            foreground_seconds = COALESCE($foreground_seconds, foreground_seconds),
            background_seconds = COALESCE($background_seconds, background_seconds),
            -- Re-sync on close: a session synced as OPEN must tell the server it ended.
            is_synced = 0,
            synced_at = NULL
        WHERE id = $id
    ";

    /// <summary>Periodic focus-duration flush for open sessions (re-syncs the row so
    /// the server learns the growing totals — rule 2026-08-12: any update re-queues).
    /// ADDITIVE: the in-memory counter holds the DELTA since the last flush and is
    /// cleared after it, so the DB column must accumulate (fg = fg + delta). An
    /// overwrite here would leave the row stuck on the last flush window (~300s /
    /// ~30s) instead of the session total — the root cause of the frozen ~0s values.
    /// The SQLite row is therefore the source of truth for the session total and
    /// survives restarts (SyncService re-sends it verbatim; the server upsert
    /// overwrites with it).</summary>
    internal const string UpdateAppSessionFocusSql = @"
        UPDATE app_sessions
        SET foreground_seconds = COALESCE(foreground_seconds, 0) + $foreground_seconds,
            background_seconds = COALESCE(background_seconds, 0) + $background_seconds,
            -- Touch last_activity_at on focus changes (the user just moved
            -- the focus; the server sweeper should know the tracker is alive).
            -- ZOMBIE-PREVENTION (2026-09-02): only do this for an open session.
            -- A closed session (`ended_at IS NOT NULL`) must not be re-queued
            -- or re-stamped with a fresh activity timestamp — that was the
            -- source of the 26h Stale chrome session bug. Focus updates
            -- arriving after close are still accumulated (the final flush
            -- happens via a dedicated close path, not the focus loop).
            last_activity_at = CASE
                WHEN ended_at IS NULL THEN strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
                ELSE last_activity_at
            END,
            is_synced = CASE
                WHEN ended_at IS NULL THEN 0
                ELSE is_synced
            END
        WHERE id = $id
    ";

    internal const string MarkAppSessionsSentSql = @"
        UPDATE app_sessions
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    // APP ITEMS (generic child of app_sessions)

    internal const string InsertAppItemSql = @"
        INSERT INTO app_items
            (id, machine_id, app_session_id, parent_item_id, item_type, title, identifier, url, domain,
             opened_at, closed_at, process_id,
             object_type, action, journey_id, sequence, previous_path, current_path,
             window_id, tab_id, metadata_json)
        VALUES
            ($id, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $app_session_id, $parent_item_id, $item_type, $title, $identifier, $url, $domain,
             $opened_at, $closed_at, $process_id,
             $object_type, $action, $journey_id, $sequence, $previous_path, $current_path,
             $window_id, $tab_id, $metadata_json)
        ON CONFLICT(id) DO UPDATE SET
            machine_id = COALESCE(NULLIF(excluded.machine_id, ''), app_items.machine_id),
            title = excluded.title,
            identifier = excluded.identifier,
            url = COALESCE(NULLIF(excluded.url, ''), app_items.url),
            domain = COALESCE(NULLIF(excluded.domain, ''), app_items.domain),
            parent_item_id = COALESCE(excluded.parent_item_id, app_items.parent_item_id),
            closed_at = COALESCE(excluded.closed_at, app_items.closed_at),
            object_type = COALESCE(NULLIF(excluded.object_type, ''), app_items.object_type),
            action = COALESCE(NULLIF(excluded.action, ''), app_items.action),
            sequence = excluded.sequence,
            previous_path = COALESCE(NULLIF(excluded.previous_path, ''), app_items.previous_path),
            current_path = COALESCE(NULLIF(excluded.current_path, ''), app_items.current_path),
            metadata_json = excluded.metadata_json,
            -- Any re-stored (updated) item must re-sync (user rule 2026-08-12).
            is_synced = 0
    ";

    internal const string MarkAppItemsSentSql = @"
        UPDATE app_items
        SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')
        WHERE id IN ({0})
    ";

    internal const string GetLastNetworkInfoSql = @"
        SELECT * FROM network_info
        WHERE is_current = 1
        ORDER BY collected_at DESC
        LIMIT 1
    ";

    // UTILITY STATEMENTS

    internal const string UpsertStatusSql = @"
        INSERT INTO app_status (key, machine_id, value, updated_at, is_synced)
        VALUES ($key, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $value, strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'), 0)
        ON CONFLICT(key) DO UPDATE SET
            machine_id = COALESCE(NULLIF(excluded.machine_id, ''), app_status.machine_id),
            value = excluded.value,
            updated_at = excluded.updated_at,
            is_synced = 0
    ";

    internal const string InsertPermissionSql = @"
        INSERT INTO permission_status
            (check_id, machine_id, session_id, session_type, platform, checked_at, method, works, details, employee_id, employee_name, is_synced)
        VALUES
            ($check_id, COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), ''), $session_id, $session_type, $platform, $checked_at, $method, $works, $details, $employee_id, $employee_name, 0)
        ON CONFLICT(check_id) DO UPDATE SET
            machine_id = COALESCE(NULLIF(excluded.machine_id, ''), permission_status.machine_id),
            works = excluded.works,
            details = excluded.details,
            checked_at = excluded.checked_at,
            is_synced = 0
    ";
}
