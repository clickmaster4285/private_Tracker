-- App session foreground/background focus durations (2026-08-15).
-- The desktop client knows the OS foreground window every collection cycle and
-- accumulates how long each session's window held the focus (foreground_seconds)
-- vs ran in the background (background_seconds). Rows are re-synced as the
-- values grow and again on close, so the web dashboard can show per-app focus time.
ALTER TABLE app_sessions ADD COLUMN foreground_seconds DOUBLE PRECISION NOT NULL DEFAULT 0;
ALTER TABLE app_sessions ADD COLUMN background_seconds DOUBLE PRECISION NOT NULL DEFAULT 0;
