-- 009_installed_packages.sql
-- Add installed_packages table for CLI tools, runtimes, and libraries
-- Separate from installed_applications which stores only GUI/desktop apps

CREATE TABLE IF NOT EXISTS installed_packages (
    id               TEXT PRIMARY KEY,
    employee_id      VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    package_name     TEXT NOT NULL,
    version          TEXT NOT NULL DEFAULT '',
    category         TEXT NOT NULL DEFAULT 'tool',
    source_manager   TEXT NOT NULL DEFAULT '',
    install_path     TEXT NOT NULL DEFAULT '',
    publisher        TEXT NOT NULL DEFAULT '',
    description      TEXT NOT NULL DEFAULT '',
    detected_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at        TIMESTAMPTZ,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at       TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_installed_packages_employee
    ON installed_packages(employee_id, detected_at DESC);

CREATE INDEX IF NOT EXISTS idx_installed_packages_source
    ON installed_packages(source_manager);

CREATE INDEX IF NOT EXISTS idx_installed_packages_category
    ON installed_packages(category);
