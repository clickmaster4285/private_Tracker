-- 017_status_hardware_permission_storage.sql
-- Sync-surface expansion (2026-08-11): the desktop client now sends four previously
-- local-only tables to the server. All four mirror the client SQLite schema, are keyed
-- idempotently (ON CONFLICT by the client GUID / natural key), and follow the soft-delete
-- convention (deleted_at TIMESTAMPTZ, filtered in queries).

-- app_status — key/value status rows (heartbeat, login state, permission bookmarks).
-- Natural key per employee: (employee_id, key). Ephemeral status, no soft delete needed.
CREATE TABLE IF NOT EXISTS app_status (
    employee_id  VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    key          TEXT NOT NULL,
    value        TEXT NOT NULL DEFAULT '',
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (employee_id, key)
);

CREATE INDEX IF NOT EXISTS idx_app_status_employee ON app_status (employee_id);

-- hardware_devices — USB / peripheral hotplug history (plug-in & plug-out).
CREATE TABLE IF NOT EXISTS hardware_devices (
    id            TEXT PRIMARY KEY,
    employee_id   VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    device_class  TEXT NOT NULL DEFAULT 'other',
    vendor        TEXT NOT NULL DEFAULT '',
    product       TEXT NOT NULL DEFAULT '',
    serial        TEXT NOT NULL DEFAULT '',
    bus_path      TEXT NOT NULL DEFAULT '',
    device_node   TEXT NOT NULL DEFAULT '',
    plugged_at    TIMESTAMPTZ NOT NULL,
    unplugged_at  TIMESTAMPTZ,
    synced_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at    TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_hardware_devices_employee ON hardware_devices (employee_id);
CREATE INDEX IF NOT EXISTS idx_hardware_devices_class    ON hardware_devices (device_class, plugged_at);

-- permission_status — one row per permission method per employee (client dedups to a
-- stable "{platform}_{method}" check_id since 2026-08-11).
CREATE TABLE IF NOT EXISTS permission_status (
    check_id      TEXT PRIMARY KEY,
    employee_id   VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    session_id    TEXT NOT NULL DEFAULT '',
    session_type  TEXT NOT NULL DEFAULT '',
    platform      TEXT NOT NULL DEFAULT '',
    checked_at    TIMESTAMPTZ NOT NULL,
    method        TEXT NOT NULL DEFAULT '',
    works         BOOLEAN NOT NULL DEFAULT false,
    details       TEXT NOT NULL DEFAULT '',
    synced_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at    TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_permission_status_employee ON permission_status (employee_id);

-- storage_devices — relational children of device_hardware_info.
CREATE TABLE IF NOT EXISTS storage_devices (
    id                  TEXT PRIMARY KEY,
    employee_id         VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    device_hardware_id  TEXT NOT NULL REFERENCES device_hardware_info(id),
    device_type         TEXT NOT NULL DEFAULT '',
    model               TEXT NOT NULL DEFAULT '',
    capacity_mb         BIGINT NOT NULL DEFAULT 0,
    synced_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at          TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_storage_devices_employee ON storage_devices (employee_id);
CREATE INDEX IF NOT EXISTS idx_storage_devices_hw       ON storage_devices (device_hardware_id);
