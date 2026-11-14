-- Face verify activity (client DeviceAuth sync → admin Face Verify module).
CREATE TABLE IF NOT EXISTS face_verify_events (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_event_id   UUID NOT NULL,
    employee_id       TEXT NOT NULL,
    machine_id        TEXT NOT NULL DEFAULT '',
    mode              TEXT NOT NULL DEFAULT 'simple',
    trigger           TEXT NOT NULL DEFAULT '',
    pose_id           TEXT NOT NULL DEFAULT '',
    result            TEXT NOT NULL,
    score             REAL,
    threshold         REAL,
    detail            TEXT NOT NULL DEFAULT '',
    client_created_at TIMESTAMPTZ NOT NULL,
    synced_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT face_verify_events_client_event_id_key UNIQUE (client_event_id)
);

CREATE INDEX IF NOT EXISTS idx_face_verify_events_employee_created
    ON face_verify_events (employee_id, client_created_at DESC);

CREATE INDEX IF NOT EXISTS idx_face_verify_events_created
    ON face_verify_events (client_created_at DESC);

CREATE INDEX IF NOT EXISTS idx_face_verify_events_result
    ON face_verify_events (result, client_created_at DESC);
