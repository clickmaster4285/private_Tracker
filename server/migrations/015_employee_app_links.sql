-- 015_employee_app_links.sql
-- Application catalog dedup + employee↔application junction.
-- Catalog natural identity: (desktop_id, binary_name). Per-install metadata
-- (version, path, install_date, publisher) lives on the junction row.

ALTER TABLE installed_applications ADD COLUMN IF NOT EXISTS app_fingerprint TEXT;

UPDATE installed_applications
SET app_fingerprint = COALESCE(NULLIF(desktop_id, ''), '') || '|' || binary_name
WHERE app_fingerprint IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_installed_applications_fingerprint
    ON installed_applications(app_fingerprint);

CREATE TABLE IF NOT EXISTS employee_installed_applications (
    id                       BIGSERIAL PRIMARY KEY,
    employee_id              VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    installed_application_id TEXT NOT NULL REFERENCES installed_applications(id),
    app_version              TEXT NOT NULL DEFAULT '',
    publisher                TEXT NOT NULL DEFAULT '',
    install_path             TEXT NOT NULL DEFAULT '',
    install_date             TIMESTAMPTZ,
    first_seen_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    is_active                BOOLEAN NOT NULL DEFAULT true,
    UNIQUE (employee_id, installed_application_id)
);

CREATE INDEX IF NOT EXISTS idx_eia_employee ON employee_installed_applications (employee_id);
CREATE INDEX IF NOT EXISTS idx_eia_app      ON employee_installed_applications (installed_application_id);
