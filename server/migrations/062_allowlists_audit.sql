-- 062_allowlists_audit.sql
-- Per-row allow-list identity + audit (who created/updated) for admin list UI.

-- Web allow hosts: host was PK; rebuild with id + audit columns.
CREATE TABLE IF NOT EXISTS dlp_lockdown_allow_hosts_v2 (
    id         BIGSERIAL PRIMARY KEY,
    host       TEXT NOT NULL,
    note       TEXT NOT NULL DEFAULT '',
    created_by UUID REFERENCES users(id) ON DELETE SET NULL,
    updated_by UUID REFERENCES users(id) ON DELETE SET NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT dlp_lockdown_allow_hosts_v2_host_uq UNIQUE (host)
);

INSERT INTO dlp_lockdown_allow_hosts_v2 (host, created_at)
SELECT host, COALESCE(created_at, NOW())
FROM dlp_lockdown_allow_hosts
ON CONFLICT (host) DO NOTHING;

DROP TABLE IF EXISTS dlp_lockdown_allow_hosts;
ALTER TABLE dlp_lockdown_allow_hosts_v2 RENAME TO dlp_lockdown_allow_hosts;

CREATE INDEX IF NOT EXISTS idx_dlp_lockdown_allow_hosts_created
    ON dlp_lockdown_allow_hosts (created_at DESC);
CREATE INDEX IF NOT EXISTS idx_dlp_lockdown_allow_hosts_created_by
    ON dlp_lockdown_allow_hosts (created_by)
    WHERE created_by IS NOT NULL;

ALTER TABLE dlp_lockdown_allow_apps
    ADD COLUMN IF NOT EXISTS note TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS created_by UUID REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS updated_by UUID REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

CREATE INDEX IF NOT EXISTS idx_dlp_lockdown_allow_apps_created
    ON dlp_lockdown_allow_apps (created_at DESC);
CREATE INDEX IF NOT EXISTS idx_dlp_lockdown_allow_apps_created_by
    ON dlp_lockdown_allow_apps (created_by)
    WHERE created_by IS NOT NULL;
