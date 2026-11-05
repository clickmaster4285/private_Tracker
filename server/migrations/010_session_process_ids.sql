-- 010_session_process_ids.sql
-- Persist OS process IDs on app_sessions for hierarchy tracking (node → terminal → IDE)

ALTER TABLE app_sessions ADD COLUMN IF NOT EXISTS process_id INTEGER;
ALTER TABLE app_sessions ADD COLUMN IF NOT EXISTS parent_process_id INTEGER;

CREATE INDEX IF NOT EXISTS idx_app_sessions_process_id
    ON app_sessions(process_id);

CREATE INDEX IF NOT EXISTS idx_app_sessions_parent_process_id
    ON app_sessions(parent_process_id);

CREATE INDEX IF NOT EXISTS idx_app_items_context
    ON app_items(employee_id, item_type, identifier);
