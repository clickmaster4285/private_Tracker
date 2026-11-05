-- 012_installed_applications_identity.sql
-- Add software identity columns to installed_applications for metadata-driven classification

ALTER TABLE installed_applications ADD COLUMN IF NOT EXISTS binary_name TEXT NOT NULL DEFAULT '';
ALTER TABLE installed_applications ADD COLUMN IF NOT EXISTS is_browser BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE installed_applications ADD COLUMN IF NOT EXISTS desktop_id TEXT NOT NULL DEFAULT '';
ALTER TABLE installed_applications ADD COLUMN IF NOT EXISTS categories TEXT NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS idx_installed_apps_binary
    ON installed_applications(binary_name);
