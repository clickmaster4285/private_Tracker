-- 013_session_identity.sql
-- Add client-side session identity columns to app_sessions.
-- No FK constraints: a session may reference an app/package row that was never synced,
-- and a hard FK would break the ingestion pipeline on out-of-order syncs.

ALTER TABLE app_sessions ADD COLUMN IF NOT EXISTS installed_app_id TEXT;
ALTER TABLE app_sessions ADD COLUMN IF NOT EXISTS installed_package_id TEXT;
ALTER TABLE app_sessions ADD COLUMN IF NOT EXISTS grouped_by TEXT;
ALTER TABLE app_sessions ADD COLUMN IF NOT EXISTS cgroup_scope TEXT;
ALTER TABLE app_sessions ADD COLUMN IF NOT EXISTS context_label TEXT;

CREATE INDEX IF NOT EXISTS idx_app_sessions_employee_started
    ON app_sessions(employee_id, started_at);
