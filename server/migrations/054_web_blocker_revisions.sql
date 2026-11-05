-- 054_web_blocker_revisions.sql
-- Snapshot each Web Blocker rule before UPDATE. Domains / paths / schedule days
-- are relational child rows (no TEXT[] / JSONB blobs) — same shape as live
-- web_blocker_domains / web_blocker_paths / web_blocker_schedule_days and the
-- Windows DlpService policy_* tables.

CREATE TABLE IF NOT EXISTS web_blocker_rule_revisions (
    id                 BIGSERIAL PRIMARY KEY,
    rule_id            UUID NOT NULL REFERENCES web_blocker_rules(id) ON DELETE CASCADE,
    revision           INT NOT NULL,
    name               TEXT NOT NULL,
    enabled            BOOLEAN NOT NULL,
    action             TEXT NOT NULL,
    severity           TEXT NOT NULL,
    scope_all          BOOLEAN NOT NULL DEFAULT TRUE,
    schedule_enabled   BOOLEAN NOT NULL DEFAULT FALSE,
    schedule_start     TEXT NOT NULL DEFAULT '',
    schedule_end       TEXT NOT NULL DEFAULT '',
    schedule_timezone  TEXT NOT NULL DEFAULT 'UTC',
    changed_by         UUID,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (rule_id, revision)
);

CREATE INDEX IF NOT EXISTS idx_web_blocker_revisions_rule
    ON web_blocker_rule_revisions(rule_id, revision DESC);

CREATE TABLE IF NOT EXISTS web_blocker_revision_domains (
    id           BIGSERIAL PRIMARY KEY,
    revision_id  BIGINT NOT NULL REFERENCES web_blocker_rule_revisions(id) ON DELETE CASCADE,
    domain       TEXT NOT NULL,
    UNIQUE (revision_id, domain)
);

CREATE INDEX IF NOT EXISTS idx_web_blocker_revision_domains_rev
    ON web_blocker_revision_domains(revision_id);

CREATE TABLE IF NOT EXISTS web_blocker_revision_paths (
    id            BIGSERIAL PRIMARY KEY,
    revision_id   BIGINT NOT NULL REFERENCES web_blocker_rule_revisions(id) ON DELETE CASCADE,
    path_pattern  TEXT NOT NULL,
    UNIQUE (revision_id, path_pattern)
);

CREATE INDEX IF NOT EXISTS idx_web_blocker_revision_paths_rev
    ON web_blocker_revision_paths(revision_id);

CREATE TABLE IF NOT EXISTS web_blocker_revision_schedule_days (
    revision_id  BIGINT NOT NULL REFERENCES web_blocker_rule_revisions(id) ON DELETE CASCADE,
    day          TEXT NOT NULL,
    PRIMARY KEY (revision_id, day)
);

-- Idempotent repair: if an earlier draft of 054 created TEXT[] columns, move
-- values into child tables and drop the arrays.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'web_blocker_rule_revisions' AND column_name = 'domains'
    ) THEN
        INSERT INTO web_blocker_revision_domains (revision_id, domain)
        SELECT r.id, d
        FROM web_blocker_rule_revisions r
        CROSS JOIN LATERAL unnest(COALESCE(r.domains, ARRAY[]::text[])) AS d
        ON CONFLICT (revision_id, domain) DO NOTHING;

        INSERT INTO web_blocker_revision_paths (revision_id, path_pattern)
        SELECT r.id, p
        FROM web_blocker_rule_revisions r
        CROSS JOIN LATERAL unnest(COALESCE(r.paths, ARRAY[]::text[])) AS p
        ON CONFLICT (revision_id, path_pattern) DO NOTHING;

        INSERT INTO web_blocker_revision_schedule_days (revision_id, day)
        SELECT r.id, day
        FROM web_blocker_rule_revisions r
        CROSS JOIN LATERAL unnest(COALESCE(r.schedule_days, ARRAY[]::text[])) AS day
        ON CONFLICT (revision_id, day) DO NOTHING;

        ALTER TABLE web_blocker_rule_revisions
            DROP COLUMN IF EXISTS domains,
            DROP COLUMN IF EXISTS paths,
            DROP COLUMN IF EXISTS schedule_days;
    END IF;
END $$;
