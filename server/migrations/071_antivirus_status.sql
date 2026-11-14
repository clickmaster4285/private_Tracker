-- 071_antivirus_status.sql
-- 24/7 antivirus guard — fleet engine health.
--   antivirus_status : one row per (employee, machine) — latest Microsoft Defender / third-party
--                      AV state reported by the Windows DlpService AntivirusGuardWorker
--                      (POST /api/v1/antivirus/status/sync, DeviceAuth).
-- Detections ("malware") and tamper events ("av_tamper") are NOT stored here: they ride the
-- existing dlp_alerts inbox (type column is free text) so triage/history stay in one place.

CREATE TABLE IF NOT EXISTS antivirus_status (
    id                          BIGSERIAL PRIMARY KEY,
    employee_id                 VARCHAR(20) NOT NULL REFERENCES employees(employee_id) ON DELETE CASCADE,
    machine_id                  TEXT        NOT NULL DEFAULT '',
    health                      TEXT        NOT NULL DEFAULT 'unknown'
                                CHECK (health IN ('protected', 'outdated', 'realtime_off', 'disabled',
                                                  'third_party', 'unavailable', 'unknown')),
    product_name                TEXT        NOT NULL DEFAULT '',
    running_mode                TEXT        NOT NULL DEFAULT '',
    service_enabled             BOOLEAN     NOT NULL DEFAULT FALSE,
    antivirus_enabled           BOOLEAN     NOT NULL DEFAULT FALSE,
    real_time_enabled           BOOLEAN     NOT NULL DEFAULT FALSE,
    behavior_monitor_enabled    BOOLEAN     NOT NULL DEFAULT FALSE,
    on_access_enabled           BOOLEAN     NOT NULL DEFAULT FALSE,
    ioav_enabled                BOOLEAN     NOT NULL DEFAULT FALSE,
    tamper_protected            BOOLEAN     NOT NULL DEFAULT FALSE,
    cloud_enabled               BOOLEAN     NOT NULL DEFAULT FALSE,
    pua_enabled                 BOOLEAN     NOT NULL DEFAULT FALSE,
    network_protection_enabled  BOOLEAN     NOT NULL DEFAULT FALSE,
    signature_version           TEXT        NOT NULL DEFAULT '',
    signature_age_days          INTEGER     NOT NULL DEFAULT 0,
    signature_updated_at        TIMESTAMPTZ,
    quick_scan_at               TIMESTAMPTZ,
    full_scan_at                TIMESTAMPTZ,
    engine_version              TEXT        NOT NULL DEFAULT '',
    third_party_products        TEXT        NOT NULL DEFAULT '',
    exclusion_count             INTEGER     NOT NULL DEFAULT 0,
    guard_enabled               BOOLEAN     NOT NULL DEFAULT TRUE,
    last_enforce_at             TIMESTAMPTZ,
    last_tamper_at              TIMESTAMPTZ,
    tamper_count_24h            INTEGER     NOT NULL DEFAULT 0,
    agent_version               TEXT        NOT NULL DEFAULT '',
    error                       TEXT        NOT NULL DEFAULT '',
    reported_at                 TIMESTAMPTZ NOT NULL DEFAULT NOW(),   -- client clock
    synced_at                   TIMESTAMPTZ NOT NULL DEFAULT NOW(),   -- server clock (staleness)
    created_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT antivirus_status_identity UNIQUE (employee_id, machine_id)
);

CREATE INDEX IF NOT EXISTS idx_antivirus_status_health
    ON antivirus_status (health, synced_at DESC);
CREATE INDEX IF NOT EXISTS idx_antivirus_status_synced
    ON antivirus_status (synced_at DESC);

-- Fast per-machine malware counts for the fleet view (server-projected threats24h).
CREATE INDEX IF NOT EXISTS idx_dlp_alerts_type_machine_occurred
    ON dlp_alerts (type, machine_id, occurred_at DESC);
