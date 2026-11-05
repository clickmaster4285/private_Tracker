-- 063_allowlists_enabled.sql
-- Per-row Active/Inactive on draft allow hosts and apps. Publish only includes enabled=true.

ALTER TABLE dlp_lockdown_allow_hosts
    ADD COLUMN IF NOT EXISTS enabled BOOLEAN NOT NULL DEFAULT TRUE;

ALTER TABLE dlp_lockdown_allow_apps
    ADD COLUMN IF NOT EXISTS enabled BOOLEAN NOT NULL DEFAULT TRUE;

CREATE INDEX IF NOT EXISTS idx_dlp_lockdown_allow_hosts_enabled
    ON dlp_lockdown_allow_hosts (enabled);

CREATE INDEX IF NOT EXISTS idx_dlp_lockdown_allow_apps_enabled
    ON dlp_lockdown_allow_apps (enabled);
