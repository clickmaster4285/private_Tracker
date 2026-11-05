-- ────────────────────────────────────────────────────────────
-- 031: 3-state app_sessions lifecycle (ACTIVE / STALE / CLOSED)
--
-- Replaces the binary "Running / Closed" model (which kept stale
-- sessions stuck as "Running" forever once a PC went offline, was
-- uninstalled, or never rebooted). The status column + last_activity_at /
-- last_sync_at let a server-side cron transition sessions through:
--   ACTIVE  → STALE  (no sync for > STALE_AFTER_MIN, default 10)
--   STALE   → CLOSED (no sync for > CLOSE_AFTER_HOURS, default 24)
-- A client that reconnects after a long offline window re-uploads
-- activity, refreshes last_sync_at, and the session returns to ACTIVE
-- (only CLOSED is terminal).
-- ────────────────────────────────────────────────────────────

ALTER TABLE app_sessions
    ADD COLUMN IF NOT EXISTS status          TEXT        NOT NULL DEFAULT 'ACTIVE',
    ADD COLUMN IF NOT EXISTS last_activity_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS last_sync_at    TIMESTAMPTZ;

-- Backfill last_sync_at for any pre-existing rows so the new sweeper
-- has a baseline (rows older than the cutoff will be promoted to
-- STALE on the next sweep pass).
UPDATE app_sessions
   SET last_sync_at = COALESCE(synced_at, started_at)
 WHERE last_sync_at IS NULL;

UPDATE app_sessions
   SET last_activity_at = COALESCE(ended_at, started_at)
 WHERE last_activity_at IS NULL;

-- Index supports the sweeper's "WHERE status='ACTIVE' AND last_sync_at < cutoff"
-- and the dashboard's "WHERE status='STALE' ORDER BY last_sync_at DESC".
CREATE INDEX IF NOT EXISTS idx_app_sessions_status_sync
    ON app_sessions(status, last_sync_at DESC);

-- Partial index for the "what's currently open" lookup.
CREATE INDEX IF NOT EXISTS idx_app_sessions_active
    ON app_sessions(employee_id, last_sync_at DESC)
    WHERE status = 'ACTIVE';