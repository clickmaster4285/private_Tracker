-- Company holidays approval workflow: pending | approved | rejected.
-- Existing rows stay approved so schedule/attendance behavior is unchanged.
-- Only approved holidays are mirrored to clients via GET /schedules/me.

ALTER TABLE company_holidays
    ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'approved';

UPDATE company_holidays
SET status = 'approved'
WHERE status IS NULL OR TRIM(status) = '';

ALTER TABLE company_holidays
    DROP CONSTRAINT IF EXISTS chk_company_holidays_status;
ALTER TABLE company_holidays
    ADD CONSTRAINT chk_company_holidays_status
    CHECK (status IN ('pending', 'approved', 'rejected'));

CREATE INDEX IF NOT EXISTS idx_company_holidays_status
    ON company_holidays (status)
    WHERE deleted_at IS NULL;
