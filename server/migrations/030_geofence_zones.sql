-- 030_geofence_zones.sql
-- Phase 3 GPS B.8: geofence zones + enter/exit events on location ingest.

CREATE TABLE IF NOT EXISTS geofence_zones (
    id             SERIAL PRIMARY KEY,
    name           TEXT NOT NULL,
    latitude       DOUBLE PRECISION NOT NULL,
    longitude      DOUBLE PRECISION NOT NULL,
    radius_m       REAL NOT NULL DEFAULT 200,
    alert_on_exit  BOOLEAN NOT NULL DEFAULT true,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at     TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS geofence_events (
    id                  TEXT PRIMARY KEY,
    employee_id         VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    geofence_zone_id    INTEGER NOT NULL REFERENCES geofence_zones(id),
    location_sample_id  TEXT REFERENCES location_samples(id),
    event_type          TEXT NOT NULL CHECK (event_type IN ('enter', 'exit')),
    occurred_at         TIMESTAMPTZ NOT NULL,
    latitude            DOUBLE PRECISION NOT NULL,
    longitude           DOUBLE PRECISION NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_geofence_events_employee
    ON geofence_events (employee_id, occurred_at DESC);

CREATE INDEX IF NOT EXISTS idx_geofence_events_zone
    ON geofence_events (geofence_zone_id, occurred_at DESC);
