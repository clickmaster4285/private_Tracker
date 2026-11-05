-- 005_soft_delete.sql
-- Add soft delete support to all tables

-- Add deleted_at to users
ALTER TABLE users ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;

-- Add deleted_at to employees
ALTER TABLE employees ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;

-- Add deleted_at to departments
ALTER TABLE departments ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;

-- Indexes for soft delete filtering
CREATE INDEX IF NOT EXISTS idx_users_deleted_at ON users(deleted_at) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_employees_deleted_at ON employees(deleted_at) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_departments_deleted_at ON departments(deleted_at) WHERE deleted_at IS NULL;
