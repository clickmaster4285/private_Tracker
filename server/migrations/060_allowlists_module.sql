-- 060_allowlists_module.sql
-- App allow-list draft + publish snapshot. Default web lockdown draft ON so
-- Publish after install fail-closes the fleet until allow hosts are set.
-- Web allow hosts remain in dlp_lockdown_allow_hosts (059).

CREATE TABLE IF NOT EXISTS dlp_lockdown_allow_apps (
    id         BIGSERIAL PRIMARY KEY,
    publisher  TEXT NOT NULL DEFAULT '',
    product    TEXT NOT NULL DEFAULT '',
    sha256     TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT dlp_lockdown_allow_apps_identity
        CHECK (length(trim(publisher)) > 0 OR length(trim(sha256)) = 64)
);

CREATE INDEX IF NOT EXISTS idx_dlp_lockdown_allow_apps_sha
    ON dlp_lockdown_allow_apps (sha256) WHERE sha256 <> '';

CREATE TABLE IF NOT EXISTS dlp_policy_allow_apps (
    policy_version BIGINT NOT NULL
        REFERENCES dlp_policy_versions(version) ON DELETE CASCADE,
    seq            INT NOT NULL,
    publisher      TEXT NOT NULL DEFAULT '',
    product        TEXT NOT NULL DEFAULT '',
    sha256         TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (policy_version, seq)
);

-- Product default: web lockdown draft is ON (Publish still required to enforce).
UPDATE dlp_lockdown_settings
SET lockdown = TRUE, updated_at = NOW()
WHERE id = 1;
