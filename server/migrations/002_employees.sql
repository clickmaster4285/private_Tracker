-- 002_employees.sql
-- Create separate employees table and migrate existing non-admin users

-- Create employees table
CREATE TABLE IF NOT EXISTS employees (
  id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  employee_id      VARCHAR(20) UNIQUE NOT NULL,
  name             VARCHAR(255) NOT NULL,
  email            VARCHAR(255) NOT NULL DEFAULT '',
  department       VARCHAR(100) NOT NULL DEFAULT 'Engineering',
  role             VARCHAR(50) NOT NULL DEFAULT 'employee',
  shift            VARCHAR(20) NOT NULL DEFAULT 'Day',
  tracking_enabled  BOOLEAN NOT NULL DEFAULT true,
  tracking_status  VARCHAR(20) NOT NULL DEFAULT 'untracked',
  is_online        BOOLEAN NOT NULL DEFAULT false,
  avatar           VARCHAR(10),
  avatar_color     VARCHAR(10),
  created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_employees_employee_id ON employees(employee_id);
CREATE INDEX IF NOT EXISTS idx_employees_email ON employees(email);
CREATE INDEX IF NOT EXISTS idx_employees_department ON employees(department);
CREATE INDEX IF NOT EXISTS idx_employees_role ON employees(role);

-- Migrate existing non-admin users to employees table
INSERT INTO employees (id, employee_id, name, email, department, role, shift,
                       tracking_enabled, tracking_status, is_online, avatar, avatar_color,
                       created_at, updated_at)
SELECT id, employee_id, name, email, department, role, shift,
       tracking_enabled, tracking_status, is_online, avatar, avatar_color,
       created_at, updated_at
FROM users
WHERE is_company_admin = false
  AND NOT EXISTS (SELECT 1 FROM employees e WHERE e.employee_id = users.employee_id);

-- Remove migrated users from users table
DELETE FROM users WHERE is_company_admin = false;

-- Updated_at trigger for employees
DROP TRIGGER IF EXISTS trg_employees_updated_at ON employees;
CREATE TRIGGER trg_employees_updated_at
  BEFORE UPDATE ON employees
  FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
