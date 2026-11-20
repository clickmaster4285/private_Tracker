-- Shift-account PCs: bind an employee login to the Windows account (SID) it was made from.
-- One physical PC can host several Windows accounts (Shift-Morning / -Evening / -Night);
-- an employee must not be reused from a different account on the same PC.
ALTER TABLE employee_devices ADD COLUMN IF NOT EXISTS windows_sid  TEXT NOT NULL DEFAULT '';
ALTER TABLE employee_devices ADD COLUMN IF NOT EXISTS windows_user TEXT NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS idx_employee_devices_binding
    ON employee_devices (employee_id, lower(device_name))
    WHERE revoked_at IS NULL AND windows_sid <> '';
