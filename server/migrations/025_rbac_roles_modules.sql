-- 025_rbac_roles_modules.sql
-- Dynamic RBAC: roles, modules, submodules and per-role submodule permissions.
--
--   roles                      → role reference; `company_admin` is a seeded SYSTEM role (full access)
--   modules                    → navigation module groups (General, HR, Monitoring, Settings, ...)
--   submodules                 → concrete permission keys under a module (dashboard, users, settings/user-management, ...)
--   role_submodule_permissions → junction: a granted (role, submodule) pair means "allowed"
--   users                      → `role_id` becomes the ONLY role source of truth;
--                                legacy `role`/`department`/`is_company_admin` columns are dropped
--
-- The company_admin role is seeded HERE so the users.role_id backfill below has a target;
-- the Go seeder re-ensures the full module/submodule catalog + grants on every boot.

-- ─────────────────────────────────────
-- ROLES
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS roles (
    id          SERIAL PRIMARY KEY,
    name        VARCHAR(100) UNIQUE NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    is_system   BOOLEAN NOT NULL DEFAULT false,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at  TIMESTAMPTZ
);

INSERT INTO roles (name, description, is_system) VALUES
    ('company_admin', 'Full access to every module and submodule', true)
ON CONFLICT (name) DO NOTHING;

DROP TRIGGER IF EXISTS trg_roles_updated_at ON roles;
CREATE TRIGGER trg_roles_updated_at
    BEFORE UPDATE ON roles
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

CREATE INDEX IF NOT EXISTS idx_roles_deleted_at ON roles(deleted_at) WHERE deleted_at IS NULL;

-- ─────────────────────────────────────
-- MODULES (navigation groups)
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS modules (
    id         SERIAL PRIMARY KEY,
    key        VARCHAR(100) UNIQUE NOT NULL,
    name       VARCHAR(100) NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

DROP TRIGGER IF EXISTS trg_modules_updated_at ON modules;
CREATE TRIGGER trg_modules_updated_at
    BEFORE UPDATE ON modules
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- ─────────────────────────────────────
-- SUBMODULES (permission keys)
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS submodules (
    id          SERIAL PRIMARY KEY,
    module_id   INTEGER NOT NULL REFERENCES modules(id) ON DELETE CASCADE,
    key         VARCHAR(100) UNIQUE NOT NULL,
    name        VARCHAR(100) NOT NULL,
    route_path  VARCHAR(200) NOT NULL DEFAULT '',
    sort_order  INTEGER NOT NULL DEFAULT 0,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_submodules_module ON submodules(module_id);

DROP TRIGGER IF EXISTS trg_submodules_updated_at ON submodules;
CREATE TRIGGER trg_submodules_updated_at
    BEFORE UPDATE ON submodules
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- ─────────────────────────────────────
-- ROLE ↔ SUBMODULE PERMISSIONS
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS role_submodule_permissions (
    role_id      INTEGER NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    submodule_id INTEGER NOT NULL REFERENCES submodules(id) ON DELETE CASCADE,
    granted_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (role_id, submodule_id)
);

CREATE INDEX IF NOT EXISTS idx_rsp_submodule ON role_submodule_permissions(submodule_id);

-- ─────────────────────────────────────
-- USERS → role_id as single source of truth
-- ─────────────────────────────────────
ALTER TABLE users ADD COLUMN IF NOT EXISTS role_id INTEGER REFERENCES roles(id);

-- Backfill: admins flagged via is_company_admin keep that identity through the new FK.
UPDATE users
SET role_id = (SELECT id FROM roles WHERE name = 'company_admin')
WHERE role_id IS NULL;

ALTER TABLE users ALTER COLUMN role_id SET NOT NULL;

CREATE INDEX IF NOT EXISTS idx_users_role_id ON users(role_id);

-- Drop the denormalized columns + their indexes (mirrors the 018/019 pattern).
DROP INDEX IF EXISTS idx_users_role;
DROP INDEX IF EXISTS idx_users_department;
DROP INDEX IF EXISTS idx_users_company_admin;

ALTER TABLE users DROP COLUMN IF EXISTS role;
ALTER TABLE users DROP COLUMN IF EXISTS department;
ALTER TABLE users DROP COLUMN IF EXISTS is_company_admin;
