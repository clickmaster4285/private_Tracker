-- 019_drop_employee_department.sql
-- Remove the department name column from employees — employees reference the
-- department exclusively through department_id (FK → departments.id), so the
-- denormalized name is redundant. The API still returns the department name,
-- derived from the JOIN at read time. The `users` table (company-admin accounts)
-- is unrelated and unchanged.

-- The name is restored on every read via `LEFT JOIN departments d ON e.department_id = d.id`,
-- and INSERT/UPDATE now write department_id only. Pre-migration rows keep their FK value,
-- so dropping the column loses no information.

ALTER TABLE employees DROP COLUMN IF EXISTS department;

-- Postgres drops indexes on the column automatically, but keep this explicit for
-- databases where the index survived a partial migration.
DROP INDEX IF EXISTS idx_employees_department;