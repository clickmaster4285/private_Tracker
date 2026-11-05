-- 053_dlp_relational.sql
-- Replace DLP JSONB blobs with relational tables.
-- Web Blocker gets its own tables (no longer packed into dlp_rules.match_json).
-- Policy publish snapshots and alert evidence become row-shaped too.

-- ═══════════════════════════════════════════════════════════════════════════
-- WEB BLOCKER (dedicated)
-- ═══════════════════════════════════════════════════════════════════════════

CREATE TABLE IF NOT EXISTS web_blocker_rules (
    id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name               TEXT NOT NULL,
    enabled            BOOLEAN NOT NULL DEFAULT TRUE,
    action             TEXT NOT NULL DEFAULT 'alert'
                       CHECK (action IN ('alert', 'block', 'alert_block')),
    severity           TEXT NOT NULL DEFAULT 'medium'
                       CHECK (severity IN ('critical', 'high', 'medium', 'low')),
    scope_all          BOOLEAN NOT NULL DEFAULT TRUE,
    schedule_enabled   BOOLEAN NOT NULL DEFAULT FALSE,
    schedule_start     TEXT NOT NULL DEFAULT '',
    schedule_end       TEXT NOT NULL DEFAULT '',
    schedule_timezone  TEXT NOT NULL DEFAULT 'UTC',
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at         TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_web_blocker_rules_enabled
    ON web_blocker_rules(enabled) WHERE deleted_at IS NULL;

DROP TRIGGER IF EXISTS trg_web_blocker_rules_updated_at ON web_blocker_rules;
CREATE TRIGGER trg_web_blocker_rules_updated_at
    BEFORE UPDATE ON web_blocker_rules
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

CREATE TABLE IF NOT EXISTS web_blocker_domains (
    id       BIGSERIAL PRIMARY KEY,
    rule_id  UUID NOT NULL REFERENCES web_blocker_rules(id) ON DELETE CASCADE,
    domain   TEXT NOT NULL,
    UNIQUE (rule_id, domain)
);

CREATE INDEX IF NOT EXISTS idx_web_blocker_domains_rule
    ON web_blocker_domains(rule_id);

CREATE TABLE IF NOT EXISTS web_blocker_paths (
    id           BIGSERIAL PRIMARY KEY,
    rule_id      UUID NOT NULL REFERENCES web_blocker_rules(id) ON DELETE CASCADE,
    path_pattern TEXT NOT NULL,
    UNIQUE (rule_id, path_pattern)
);

CREATE INDEX IF NOT EXISTS idx_web_blocker_paths_rule
    ON web_blocker_paths(rule_id);

CREATE TABLE IF NOT EXISTS web_blocker_schedule_days (
    rule_id UUID NOT NULL REFERENCES web_blocker_rules(id) ON DELETE CASCADE,
    day     TEXT NOT NULL
            CHECK (day IN ('mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun')),
    PRIMARY KEY (rule_id, day)
);

-- Migrate existing web_navigate rows out of dlp_rules → web_blocker_*
DO $$
DECLARE
    r RECORD;
    new_id UUID;
    dom TEXT;
    pth TEXT;
    day TEXT;
    sched JSONB;
    matchj JSONB;
BEGIN
    FOR r IN
        SELECT id, name, enabled, action, severity, match_json, scope_json, created_at, updated_at
        FROM dlp_rules
        WHERE deleted_at IS NULL AND trigger = 'web_navigate'
    LOOP
        matchj := COALESCE(r.match_json, '{}'::jsonb);
        sched := COALESCE(matchj->'schedule', '{}'::jsonb);

        INSERT INTO web_blocker_rules (
            id, name, enabled, action, severity, scope_all,
            schedule_enabled, schedule_start, schedule_end, schedule_timezone,
            created_at, updated_at
        ) VALUES (
            r.id,
            r.name,
            r.enabled,
            r.action,
            r.severity,
            COALESCE((r.scope_json->>'all')::boolean, TRUE),
            COALESCE((sched->>'enabled')::boolean, FALSE),
            COALESCE(sched->>'start', ''),
            COALESCE(sched->>'end', ''),
            COALESCE(NULLIF(sched->>'timezone', ''), 'UTC'),
            r.created_at,
            r.updated_at
        )
        ON CONFLICT (id) DO NOTHING;

        new_id := r.id;

        IF jsonb_typeof(matchj->'domains') = 'array' THEN
            FOR dom IN SELECT jsonb_array_elements_text(matchj->'domains')
            LOOP
                INSERT INTO web_blocker_domains (rule_id, domain)
                VALUES (new_id, lower(trim(dom)))
                ON CONFLICT DO NOTHING;
            END LOOP;
        END IF;

        IF jsonb_typeof(matchj->'paths') = 'array' THEN
            FOR pth IN SELECT jsonb_array_elements_text(matchj->'paths')
            LOOP
                INSERT INTO web_blocker_paths (rule_id, path_pattern)
                VALUES (new_id, trim(pth))
                ON CONFLICT DO NOTHING;
            END LOOP;
        END IF;

        IF jsonb_typeof(sched->'days') = 'array' THEN
            FOR day IN SELECT jsonb_array_elements_text(sched->'days')
            LOOP
                IF day IN ('mon','tue','wed','thu','fri','sat','sun') THEN
                    INSERT INTO web_blocker_schedule_days (rule_id, day)
                    VALUES (new_id, day)
                    ON CONFLICT DO NOTHING;
                END IF;
            END LOOP;
        END IF;

        UPDATE dlp_rules SET deleted_at = NOW() WHERE id = r.id AND deleted_at IS NULL;
    END LOOP;
END $$;

-- ═══════════════════════════════════════════════════════════════════════════
-- DLP RULES — relational match + scope (drop JSONB)
-- ═══════════════════════════════════════════════════════════════════════════

ALTER TABLE dlp_rules
    ADD COLUMN IF NOT EXISTS scope_all BOOLEAN NOT NULL DEFAULT TRUE;

UPDATE dlp_rules
SET scope_all = COALESCE((scope_json->>'all')::boolean, TRUE)
WHERE scope_json IS NOT NULL;

CREATE TABLE IF NOT EXISTS dlp_rule_extensions (
    rule_id   UUID NOT NULL REFERENCES dlp_rules(id) ON DELETE CASCADE,
    extension TEXT NOT NULL,
    PRIMARY KEY (rule_id, extension)
);

CREATE TABLE IF NOT EXISTS dlp_rule_patterns (
    rule_id UUID NOT NULL REFERENCES dlp_rules(id) ON DELETE CASCADE,
    pattern TEXT NOT NULL,
    PRIMARY KEY (rule_id, pattern)
);

-- Backfill extensions / patterns from match_json for non-web rules still alive
DO $$
DECLARE
    r RECORD;
    ext TEXT;
    pat TEXT;
    matchj JSONB;
BEGIN
    FOR r IN
        SELECT id, match_json FROM dlp_rules
        WHERE deleted_at IS NULL AND trigger <> 'web_navigate'
    LOOP
        matchj := COALESCE(r.match_json, '{}'::jsonb);
        IF jsonb_typeof(matchj->'extensions') = 'array' THEN
            FOR ext IN SELECT jsonb_array_elements_text(matchj->'extensions')
            LOOP
                INSERT INTO dlp_rule_extensions (rule_id, extension)
                VALUES (r.id, lower(trim(ext)))
                ON CONFLICT DO NOTHING;
            END LOOP;
        END IF;
        IF matchj ? 'pattern' AND NULLIF(trim(matchj->>'pattern'), '') IS NOT NULL THEN
            INSERT INTO dlp_rule_patterns (rule_id, pattern)
            VALUES (r.id, trim(matchj->>'pattern'))
            ON CONFLICT DO NOTHING;
        END IF;
    END LOOP;
END $$;

ALTER TABLE dlp_rules DROP COLUMN IF EXISTS match_json;
ALTER TABLE dlp_rules DROP COLUMN IF EXISTS scope_json;

-- Soft-deleted web_navigate leftovers must leave the trigger enum before CHECK tightens.
UPDATE dlp_rules SET trigger = 'usb_storage' WHERE trigger = 'web_navigate';

ALTER TABLE dlp_rules DROP CONSTRAINT IF EXISTS dlp_rules_trigger_check;
ALTER TABLE dlp_rules ADD CONSTRAINT dlp_rules_trigger_check CHECK (trigger IN (
    'usb_storage', 'usb_file',
    'software_install', 'software_uninstall', 'cloud_sync_folder',
    'file_transfer', 'email'
));

-- ═══════════════════════════════════════════════════════════════════════════
-- POLICY VERSIONS — relational snapshot (drop bundle_json)
-- ═══════════════════════════════════════════════════════════════════════════

CREATE TABLE IF NOT EXISTS dlp_policy_rules (
    policy_version     BIGINT NOT NULL REFERENCES dlp_policy_versions(version) ON DELETE CASCADE,
    rule_id            UUID NOT NULL,
    name               TEXT NOT NULL DEFAULT '',
    trigger            TEXT NOT NULL,
    action             TEXT NOT NULL,
    severity           TEXT NOT NULL,
    alert              BOOLEAN NOT NULL DEFAULT TRUE,
    enabled            BOOLEAN NOT NULL DEFAULT TRUE,
    schedule_enabled   BOOLEAN NOT NULL DEFAULT FALSE,
    schedule_start     TEXT NOT NULL DEFAULT '',
    schedule_end       TEXT NOT NULL DEFAULT '',
    schedule_timezone  TEXT NOT NULL DEFAULT 'UTC',
    PRIMARY KEY (policy_version, rule_id)
);

CREATE TABLE IF NOT EXISTS dlp_policy_domains (
    id             BIGSERIAL PRIMARY KEY,
    policy_version BIGINT NOT NULL REFERENCES dlp_policy_versions(version) ON DELETE CASCADE,
    rule_id        UUID,
    domain         TEXT NOT NULL,
    source         TEXT NOT NULL DEFAULT 'rule'
                   CHECK (source IN ('rule', 'shadow'))
);

CREATE INDEX IF NOT EXISTS idx_dlp_policy_domains_ver
    ON dlp_policy_domains(policy_version);

CREATE TABLE IF NOT EXISTS dlp_policy_paths (
    id             BIGSERIAL PRIMARY KEY,
    policy_version BIGINT NOT NULL REFERENCES dlp_policy_versions(version) ON DELETE CASCADE,
    rule_id        UUID NOT NULL,
    path_pattern   TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_dlp_policy_paths_ver
    ON dlp_policy_paths(policy_version);

CREATE TABLE IF NOT EXISTS dlp_policy_schedule_days (
    policy_version BIGINT NOT NULL REFERENCES dlp_policy_versions(version) ON DELETE CASCADE,
    rule_id        UUID NOT NULL,
    day            TEXT NOT NULL
                   CHECK (day IN ('mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun')),
    PRIMARY KEY (policy_version, rule_id, day)
);

CREATE TABLE IF NOT EXISTS dlp_policy_extensions (
    policy_version BIGINT NOT NULL REFERENCES dlp_policy_versions(version) ON DELETE CASCADE,
    rule_id        UUID NOT NULL,
    extension      TEXT NOT NULL,
    PRIMARY KEY (policy_version, rule_id, extension)
);

-- Best-effort backfill latest bundles from existing bundle_json (optional; new publishes use relational)
DO $$
DECLARE
    v RECORD;
    rule JSONB;
    dom TEXT;
    pth TEXT;
    day TEXT;
    matchj JSONB;
    sched JSONB;
    rid UUID;
BEGIN
    FOR v IN SELECT version, bundle_json FROM dlp_policy_versions WHERE bundle_json IS NOT NULL
    LOOP
        IF jsonb_typeof(v.bundle_json->'rules') = 'array' THEN
            FOR rule IN SELECT * FROM jsonb_array_elements(v.bundle_json->'rules')
            LOOP
                BEGIN
                    rid := (rule->>'id')::uuid;
                EXCEPTION WHEN others THEN
                    CONTINUE;
                END;
                matchj := COALESCE(rule->'match', '{}'::jsonb);
                sched := COALESCE(matchj->'schedule', '{}'::jsonb);
                INSERT INTO dlp_policy_rules (
                    policy_version, rule_id, name, trigger, action, severity, alert, enabled,
                    schedule_enabled, schedule_start, schedule_end, schedule_timezone
                ) VALUES (
                    v.version, rid,
                    COALESCE(rule->>'name', ''),
                    COALESCE(rule->>'trigger', ''),
                    COALESCE(rule->>'action', 'alert'),
                    COALESCE(rule->>'severity', 'medium'),
                    COALESCE((rule->>'alert')::boolean, TRUE),
                    COALESCE((rule->>'enabled')::boolean, TRUE),
                    COALESCE((sched->>'enabled')::boolean, FALSE),
                    COALESCE(sched->>'start', ''),
                    COALESCE(sched->>'end', ''),
                    COALESCE(NULLIF(sched->>'timezone', ''), 'UTC')
                ) ON CONFLICT DO NOTHING;

                IF jsonb_typeof(matchj->'domains') = 'array' THEN
                    FOR dom IN SELECT jsonb_array_elements_text(matchj->'domains')
                    LOOP
                        INSERT INTO dlp_policy_domains (policy_version, rule_id, domain, source)
                        VALUES (v.version, rid, lower(trim(dom)), 'rule');
                    END LOOP;
                END IF;
                IF jsonb_typeof(matchj->'paths') = 'array' THEN
                    FOR pth IN SELECT jsonb_array_elements_text(matchj->'paths')
                    LOOP
                        INSERT INTO dlp_policy_paths (policy_version, rule_id, path_pattern)
                        VALUES (v.version, rid, trim(pth));
                    END LOOP;
                END IF;
                IF jsonb_typeof(matchj->'extensions') = 'array' THEN
                    FOR dom IN SELECT jsonb_array_elements_text(matchj->'extensions')
                    LOOP
                        INSERT INTO dlp_policy_extensions (policy_version, rule_id, extension)
                        VALUES (v.version, rid, lower(trim(dom)))
                        ON CONFLICT DO NOTHING;
                    END LOOP;
                END IF;
                IF jsonb_typeof(sched->'days') = 'array' THEN
                    FOR day IN SELECT jsonb_array_elements_text(sched->'days')
                    LOOP
                        IF day IN ('mon','tue','wed','thu','fri','sat','sun') THEN
                            INSERT INTO dlp_policy_schedule_days (policy_version, rule_id, day)
                            VALUES (v.version, rid, day)
                            ON CONFLICT DO NOTHING;
                        END IF;
                    END LOOP;
                END IF;
            END LOOP;
        END IF;

        IF jsonb_typeof(v.bundle_json#>'{shadowDeny,domains}') = 'array' THEN
            FOR dom IN SELECT jsonb_array_elements_text(v.bundle_json#>'{shadowDeny,domains}')
            LOOP
                INSERT INTO dlp_policy_domains (policy_version, rule_id, domain, source)
                VALUES (v.version, NULL, lower(trim(dom)), 'shadow');
            END LOOP;
        END IF;
    END LOOP;
END $$;

ALTER TABLE dlp_policy_versions DROP COLUMN IF EXISTS bundle_json;

-- ═══════════════════════════════════════════════════════════════════════════
-- ALERT EVIDENCE — relational key/value (drop evidence_json)
-- ═══════════════════════════════════════════════════════════════════════════

CREATE TABLE IF NOT EXISTS dlp_alert_evidence (
    alert_id UUID NOT NULL REFERENCES dlp_alerts(id) ON DELETE CASCADE,
    key      TEXT NOT NULL,
    value    TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (alert_id, key)
);

DO $$
DECLARE
    a RECORD;
    kv RECORD;
BEGIN
    FOR a IN SELECT id, evidence_json FROM dlp_alerts WHERE evidence_json IS NOT NULL AND evidence_json <> '{}'::jsonb
    LOOP
        FOR kv IN SELECT * FROM jsonb_each_text(a.evidence_json)
        LOOP
            INSERT INTO dlp_alert_evidence (alert_id, key, value)
            VALUES (a.id, kv.key, COALESCE(kv.value, ''))
            ON CONFLICT DO NOTHING;
        END LOOP;
    END LOOP;
END $$;

ALTER TABLE dlp_alerts DROP COLUMN IF EXISTS evidence_json;
