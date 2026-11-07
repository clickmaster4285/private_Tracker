-- 064_allowlists_app_catalog.sql
-- Catalog-backed app allow entries: admin picks installed_applications;
-- Publish expands to path/publisher/sha identities for the agent.

ALTER TABLE dlp_lockdown_allow_apps
    ADD COLUMN IF NOT EXISTS installed_application_id TEXT,
    ADD COLUMN IF NOT EXISTS app_name TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS install_path TEXT NOT NULL DEFAULT '';

ALTER TABLE dlp_lockdown_allow_apps
    DROP CONSTRAINT IF EXISTS dlp_lockdown_allow_apps_identity;

ALTER TABLE dlp_lockdown_allow_apps
    ADD CONSTRAINT dlp_lockdown_allow_apps_identity CHECK (
        length(trim(publisher)) > 0
        OR length(trim(sha256)) = 64
        OR length(trim(install_path)) > 0
        OR (installed_application_id IS NOT NULL AND length(trim(installed_application_id)) > 0)
    );

CREATE UNIQUE INDEX IF NOT EXISTS idx_dlp_lockdown_allow_apps_catalog
    ON dlp_lockdown_allow_apps (installed_application_id)
    WHERE installed_application_id IS NOT NULL AND length(trim(installed_application_id)) > 0;

CREATE INDEX IF NOT EXISTS idx_dlp_lockdown_allow_apps_app_name
    ON dlp_lockdown_allow_apps (lower(app_name));

ALTER TABLE dlp_policy_allow_apps
    ADD COLUMN IF NOT EXISTS install_path TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS installed_application_id TEXT,
    ADD COLUMN IF NOT EXISTS app_name TEXT NOT NULL DEFAULT '';
