-- 022_sequence_retention_indexes.sql
-- Fix employee_id sequence cycling and add partial composite indexes for performance.

-- 1. Fix employee sequence ceiling (no cycling, maximum BIGINT limit)
ALTER SEQUENCE IF EXISTS employee_id_seq NO CYCLE MAXVALUE 9223372036854775807;

-- 2. Add partial composite indexes for high-throughput activity queries
CREATE INDEX IF NOT EXISTS idx_app_sessions_emp_started ON app_sessions(employee_id, started_at DESC) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_app_items_session_opened ON app_items(app_session_id, opened_at DESC);
CREATE INDEX IF NOT EXISTS idx_app_items_emp_opened ON app_items(employee_id, opened_at DESC);
