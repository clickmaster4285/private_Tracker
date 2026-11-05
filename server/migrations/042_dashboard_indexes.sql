-- 042_dashboard_indexes.sql
-- Indexes for GET /api/v1/dashboard/summary aggregates.
-- Proven needed by EXPLAIN on live DB (2026-10-05):
--   org-wide browser_tab date filters used idx_app_items_employee and filtered
--   out ~half the rows; (item_type, opened_at) avoids that scan shape at scale.
--   device last_seen buckets benefit from a partial last_seen index.

CREATE INDEX IF NOT EXISTS idx_app_items_type_opened
    ON app_items (item_type, opened_at DESC)
    WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_employee_devices_last_seen
    ON employee_devices (last_seen_at)
    WHERE revoked_at IS NULL;
