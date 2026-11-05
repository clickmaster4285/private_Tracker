-- 046_device_machine_scope.sql
-- Per-device scoping for the synced machine data surfaces.
--
-- Until now only app_sessions carried machine_id, so an employee who swapped PCs had
-- every hardware / network / peripheral / permission / software row collapsed into one
-- employee-wide bucket and the web Device Specs module could not tell the two machines
-- apart. The desktop client authenticates every sync with its device token, so the
-- server can stamp the device's machine_id on each row WITHOUT any client change.
--
-- machine_id stays '' for rows that predate this migration and cannot be attributed
-- (see the single-device backfill below); the web shows those only under "All devices".

ALTER TABLE device_hardware_info ADD COLUMN IF NOT EXISTS machine_id TEXT NOT NULL DEFAULT '';
ALTER TABLE network_info         ADD COLUMN IF NOT EXISTS machine_id TEXT NOT NULL DEFAULT '';
ALTER TABLE storage_devices      ADD COLUMN IF NOT EXISTS machine_id TEXT NOT NULL DEFAULT '';
ALTER TABLE hardware_devices     ADD COLUMN IF NOT EXISTS machine_id TEXT NOT NULL DEFAULT '';
ALTER TABLE permission_status    ADD COLUMN IF NOT EXISTS machine_id TEXT NOT NULL DEFAULT '';
ALTER TABLE app_status           ADD COLUMN IF NOT EXISTS machine_id TEXT NOT NULL DEFAULT '';

ALTER TABLE employee_installed_applications ADD COLUMN IF NOT EXISTS machine_id TEXT NOT NULL DEFAULT '';
ALTER TABLE employee_installed_packages     ADD COLUMN IF NOT EXISTS machine_id TEXT NOT NULL DEFAULT '';

-- ─────────────────────────────────────
-- Backfill: an employee with exactly ONE machine ever seen in app_sessions can only
-- have produced rows from that machine, so attribute their legacy rows to it.
-- Employees with two or more machines stay unattributed ('' = honest "unknown machine").
-- ─────────────────────────────────────
CREATE TEMP TABLE single_machine_employees ON COMMIT DROP AS
SELECT employee_id, MIN(machine_id) AS machine_id
FROM app_sessions
WHERE machine_id <> '' AND deleted_at IS NULL
GROUP BY employee_id
HAVING COUNT(DISTINCT machine_id) = 1;

UPDATE device_hardware_info d
SET machine_id = m.machine_id
FROM single_machine_employees m
WHERE d.employee_id = m.employee_id AND d.machine_id = '';

UPDATE network_info n
SET machine_id = m.machine_id
FROM single_machine_employees m
WHERE n.employee_id = m.employee_id AND n.machine_id = '';

UPDATE storage_devices s
SET machine_id = m.machine_id
FROM single_machine_employees m
WHERE s.employee_id = m.employee_id AND s.machine_id = '';

UPDATE hardware_devices h
SET machine_id = m.machine_id
FROM single_machine_employees m
WHERE h.employee_id = m.employee_id AND h.machine_id = '';

UPDATE permission_status p
SET machine_id = m.machine_id
FROM single_machine_employees m
WHERE p.employee_id = m.employee_id AND p.machine_id = '';

UPDATE app_status a
SET machine_id = m.machine_id
FROM single_machine_employees m
WHERE a.employee_id = m.employee_id AND a.machine_id = '';

UPDATE employee_installed_applications l
SET machine_id = m.machine_id
FROM single_machine_employees m
WHERE l.employee_id = m.employee_id AND l.machine_id = '';

UPDATE employee_installed_packages l
SET machine_id = m.machine_id
FROM single_machine_employees m
WHERE l.employee_id = m.employee_id AND l.machine_id = '';

-- ─────────────────────────────────────
-- Natural keys that were employee-wide must become device-wide, otherwise the second
-- machine of the same employee overwrites the first machine's row.
-- ─────────────────────────────────────

-- permission_status: check_id is "{platform}_{method}" and collided globally.
ALTER TABLE permission_status DROP CONSTRAINT IF EXISTS permission_status_pkey;
ALTER TABLE permission_status ADD PRIMARY KEY (employee_id, machine_id, check_id);

-- app_status: natural key becomes (employee_id, machine_id, key).
ALTER TABLE app_status DROP CONSTRAINT IF EXISTS app_status_pkey;
ALTER TABLE app_status ADD PRIMARY KEY (employee_id, machine_id, key);

-- junction links: one row per (employee, machine, catalog item).
-- NOTE: PostgreSQL truncates identifiers to 63 chars, so the auto-generated names are
-- short forms of the column list. Drop ANY unique constraint on the old column pair
-- (looked up dynamically) so a hand-renamed database still migrates cleanly.
DO $$
DECLARE c record;
BEGIN
    FOR c IN
        SELECT conname FROM pg_constraint
        WHERE conrelid = 'employee_installed_applications'::regclass
          AND contype = 'u'
          AND pg_get_constraintdef(oid) ILIKE '%(employee_id, installed_application_id)%'
    LOOP
        EXECUTE format('ALTER TABLE employee_installed_applications DROP CONSTRAINT %I', c.conname);
    END LOOP;

    FOR c IN
        SELECT conname FROM pg_constraint
        WHERE conrelid = 'employee_installed_packages'::regclass
          AND contype = 'u'
          AND pg_get_constraintdef(oid) ILIKE '%(employee_id, installed_package_id)%'
    LOOP
        EXECUTE format('ALTER TABLE employee_installed_packages DROP CONSTRAINT %I', c.conname);
    END LOOP;
END $$;

ALTER TABLE employee_installed_applications
    ADD CONSTRAINT eia_employee_machine_app_key UNIQUE (employee_id, machine_id, installed_application_id);

ALTER TABLE employee_installed_packages
    ADD CONSTRAINT eip_employee_machine_package_key UNIQUE (employee_id, machine_id, installed_package_id);

-- ─────────────────────────────────────
-- Indexes for the per-device detail reads.
-- ─────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_device_hw_employee_machine
    ON device_hardware_info (employee_id, machine_id, collected_at DESC);
CREATE INDEX IF NOT EXISTS idx_network_info_employee_machine
    ON network_info (employee_id, machine_id, collected_at DESC);
CREATE INDEX IF NOT EXISTS idx_storage_devices_employee_machine
    ON storage_devices (employee_id, machine_id);
CREATE INDEX IF NOT EXISTS idx_hardware_devices_employee_machine
    ON hardware_devices (employee_id, machine_id, plugged_at DESC);
CREATE INDEX IF NOT EXISTS idx_permission_status_employee_machine
    ON permission_status (employee_id, machine_id);
CREATE INDEX IF NOT EXISTS idx_eia_employee_machine
    ON employee_installed_applications (employee_id, machine_id);
CREATE INDEX IF NOT EXISTS idx_eip_employee_machine
    ON employee_installed_packages (employee_id, machine_id);
