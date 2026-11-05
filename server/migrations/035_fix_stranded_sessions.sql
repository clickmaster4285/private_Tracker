-- Freeze sessions that stopped being synced before the close window
UPDATE app_sessions
   SET status   = 'CLOSED',
       ended_at = COALESCE(last_activity_at, last_sync_at, started_at)
 WHERE ended_at IS NULL
   AND status IN ('OFFLINE','STALE')
   AND last_sync_at < NOW() - make_interval(hours => 24);
