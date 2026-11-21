-- "Give extra time": per-employee minutes added before the shift start and/or after the
-- shift end, for one day or a date range. The shift itself is unchanged; /schedules/me
-- carries the extension so the PC opens the Windows account earlier / signs out later
-- for that employee only. Soft-deleted rows are ignored.
CREATE TABLE IF NOT EXISTS shift_extensions (
    id             BIGSERIAL PRIMARY KEY,
    employee_id    VARCHAR(20) NOT NULL REFERENCES employees(employee_id) ON DELETE CASCADE,
    start_date     DATE        NOT NULL,
    end_date       DATE        NOT NULL,
    before_minutes INT         NOT NULL DEFAULT 0,
    after_minutes  INT         NOT NULL DEFAULT 0,
    reason         TEXT        NOT NULL DEFAULT '',
    created_by     TEXT        NOT NULL DEFAULT '',
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at     TIMESTAMPTZ,
    CONSTRAINT shift_extensions_dates_chk   CHECK (end_date >= start_date),
    CONSTRAINT shift_extensions_minutes_chk CHECK (
        before_minutes BETWEEN 0 AND 720
        AND after_minutes BETWEEN 0 AND 720
        AND before_minutes + after_minutes > 0)
);

CREATE INDEX IF NOT EXISTS idx_shift_extensions_employee_dates
    ON shift_extensions (employee_id, end_date)
    WHERE deleted_at IS NULL;
