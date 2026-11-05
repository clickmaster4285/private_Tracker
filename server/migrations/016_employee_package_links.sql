-- 016_employee_package_links.sql
-- Package catalog dedup + employee↔package junction.
-- Catalog natural identity: (package_name, source_manager). Per-install metadata
-- (version, path, publisher) lives on the junction row.

ALTER TABLE installed_packages ADD COLUMN IF NOT EXISTS package_fingerprint TEXT;

UPDATE installed_packages
SET package_fingerprint = package_name || '|' || source_manager
WHERE package_fingerprint IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_installed_packages_fingerprint
    ON installed_packages(package_fingerprint);

CREATE TABLE IF NOT EXISTS employee_installed_packages (
    id                  BIGSERIAL PRIMARY KEY,
    employee_id         VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    installed_package_id TEXT NOT NULL REFERENCES installed_packages(id),
    version             TEXT NOT NULL DEFAULT '',
    publisher           TEXT NOT NULL DEFAULT '',
    install_path        TEXT NOT NULL DEFAULT '',
    first_seen_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    is_active           BOOLEAN NOT NULL DEFAULT true,
    UNIQUE (employee_id, installed_package_id)
);

CREATE INDEX IF NOT EXISTS idx_eip_employee ON employee_installed_packages (employee_id);
CREATE INDEX IF NOT EXISTS idx_eip_package  ON employee_installed_packages (installed_package_id);
