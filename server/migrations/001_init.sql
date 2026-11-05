-- 001_init.sql
-- Alpha AI Tracker - Initial Schema

-- Employee ID sequence for EMP-XXXXX format
CREATE SEQUENCE IF NOT EXISTS employee_id_seq
  START WITH 10001
  INCREMENT BY 1
  MINVALUE 10001
  MAXVALUE 99999
  CYCLE;

-- Users table (core authentication & employee profiles)
CREATE TABLE IF NOT EXISTS users (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  employee_id     VARCHAR(20) UNIQUE NOT NULL DEFAULT ('EMP-' || LPAD(NEXTVAL('employee_id_seq')::TEXT, 5, '0')),
  name            VARCHAR(255) NOT NULL,
  email           VARCHAR(255) UNIQUE NOT NULL,
  password_hash   VARCHAR(255) NOT NULL,
  role            VARCHAR(50) NOT NULL DEFAULT 'employee',
  department      VARCHAR(100) NOT NULL DEFAULT 'Engineering',
  shift           VARCHAR(20) NOT NULL DEFAULT 'Day',
  tracking_enabled BOOLEAN NOT NULL DEFAULT true,
  tracking_status VARCHAR(20) NOT NULL DEFAULT 'untracked',
  is_online       BOOLEAN NOT NULL DEFAULT false,
  avatar          VARCHAR(10),
  avatar_color    VARCHAR(10),
  is_company_admin BOOLEAN NOT NULL DEFAULT false,
  created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_users_email ON users(email);
CREATE INDEX IF NOT EXISTS idx_users_employee_id ON users(employee_id);
CREATE INDEX IF NOT EXISTS idx_users_department ON users(department);
CREATE INDEX IF NOT EXISTS idx_users_role ON users(role);
CREATE INDEX IF NOT EXISTS idx_users_company_admin ON users(is_company_admin) WHERE is_company_admin = true;

-- Departments reference table
CREATE TABLE IF NOT EXISTS departments (
  id    SERIAL PRIMARY KEY,
  name  VARCHAR(100) UNIQUE NOT NULL
);

-- Seed default departments
INSERT INTO departments (name) VALUES
  ('Engineering'), ('Design'), ('Marketing'), ('Sales'),
  ('HR'), ('Finance'), ('QA'), ('DevOps')
ON CONFLICT (name) DO NOTHING;

-- Updated_at trigger
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = NOW();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_users_updated_at ON users;
CREATE TRIGGER trg_users_updated_at
  BEFORE UPDATE ON users
  FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- Auto employee_id trigger for manual inserts without employee_id
CREATE OR REPLACE FUNCTION auto_employee_id()
RETURNS TRIGGER AS $$
BEGIN
  IF NEW.employee_id IS NULL OR NEW.employee_id = '' THEN
    NEW.employee_id := 'EMP-' || LPAD(NEXTVAL('employee_id_seq')::TEXT, 5, '0');
  END IF;
  IF NEW.avatar IS NULL THEN
    NEW.avatar := UPPER(LEFT(SPLIT_PART(NEW.name, ' ', 1), 1) || COALESCE(LEFT(SPLIT_PART(NEW.name, ' ', 2), 1), ''));
  END IF;
  IF NEW.avatar_color IS NULL THEN
    NEW.avatar_color := (
      CASE (FLOOR(RANDOM() * 10))::INT
        WHEN 0 THEN '#7C3AED' WHEN 1 THEN '#EC4899' WHEN 2 THEN '#F59E0B'
        WHEN 3 THEN '#10B981' WHEN 4 THEN '#3B82F6' WHEN 5 THEN '#EF4444'
        WHEN 6 THEN '#8B5CF6' WHEN 7 THEN '#06B6D4' WHEN 8 THEN '#F97316'
        ELSE '#14B8A6'
      END
    );
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_users_auto_fields ON users;
CREATE TRIGGER trg_users_auto_fields
  BEFORE INSERT ON users
  FOR EACH ROW EXECUTE FUNCTION auto_employee_id();
