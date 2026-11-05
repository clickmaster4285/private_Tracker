-- 008_app_items.sql
-- Add generic app_items table replacing browser_contexts, file_explorer_contexts, urls, url_visits
-- app_items is a self-referencing generic child table for app_sessions

CREATE TABLE IF NOT EXISTS app_items (
    id                TEXT PRIMARY KEY,
    employee_id       VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    app_session_id    TEXT NOT NULL REFERENCES app_sessions(id),
    parent_item_id    TEXT REFERENCES app_items(id),
    item_type         TEXT NOT NULL DEFAULT '',
    title             TEXT NOT NULL DEFAULT '',
    identifier        TEXT NOT NULL DEFAULT '',
    opened_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    closed_at         TIMESTAMPTZ,
    synced_at         TIMESTAMPTZ,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at        TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_app_items_employee
    ON app_items(employee_id, opened_at DESC);

CREATE INDEX IF NOT EXISTS idx_app_items_app_session
    ON app_items(app_session_id);

CREATE INDEX IF NOT EXISTS idx_app_items_parent
    ON app_items(parent_item_id);
