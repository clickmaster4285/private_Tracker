-- Periodic offline-first screenshots (client WebP → media/screenshots/…).
-- Index only — bytes live on disk. No retention purge in this ship.

CREATE TABLE IF NOT EXISTS screenshots (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    employee_id   TEXT NOT NULL,
    machine_id    TEXT NOT NULL DEFAULT '',
    captured_at   TIMESTAMPTZ NOT NULL,
    rel_path      TEXT NOT NULL,
    byte_size     BIGINT NOT NULL DEFAULT 0,
    width         INT NOT NULL DEFAULT 0,
    height        INT NOT NULL DEFAULT 0,
    content_type  TEXT NOT NULL DEFAULT 'image/webp',
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT screenshots_rel_path_unique UNIQUE (rel_path)
);

CREATE INDEX IF NOT EXISTS idx_screenshots_employee_captured
    ON screenshots (employee_id, captured_at DESC);

CREATE INDEX IF NOT EXISTS idx_screenshots_captured
    ON screenshots (captured_at DESC);
