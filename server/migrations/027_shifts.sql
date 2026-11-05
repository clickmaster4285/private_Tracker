-- 027_shifts.sql
-- Shift management: a relational `shifts` catalog + a one-current-shift-per-employee
-- assignment, replacing the legacy denormalized `employees.shift` VARCHAR column.
--
--   shifts             → the scalable shift catalog (name unique, schedule metadata)
--   employees.shift_id → FK to shifts(id) ON DELETE SET NULL (nullable, so an
--                        employee's current shift is "None" when the row is
--                        soft-deleted; existing employees default to ID 1 = Day)
--
-- The legacy `employees.shift` text column is removed in this migration. The
-- shift NAME is resolved ONLY at read time via LEFT JOIN (mirroring the
-- department pattern in 019). All list/return SELECTs project
--   COALESCE(s.name, '') AS shift
-- so the web EmployeeResponse still carries a `shift` field — nothing
-- downstream has to know that it is now a derived name, not a stored string.
--
-- A backfill step attempts to map any pre-existing `shift` value to a
-- matching `shifts.name` (case-insensitive); rows with no match keep
-- shift_id NULL (which renders as empty / "Unassigned" on the web).

-- ─────────────────────────────────────
-- SHIFTS CATALOG
-- ─────────────────────────────────────
CREATE TABLE IF NOT EXISTS shifts (
    id                SERIAL PRIMARY KEY,
    name              VARCHAR(100) UNIQUE NOT NULL,
    start_time        TIME NOT NULL,
    end_time          TIME NOT NULL,
    working_days      VARCHAR(50) NOT NULL DEFAULT 'Mon,Tue,Wed,Thu,Fri',
    grace_minutes     INTEGER NOT NULL DEFAULT 5,
    overtime_hours    INTEGER NOT NULL DEFAULT 8,
    description       TEXT NOT NULL DEFAULT '',
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at        TIMESTAMPTZ
);

DROP TRIGGER IF EXISTS trg_shifts_updated_at ON shifts;
CREATE TRIGGER trg_shifts_updated_at
    BEFORE UPDATE ON shifts
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- Seed the same three default shifts that the old mock page carried. The
-- first INSERT is guaranteed to succeed; ON CONFLICT keeps the migration
-- idempotent on re-run (existing rows are left alone).
INSERT INTO shifts (name, start_time, end_time, working_days, grace_minutes, overtime_hours, description) VALUES
    ('Day Shift',     '09:00', '17:00', 'Mon,Tue,Wed,Thu,Fri',         5,  8, 'Standard 9-to-5 weekday shift'),
    ('Night Shift',   '22:00', '06:00', 'Mon,Tue,Wed,Thu,Fri',        10,  8, 'Overnight coverage'),
    ('Flexible Shift','08:00', '20:00', 'Mon,Tue,Wed,Thu,Fri,Sat',     15, 10, 'Flexible daytime hours incl. Saturday')
ON CONFLICT (name) DO NOTHING;

-- Lock the first seeded row as the default for any new employee.
-- (`employees.shift_id` defaults to NULL; the service falls back to this
-- lookup if the client omits a shift on create.)
SELECT set_config('app.default_shift_id',
    (SELECT id::TEXT FROM shifts WHERE name = 'Day Shift' AND deleted_at IS NULL LIMIT 1),
    false);

-- ─────────────────────────────────────
-- EMPLOYEES.SHIFT_ID (nullable FK)
-- ─────────────────────────────────────
ALTER TABLE employees ADD COLUMN IF NOT EXISTS shift_id INTEGER REFERENCES shifts(id) ON DELETE SET NULL;

CREATE INDEX IF NOT EXISTS idx_employees_shift_id
    ON employees(shift_id) WHERE shift_id IS NOT NULL;

-- Backfill: map legacy `shift` text values to shift_id by name match
-- (case-insensitive). Rows with no match keep shift_id NULL.
DO $$
DECLARE
    migrated INT := 0;
BEGIN
    UPDATE employees e
    SET shift_id = s.id
    FROM shifts s
    WHERE e.shift_id IS NULL
      AND LOWER(TRIM(COALESCE(e.shift, ''))) = LOWER(s.name)
      AND s.deleted_at IS NULL;
    GET DIAGNOSTICS migrated = ROW_COUNT;
    RAISE NOTICE '027_shifts: backfilled % employee.shift_id from legacy shift text', migrated;
END $$;

-- Drop the legacy column now that the FK is the sole source of truth.
-- (Same pattern as 019_drop_employee_department.sql.)
ALTER TABLE employees DROP COLUMN IF EXISTS shift;
