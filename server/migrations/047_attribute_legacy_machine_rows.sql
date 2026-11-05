-- 047_attribute_legacy_machine_rows.sql
-- Corrective backfill for 046_device_machine_scope.sql.
--
-- 046 added machine_id (DEFAULT '') to every device-scoped sync table and then tried to
-- attribute the pre-existing rows using app_sessions as the only evidence of "which
-- machines this employee ever used":
--
--     HAVING COUNT(DISTINCT machine_id) = 1
--
-- That single-machine heuristic fails in exactly the scenario 046 was written for — an
-- employee who swapped PCs:
--   * app_sessions is the highest-churn table and the retention worker purges ended
--     sessions after RETENTION_DAYS (30). By the time 046 runs the retired machine's
--     sessions are usually already gone, so the employee looks single-machine (or
--     machine-less) and NOTHING is backfilled; and
--   * the replacement machine may not have synced a single session yet at boot time.
--
-- Observed live: the retired machine's hardware / network / storage / peripheral /
-- permission / status / application / package rows stay machine_id = '' while the
-- Device Specs dropdown — built from employee_devices — still lists the retired machine
-- by its machine id (e.g. the legacy "default-EMP-XXXXX" bound at a login that arrived
-- with no machine id). Selecting that device sends ?deviceId=<machine> and every read
-- filters machine_id = <machine>, matches nothing, and the page renders
-- "No hardware snapshot synced yet".
--
-- This migration re-attributes those leftover rows from the DEVICE REGISTRY's own
-- activity window [created_at, last_seen_at] — the machine that was registered and
-- syncing when the row arrived. A row is attributed only when exactly ONE device window
-- contains its sync time. Ambiguous rows (two devices active at that instant) and rows
-- matching no window stay '' — the honest "unknown machine" bucket, shown under
-- "All devices" only. Rows that already carry a machine_id are never touched, so the
-- migration is idempotent and a re-run is a no-op.

-- Per-machine activity windows. Aggregate per machine so a revoked-then-reissued
-- registration ("default-EMP-10003", "default-HA-139", …) never double-counts.
CREATE TEMP TABLE device_windows ON COMMIT DROP AS
SELECT employee_id,
       machine_id,
       MIN(created_at)                          AS win_start,
       MAX(GREATEST(last_seen_at, created_at))  AS win_end
FROM employee_devices
WHERE machine_id <> ''
GROUP BY employee_id, machine_id;

DO $$
DECLARE
    n INTEGER;
BEGIN
    -- ─────────────────────────────────────────────────────────────
    -- Tables with no natural key beyond their id: attribute every '' row whose
    -- timestamp falls inside exactly one device window.
    -- ─────────────────────────────────────────────────────────────
    UPDATE device_hardware_info t
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
    RAISE NOTICE '[047] device_hardware_info attributed: %', n;

    UPDATE network_info t
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
    RAISE NOTICE '[047] network_info attributed: %', n;

    UPDATE storage_devices t
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
    RAISE NOTICE '[047] storage_devices attributed: %', n;

    UPDATE hardware_devices t
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
    RAISE NOTICE '[047] hardware_devices attributed: %', n;

    -- ─────────────────────────────────────────────────────────────
    -- Tables whose natural key now includes machine_id: only attribute when the
    -- target machine does not already hold the same key (otherwise the UPDATE would
    -- violate the natural key — the already-attributed row wins and the legacy
    -- duplicate stays '' / aggregate-only).
    -- ─────────────────────────────────────────────────────────────
    UPDATE permission_status t
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
      )
      AND NOT EXISTS (
          SELECT 1 FROM permission_status x
          WHERE x.employee_id = t.employee_id
            AND x.check_id = t.check_id
            AND x.machine_id = w.machine_id
      );
    GET DIAGNOSTICS n = ROW_COUNT;
    RAISE NOTICE '[047] permission_status attributed: %', n;

    -- app_status has no synced_at. Use created_at (the server's first-insert stamp) —
    -- updated_at is the CLIENT-reported time (models.AppStatus.UpdatedAt), so it can sit
    -- outside the device window on a machine with clock skew.
    UPDATE app_status t
    SET machine_id = w.machine_id
    FROM device_windows w
    WHERE t.machine_id = ''
      AND w.employee_id = t.employee_id
      AND COALESCE(t.created_at, t.updated_at) BETWEEN w.win_start AND w.win_end
      AND NOT EXISTS (
          SELECT 1 FROM device_windows w2
          WHERE w2.employee_id = t.employee_id
            AND w2.machine_id <> w.machine_id
            AND COALESCE(t.created_at, t.updated_at) BETWEEN w2.win_start AND w2.win_end
      )
      AND NOT EXISTS (
          SELECT 1 FROM app_status x
          WHERE x.employee_id = t.employee_id
            AND x.key = t.key
            AND x.machine_id = w.machine_id
      );
    GET DIAGNOSTICS n = ROW_COUNT;
    RAISE NOTICE '[047] app_status attributed: %', n;

    UPDATE employee_installed_applications t
    SET machine_id = w.machine_id
    FROM device_windows w
    WHERE t.machine_id = ''
      AND w.employee_id = t.employee_id
      AND t.last_seen_at BETWEEN w.win_start AND w.win_end
      AND NOT EXISTS (
          SELECT 1 FROM device_windows w2
          WHERE w2.employee_id = t.employee_id
            AND w2.machine_id <> w.machine_id
            AND t.last_seen_at BETWEEN w2.win_start AND w2.win_end
      )
      AND NOT EXISTS (
          SELECT 1 FROM employee_installed_applications x
          WHERE x.employee_id = t.employee_id
            AND x.installed_application_id = t.installed_application_id
            AND x.machine_id = w.machine_id
      );
    GET DIAGNOSTICS n = ROW_COUNT;
    RAISE NOTICE '[047] employee_installed_applications attributed: %', n;

    UPDATE employee_installed_packages t
    SET machine_id = w.machine_id
    FROM device_windows w
    WHERE t.machine_id = ''
      AND w.employee_id = t.employee_id
      AND t.last_seen_at BETWEEN w.win_start AND w.win_end
      AND NOT EXISTS (
          SELECT 1 FROM device_windows w2
          WHERE w2.employee_id = t.employee_id
            AND w2.machine_id <> w.machine_id
            AND t.last_seen_at BETWEEN w2.win_start AND w2.win_end
      )
      AND NOT EXISTS (
          SELECT 1 FROM employee_installed_packages x
          WHERE x.employee_id = t.employee_id
            AND x.installed_package_id = t.installed_package_id
            AND x.machine_id = w.machine_id
      );
    GET DIAGNOSTICS n = ROW_COUNT;
    RAISE NOTICE '[047] employee_installed_packages attributed: %', n;
END $$;
