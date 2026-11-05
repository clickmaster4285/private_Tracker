-- 050_machine_id_coverage.sql
-- Complete machine_id coverage on the server: session_events, app_items and location_samples
-- were the last synced tables without it, so an employee who swapped PCs had those rows
-- collapsed into one employee-wide bucket. Every per-machine data surface now carries the
-- dimension (device_hardware_info / network_info / storage_devices / hardware_devices /
-- permission_status / app_status / employee_installed_* already got it in 046; app_sessions
-- has had it since 006).
--
-- terms_consent intentionally stays employee-scoped: a consent is the person accepting terms,
-- not the machine (see the plan's open question Q2). Revisit only if per-machine consent is
-- actually required.
--
-- machine_id stays '' for rows that cannot be attributed — the honest "unknown machine" bucket
-- that the web shows under "All devices" only. The migration is idempotent (IF NOT EXISTS +
-- "WHERE machine_id = ''" guards), so a re-run is a no-op.

ALTER TABLE session_events  ADD COLUMN IF NOT EXISTS machine_id TEXT NOT NULL DEFAULT '';
ALTER TABLE app_items       ADD COLUMN IF NOT EXISTS machine_id TEXT NOT NULL DEFAULT '';
ALTER TABLE location_samples ADD COLUMN IF NOT EXISTS machine_id TEXT NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS idx_session_events_employee_machine
    ON session_events (employee_id, machine_id, event_at DESC);
CREATE INDEX IF NOT EXISTS idx_app_items_employee_machine
    ON app_items (employee_id, machine_id, opened_at DESC);
CREATE INDEX IF NOT EXISTS idx_location_samples_employee_machine
    ON location_samples (employee_id, machine_id, captured_at DESC);

-- ─────────────────────────────────────────────────────────────
-- app_items: exact attribution — the parent app_session already knows its machine.
-- ─────────────────────────────────────────────────────────────
UPDATE app_items i
SET machine_id = s.machine_id
FROM app_sessions s
WHERE i.app_session_id = s.id
  AND i.machine_id = ''
  AND s.machine_id <> '';

-- ─────────────────────────────────────────────────────────────
-- session_events / location_samples: attribute leftover rows from the device registry's own
-- activity window, only when exactly ONE window contains the row's server-stamped time.
-- Same rule as migration 047 — ambiguous rows stay '' rather than guessing.
-- ─────────────────────────────────────────────────────────────
CREATE TEMP TABLE device_windows ON COMMIT DROP AS
SELECT employee_id,
       machine_id,
       MIN(created_at)                         AS win_start,
       MAX(GREATEST(last_seen_at, created_at)) AS win_end
FROM employee_devices
WHERE machine_id <> ''
GROUP BY employee_id, machine_id;

DO $$
DECLARE
    n INTEGER;
BEGIN
    UPDATE session_events t
    SET machine_id = w.machine_id
    FROM device_windows w
    WHERE t.machine_id = ''
      AND w.employee_id = t.employee_id
      AND t.synced_at BETWEEN w.win_start AND w.win_end
      AND NOT EXISTS (
          SELECT 1 FROM device_windows w2
          WHERE w2.employee_id = t.employee_id
            AND w2.machine_id <> w.machine_id
            AND t.synced_at BETWEEN w2.win_start AND w2.win_end
      );
    GET DIAGNOSTICS n = ROW_COUNT;
    RAISE NOTICE '[050] session_events attributed: %', n;

    UPDATE location_samples t
    SET machine_id = w.machine_id
    FROM device_windows w
    WHERE t.machine_id = ''
      AND w.employee_id = t.employee_id
      AND t.synced_at BETWEEN w.win_start AND w.win_end
      AND NOT EXISTS (
          SELECT 1 FROM device_windows w2
          WHERE w2.employee_id = t.employee_id
            AND w2.machine_id <> w.machine_id
            AND t.synced_at BETWEEN w2.win_start AND w2.win_end
      );
    GET DIAGNOSTICS n = ROW_COUNT;
    RAISE NOTICE '[050] location_samples attributed: %', n;
END $$;
