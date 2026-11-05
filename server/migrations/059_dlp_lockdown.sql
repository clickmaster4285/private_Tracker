-- 059_dlp_lockdown.sql
-- Windows default-deny lockdown draft + immutable publish snapshot + per-machine pause.
-- Absent lockdown on older policy versions = today's deny-list behavior.

-- Draft settings (one row). Save here; Publish copies into the policy snapshot.
CREATE TABLE IF NOT EXISTS dlp_lockdown_settings (
    id         SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    lockdown   BOOLEAN NOT NULL DEFAULT FALSE,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
INSERT INTO dlp_lockdown_settings (id, lockdown)
VALUES (1, FALSE)
ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS dlp_lockdown_allow_hosts (
    host       TEXT PRIMARY KEY,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Immutable publish snapshot (per policy version)
CREATE TABLE IF NOT EXISTS dlp_policy_lockdown (
    policy_version BIGINT PRIMARY KEY
        REFERENCES dlp_policy_versions(version) ON DELETE CASCADE,
    lockdown       BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS dlp_policy_allow_hosts (
    policy_version BIGINT NOT NULL
        REFERENCES dlp_policy_versions(version) ON DELETE CASCADE,
    host           TEXT NOT NULL,
    PRIMARY KEY (policy_version, host)
);

CREATE TABLE IF NOT EXISTS dlp_policy_platform_hosts (
    policy_version BIGINT NOT NULL
        REFERENCES dlp_policy_versions(version) ON DELETE CASCADE,
    host           TEXT NOT NULL,
    PRIMARY KEY (policy_version, host)
);

-- Maintenance unlock (machine-scoped; not a fleet control API)
ALTER TABLE employee_machines
    ADD COLUMN IF NOT EXISTS lockdown_paused_until TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS idx_employee_machines_lockdown_pause
    ON employee_machines (lockdown_paused_until)
    WHERE lockdown_paused_until IS NOT NULL;
