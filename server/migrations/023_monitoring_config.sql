-- 023_monitoring_config.sql
-- Monitoring configuration domain: app/site types + categories, and classification of
-- the detected application catalog (installed_applications) and observed website domains.
--
--   monitoring_types      → the scalable "type" reference table (Productive/Unproductive/Neutral seeded)
--   monitoring_categories → category reference, scoped by kind (application | website | both)
--   installed_applications→ gains nullable type_id / category_id (existing detected catalog, classified in place)
--   monitoring_sites      → website registry: one row per observed domain (derived from app_items), classifiable

-- ─────────────────────────────────────
-- TYPES
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS monitoring_types (
    id          SERIAL PRIMARY KEY,
    name        VARCHAR(50) UNIQUE NOT NULL,
    color       VARCHAR(20) NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at  TIMESTAMPTZ
);

INSERT INTO monitoring_types (name, color, description) VALUES
    ('Productive',   '#10B981', 'Work-oriented, task-completing usage'),
    ('Unproductive', '#EF4444', 'Distracting, non-work usage'),
    ('Neutral',      '#6B7280', 'Neither clearly productive nor unproductive')
ON CONFLICT (name) DO NOTHING;

DROP TRIGGER IF EXISTS trg_monitoring_types_updated_at ON monitoring_types;
CREATE TRIGGER trg_monitoring_types_updated_at
    BEFORE UPDATE ON monitoring_types
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- ─────────────────────────────────────
-- CATEGORIES (scoped by kind)
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS monitoring_categories (
    id          SERIAL PRIMARY KEY,
    name        VARCHAR(100) UNIQUE NOT NULL,
    kind        VARCHAR(20) NOT NULL DEFAULT 'both'
                CHECK (kind IN ('application', 'website', 'both')),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at  TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_monitoring_categories_kind
    ON monitoring_categories(kind) WHERE deleted_at IS NULL;

DROP TRIGGER IF EXISTS trg_monitoring_categories_updated_at ON monitoring_categories;
CREATE TRIGGER trg_monitoring_categories_updated_at
    BEFORE UPDATE ON monitoring_categories
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- ─────────────────────────────────────
-- CLASSIFY THE DETECTED APP CATALOG IN PLACE
-- (type deletion is blocked while in use; category deletion detaches classification)
-- ─────────────────────────────────────
ALTER TABLE installed_applications ADD COLUMN IF NOT EXISTS type_id     INTEGER REFERENCES monitoring_types(id)     ON DELETE RESTRICT;
ALTER TABLE installed_applications ADD COLUMN IF NOT EXISTS category_id INTEGER REFERENCES monitoring_categories(id) ON DELETE SET NULL;

CREATE INDEX IF NOT EXISTS idx_installed_apps_type
    ON installed_applications(type_id) WHERE type_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_installed_apps_category
    ON installed_applications(category_id) WHERE category_id IS NOT NULL;

-- ─────────────────────────────────────
-- WEBSITE REGISTRY (one row per observed domain)
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS monitoring_sites (
    id            BIGSERIAL PRIMARY KEY,
    domain        VARCHAR(255) NOT NULL,
    type_id       INTEGER REFERENCES monitoring_types(id)     ON DELETE RESTRICT,
    category_id   INTEGER REFERENCES monitoring_categories(id) ON DELETE SET NULL,
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at    TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_monitoring_sites_domain
    ON monitoring_sites(domain) WHERE deleted_at IS NULL;

DROP TRIGGER IF EXISTS trg_monitoring_sites_updated_at ON monitoring_sites;
CREATE TRIGGER trg_monitoring_sites_updated_at
    BEFORE UPDATE ON monitoring_sites
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();