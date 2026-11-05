-- 018_drop_employee_role.sql
-- Remove the role column from employees — employee role was removed from the
-- product (the API, the web dashboard, and the desktop client no longer model
-- a role for employees). Admin user RBAC on the `users` table is unrelated and
-- unchanged.

ALTER TABLE employees DROP COLUMN IF EXISTS role;

-- Postgres drops indexes on the column automatically, but keep this explicit for
-- databases where the index survived a partial migration.
DROP INDEX IF EXISTS idx_employees_role;
