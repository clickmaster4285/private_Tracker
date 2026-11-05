-- 014_app_items_journey.sql
-- Add journey tracking fields + process_id to app_items (from client journey engine).

ALTER TABLE app_items ADD COLUMN IF NOT EXISTS process_id INTEGER;
ALTER TABLE app_items ADD COLUMN IF NOT EXISTS object_type TEXT NOT NULL DEFAULT '';
ALTER TABLE app_items ADD COLUMN IF NOT EXISTS action TEXT NOT NULL DEFAULT '';
ALTER TABLE app_items ADD COLUMN IF NOT EXISTS journey_id TEXT NOT NULL DEFAULT '';
ALTER TABLE app_items ADD COLUMN IF NOT EXISTS sequence INTEGER NOT NULL DEFAULT 0;
ALTER TABLE app_items ADD COLUMN IF NOT EXISTS previous_path TEXT NOT NULL DEFAULT '';
ALTER TABLE app_items ADD COLUMN IF NOT EXISTS current_path TEXT NOT NULL DEFAULT '';
ALTER TABLE app_items ADD COLUMN IF NOT EXISTS window_id INTEGER;
ALTER TABLE app_items ADD COLUMN IF NOT EXISTS tab_id INTEGER;
ALTER TABLE app_items ADD COLUMN IF NOT EXISTS metadata_json TEXT NOT NULL DEFAULT '{}';

CREATE INDEX IF NOT EXISTS idx_app_items_journey_seq
    ON app_items(journey_id, sequence);
