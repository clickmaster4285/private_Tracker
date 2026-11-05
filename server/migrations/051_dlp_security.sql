-- 051_dlp_security.sql
-- Security & DLP spine (Windows endpoint): editable rules, immutable published
-- policy bundles, alert inbox, Shadow IT catalog, and one-time removal challenges.
-- See plan.md — P0.

-- ─────────────────────────────────────
-- DLP RULES (admin-editable current set)
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS dlp_rules (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name         TEXT NOT NULL,
    enabled      BOOLEAN NOT NULL DEFAULT TRUE,
    trigger      TEXT NOT NULL
                 CHECK (trigger IN (
                     'usb_storage', 'usb_file', 'web_navigate',
                     'software_install', 'software_uninstall', 'cloud_sync_folder',
                     'file_transfer', 'email'
                 )),
    match_json   JSONB NOT NULL DEFAULT '{}'::jsonb,
    action       TEXT NOT NULL DEFAULT 'alert'
                 CHECK (action IN ('alert', 'block', 'alert_block')),
    severity     TEXT NOT NULL DEFAULT 'medium'
                 CHECK (severity IN ('critical', 'high', 'medium', 'low')),
    scope_json   JSONB NOT NULL DEFAULT '{"all":true}'::jsonb,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at   TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_dlp_rules_enabled
    ON dlp_rules(enabled) WHERE deleted_at IS NULL;

DROP TRIGGER IF EXISTS trg_dlp_rules_updated_at ON dlp_rules;
CREATE TRIGGER trg_dlp_rules_updated_at
    BEFORE UPDATE ON dlp_rules
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- ─────────────────────────────────────
-- IMMUTABLE PUBLISHED POLICY BUNDLES
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS dlp_policy_versions (
    version       BIGSERIAL PRIMARY KEY,
    bundle_json   JSONB NOT NULL,
    bundle_hash   TEXT NOT NULL,
    published_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    published_by  UUID REFERENCES users(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_dlp_policy_versions_published
    ON dlp_policy_versions(published_at DESC);

-- ─────────────────────────────────────
-- DLP ALERTS (device sync → admin inbox)
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS dlp_alerts (
    id              UUID PRIMARY KEY,
    rule_id         UUID,
    policy_version  BIGINT NOT NULL DEFAULT 0,
    employee_id     VARCHAR(50) NOT NULL,
    machine_id      TEXT NOT NULL DEFAULT '',
    type            TEXT NOT NULL DEFAULT '',
    severity        TEXT NOT NULL DEFAULT 'medium'
                    CHECK (severity IN ('critical', 'high', 'medium', 'low')),
    action_taken    TEXT NOT NULL DEFAULT 'alerted'
                    CHECK (action_taken IN ('alerted', 'blocked', 'blocked_failed')),
    occurred_at     TIMESTAMPTZ NOT NULL,
    evidence_json   JSONB NOT NULL DEFAULT '{}'::jsonb,
    status          TEXT NOT NULL DEFAULT 'open'
                    CHECK (status IN ('open', 'investigating', 'resolved', 'false_positive')),
    assigned_to     TEXT NOT NULL DEFAULT '',
    notes           TEXT NOT NULL DEFAULT '',
    synced_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_dlp_alerts_status_occurred
    ON dlp_alerts(status, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_dlp_alerts_employee_occurred
    ON dlp_alerts(employee_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_dlp_alerts_machine
    ON dlp_alerts(machine_id) WHERE machine_id <> '';

DROP TRIGGER IF EXISTS trg_dlp_alerts_updated_at ON dlp_alerts;
CREATE TRIGGER trg_dlp_alerts_updated_at
    BEFORE UPDATE ON dlp_alerts
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- ─────────────────────────────────────
-- SHADOW IT CATALOG
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS shadow_it_apps (
    id                BIGSERIAL PRIMARY KEY,
    name              TEXT NOT NULL,
    name_normalized   TEXT NOT NULL,
    kind              TEXT NOT NULL DEFAULT 'application'
                      CHECK (kind IN ('application', 'website', 'saas')),
    auth_status       TEXT NOT NULL DEFAULT 'under_review'
                      CHECK (auth_status IN ('approved', 'unauthorized', 'under_review')),
    risk              TEXT NOT NULL DEFAULT 'medium'
                      CHECK (risk IN ('high', 'medium', 'low')),
    first_detected_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    user_count        INTEGER NOT NULL DEFAULT 0,
    last_seen_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at        TIMESTAMPTZ,
    UNIQUE (name_normalized)
);

CREATE INDEX IF NOT EXISTS idx_shadow_it_apps_auth
    ON shadow_it_apps(auth_status) WHERE deleted_at IS NULL;

DROP TRIGGER IF EXISTS trg_shadow_it_apps_updated_at ON shadow_it_apps;
CREATE TRIGGER trg_shadow_it_apps_updated_at
    BEFORE UPDATE ON shadow_it_apps
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- ─────────────────────────────────────
-- PROTECTED REMOVAL CHALLENGES
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS removal_challenges (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    machine_id   TEXT NOT NULL,
    employee_id  VARCHAR(50) NOT NULL,
    token_hash   TEXT NOT NULL UNIQUE,
    purpose      TEXT NOT NULL DEFAULT 'both'
                 CHECK (purpose IN ('stop', 'uninstall', 'both')),
    expires_at   TIMESTAMPTZ NOT NULL,
    used_at      TIMESTAMPTZ,
    issued_by    UUID REFERENCES users(id) ON DELETE SET NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_removal_challenges_machine
    ON removal_challenges(machine_id, expires_at DESC)
    WHERE used_at IS NULL;
