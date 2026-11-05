-- Time & Attendance Phase 2:
-- shift timezone, company holiday calendar, and aggregate-capable session events.

ALTER TABLE shifts
    ADD COLUMN IF NOT EXISTS timezone VARCHAR(64) NOT NULL DEFAULT 'UTC';

CREATE TABLE IF NOT EXISTS company_holidays (
    id           SERIAL PRIMARY KEY,
    holiday_date DATE NOT NULL,
    label        TEXT NOT NULL DEFAULT '',
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at   TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_company_holidays_active_date
    ON company_holidays (holiday_date)
    WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_company_holidays_date
    ON company_holidays (holiday_date DESC);

DROP TRIGGER IF EXISTS trg_company_holidays_updated_at ON company_holidays;
CREATE TRIGGER trg_company_holidays_updated_at
    BEFORE UPDATE ON company_holidays
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

ALTER TABLE session_events
    ADD COLUMN IF NOT EXISTS event_count INTEGER NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS first_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS last_at TIMESTAMPTZ;

ALTER TABLE session_events
    DROP CONSTRAINT IF EXISTS chk_session_events_event_count;
ALTER TABLE session_events
    ADD CONSTRAINT chk_session_events_event_count CHECK (event_count > 0);

UPDATE session_events
SET first_at = COALESCE(first_at, event_at),
    last_at = COALESCE(last_at, event_at)
WHERE first_at IS NULL OR last_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_session_events_employee_type_time
    ON session_events (employee_id, event_type, event_at DESC);
