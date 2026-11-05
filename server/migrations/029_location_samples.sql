-- 029_location_samples.sql
-- Phase 3 GPS & Location: desktop client location fixes synced via DeviceAuth.

CREATE TABLE IF NOT EXISTS location_samples (
    id            TEXT PRIMARY KEY,
    employee_id   VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    latitude      DOUBLE PRECISION NOT NULL,
    longitude     DOUBLE PRECISION NOT NULL,
    accuracy_m    REAL,
    altitude_m    REAL,
    source        TEXT NOT NULL DEFAULT 'ip',
    address       TEXT,
    captured_at   TIMESTAMPTZ NOT NULL,
    synced_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at    TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_location_samples_employee
    ON location_samples (employee_id, captured_at DESC);

CREATE INDEX IF NOT EXISTS idx_location_samples_captured
    ON location_samples (captured_at DESC);
