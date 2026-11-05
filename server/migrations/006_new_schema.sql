-- 006_new_schema.sql
-- Add new Phase 1 & Phase 2 tables, drop legacy activity_logs / child tables

-- ─────────────────────────────────────
-- PHASE 1: DEVICE & SYSTEM INFO TABLES
-- ─────────────────────────────────────

CREATE TABLE IF NOT EXISTS device_hardware_info (
    id               TEXT PRIMARY KEY,
    employee_id      VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    device_id        TEXT NOT NULL DEFAULT '',
    mac_address      TEXT NOT NULL DEFAULT '',
    hostname         TEXT NOT NULL DEFAULT '',
    os_name          TEXT NOT NULL DEFAULT '',
    os_version       TEXT NOT NULL DEFAULT '',
    cpu_model        TEXT NOT NULL DEFAULT '',
    cpu_cores        INTEGER NOT NULL DEFAULT 0,
    ram_total_mb     BIGINT NOT NULL DEFAULT 0,
    storage_devices  JSONB NOT NULL DEFAULT '[]',
    gpu_model        TEXT NOT NULL DEFAULT '',
    gpu_vram_mb      BIGINT NOT NULL DEFAULT 0,
    collected_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at        TIMESTAMPTZ,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at       TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_device_hw_employee
    ON device_hardware_info(employee_id, collected_at DESC);

CREATE TABLE IF NOT EXISTS installed_applications (
    id               TEXT PRIMARY KEY,
    employee_id      VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    app_name         TEXT NOT NULL,
    app_version      TEXT NOT NULL DEFAULT '',
    publisher        TEXT NOT NULL DEFAULT '',
    install_path     TEXT NOT NULL DEFAULT '',
    install_date     TIMESTAMPTZ,
    uninstall_string TEXT NOT NULL DEFAULT '',
    change_type      TEXT NOT NULL DEFAULT 'seen',
    detected_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at        TIMESTAMPTZ,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at       TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_installed_apps_employee
    ON installed_applications(employee_id, detected_at DESC);

CREATE INDEX IF NOT EXISTS idx_installed_apps_name
    ON installed_applications(app_name);

CREATE TABLE IF NOT EXISTS network_info (
    id                    TEXT PRIMARY KEY,
    employee_id           VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    public_ip             TEXT NOT NULL DEFAULT '',
    private_ip            TEXT NOT NULL DEFAULT '',
    mac_address           TEXT NOT NULL DEFAULT '',
    network_interface_name TEXT NOT NULL DEFAULT '',
    collected_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at             TIMESTAMPTZ,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at            TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_network_info_employee
    ON network_info(employee_id, collected_at DESC);

CREATE TABLE IF NOT EXISTS session_events (
    id               TEXT PRIMARY KEY,
    employee_id      VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    event_type       TEXT NOT NULL,
    os_username      TEXT NOT NULL DEFAULT '',
    event_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at        TIMESTAMPTZ,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at       TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_session_events_employee
    ON session_events(employee_id, event_at DESC);

-- ─────────────────────────────────────
-- PHASE 2: RELATIONAL APPLICATION LOGS
-- ─────────────────────────────────────

CREATE TABLE IF NOT EXISTS app_sessions (
    id               TEXT PRIMARY KEY,
    employee_id      VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    process_name     TEXT NOT NULL,
    app_display_name TEXT NOT NULL DEFAULT '',
    started_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at         TIMESTAMPTZ,
    machine_id       TEXT NOT NULL DEFAULT '',
    session_id       TEXT NOT NULL DEFAULT '',
    platform         TEXT NOT NULL DEFAULT '',
    synced_at        TIMESTAMPTZ,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at       TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_app_sessions_employee
    ON app_sessions(employee_id, started_at DESC);

CREATE INDEX IF NOT EXISTS idx_app_sessions_timestamp
    ON app_sessions(started_at DESC);

-- ─────────────────────────────────────
-- DROP LEGACY TABLES
-- ─────────────────────────────────────
-- These tables were superseded and are no longer created or used:
--   activity_logs            → removed (migration 003 deleted)
--   browser_contexts         → replaced by app_items (migration 008)
--   urls / url_visits        → replaced by app_items (migration 008)
--   file_explorer_contexts   → replaced by app_items (migration 008)
-- DROP statements are idempotent so legacy databases are cleaned up.
DROP TABLE IF EXISTS browser_contexts CASCADE;
DROP TABLE IF EXISTS url_visits CASCADE;
DROP TABLE IF EXISTS urls CASCADE;
DROP TABLE IF EXISTS file_explorer_contexts CASCADE;
DROP TABLE IF EXISTS activity_logs CASCADE;
