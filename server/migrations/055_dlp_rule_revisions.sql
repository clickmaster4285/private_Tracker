-- 055_dlp_rule_revisions.sql
-- Snapshot each classic DLP rule (USB / install / …) before UPDATE.
-- Extensions / patterns are relational child rows — same shape as live
-- dlp_rule_extensions / dlp_rule_patterns.

CREATE TABLE IF NOT EXISTS dlp_rule_revisions (
    id          BIGSERIAL PRIMARY KEY,
    rule_id     UUID NOT NULL REFERENCES dlp_rules(id) ON DELETE CASCADE,
    revision    INT NOT NULL,
    name        TEXT NOT NULL,
    enabled     BOOLEAN NOT NULL,
    trigger     TEXT NOT NULL,
    action      TEXT NOT NULL,
    severity    TEXT NOT NULL,
    scope_all   BOOLEAN NOT NULL DEFAULT TRUE,
    changed_by  UUID,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (rule_id, revision)
);

CREATE INDEX IF NOT EXISTS idx_dlp_rule_revisions_rule
    ON dlp_rule_revisions(rule_id, revision DESC);

CREATE TABLE IF NOT EXISTS dlp_revision_extensions (
    id           BIGSERIAL PRIMARY KEY,
    revision_id  BIGINT NOT NULL REFERENCES dlp_rule_revisions(id) ON DELETE CASCADE,
    extension    TEXT NOT NULL,
    UNIQUE (revision_id, extension)
);

CREATE INDEX IF NOT EXISTS idx_dlp_revision_extensions_rev
    ON dlp_revision_extensions(revision_id);

CREATE TABLE IF NOT EXISTS dlp_revision_patterns (
    id           BIGSERIAL PRIMARY KEY,
    revision_id  BIGINT NOT NULL REFERENCES dlp_rule_revisions(id) ON DELETE CASCADE,
    pattern      TEXT NOT NULL,
    UNIQUE (revision_id, pattern)
);

CREATE INDEX IF NOT EXISTS idx_dlp_revision_patterns_rev
    ON dlp_revision_patterns(revision_id);
