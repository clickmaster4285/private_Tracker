-- ────────────────────────────────────────────────────────────
-- 032: Composite index for /app-sessions/usage aggregation
--
-- The new "App Usage" web dashboard page aggregates app_sessions
-- per (employee_id, app_display_name) with date-range filters.
-- A single index on (employee_id, started_at DESC) covers the
-- WHERE clause; the per-app GROUP BY uses the in-memory hash from
-- the filtered rows. app_display_name is added so the planner can
-- skip the sort when the page is grouped by app.
-- ────────────────────────────────────────────────────────────

CREATE INDEX IF NOT EXISTS idx_app_sessions_employee_started_name
    ON app_sessions(employee_id, started_at DESC, app_display_name);
