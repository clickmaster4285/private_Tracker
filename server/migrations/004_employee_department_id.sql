-- 004_employee_department_id.sql
-- Add department_id foreign key to employees table

-- First, add department_id column (nullable initially)
ALTER TABLE employees ADD COLUMN IF NOT EXISTS department_id INTEGER;

-- Migrate existing department names to department_id
UPDATE employees e
SET department_id = d.id
FROM departments d
WHERE e.department = d.name;

-- Set default for any remaining (shouldn't happen, but just in case)
UPDATE employees SET department_id = 1 WHERE department_id IS NULL;

-- Now make it NOT NULL and add FK constraint
ALTER TABLE employees ALTER COLUMN department_id SET NOT NULL;
ALTER TABLE employees ALTER COLUMN department_id SET DEFAULT 1;

-- Add the foreign key constraint
DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint WHERE conname = 'fk_employees_department'
  ) THEN
    ALTER TABLE employees ADD CONSTRAINT fk_employees_department
      FOREIGN KEY (department_id) REFERENCES departments(id);
  END IF;
END $$;

-- Rebuild indexes
CREATE INDEX IF NOT EXISTS idx_employees_department_id ON employees(department_id);

-- Update updated_at trigger
DROP TRIGGER IF EXISTS trg_employees_updated_at ON employees;
CREATE TRIGGER trg_employees_updated_at
  BEFORE UPDATE ON employees
  FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- Update the user_handler.go GetDepartments handler to respond with full department info
-- (This is just a migration note, the code changes are in the Go source files)
