-- Triage audit trail for DLP alerts.
-- One row per changed field per Save (status / assigned_to / notes).
-- changed_by is the admin user who clicked Save; name is denormalized so
-- history survives a later user soft-delete.

CREATE TABLE IF NOT EXISTS dlp_alert_events (
    id               BIGSERIAL PRIMARY KEY,
    alert_id         UUID NOT NULL REFERENCES dlp_alerts(id) ON DELETE CASCADE,
    changed_by       UUID,
    changed_by_name  TEXT NOT NULL DEFAULT '',
    field            TEXT NOT NULL
                     CHECK (field IN ('status', 'assigned_to', 'notes')),
    old_value        TEXT NOT NULL DEFAULT '',
    new_value        TEXT NOT NULL DEFAULT '',
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_dlp_alert_events_alert_created
    ON dlp_alert_events(alert_id, created_at DESC);
