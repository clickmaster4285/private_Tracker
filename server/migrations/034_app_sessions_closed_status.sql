-- 034: repair historical app-session lifecycle contradictions.
--
-- A first sync that already contained ended_at used the status column's
-- DEFAULT 'ACTIVE' because BulkInsertAppSessions did not explicitly insert a
-- status. These rows rendered as both "Running" and closed in the dashboard.
-- The forward writer now supplies CLOSED for such rows; this idempotent repair
-- aligns only historical rows that already have a client-provided end time.

UPDATE app_sessions
   SET status = 'CLOSED'
 WHERE ended_at IS NOT NULL
   AND status IS DISTINCT FROM 'CLOSED';
