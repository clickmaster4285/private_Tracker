-- 033_synced_at_backfill.sql
-- Root cause (2026-09-09): the app_items INSERT in new_schema_repo.go omitted the
-- synced_at column and migration 008 gave it no DEFAULT, so every first insert of
-- the highest-volume table stored synced_at = NULL (only the ON CONFLICT re-sync
-- path stamped it). Fix on two axes:
--   1) Writers now stamp synced_at = NOW() on INSERT (repo change, same commit).
--   2) This migration backfills historical NULLs from created_at (the server's
--      insert-time stamp — the honest best-known arrival time), then hardens the
--      column against future writers.
-- Idempotent: every statement targets only rows where synced_at IS NULL.

-- ─────────────────────────────────────────────────────────────
-- 1) Backfill app_items — BATCHED (10k rows per UPDATE). A single UPDATE over
--    millions of rows would generate a large WAL burst, bloat and lock pressure;
--    batching bounds per-statement work. The whole file still runs in ONE
--    transaction (database.RunMigrations wraps each file in a tx), so for very
--    large deployments pre-run this backfill in small psql batches before the
--    deploy window — the UPDATE is idempotent and will find 0 rows here.
-- ─────────────────────────────────────────────────────────────
DO $$
DECLARE
    updated INTEGER;
    total   INTEGER := 0;
BEGIN
    LOOP
        UPDATE app_items
        SET synced_at = created_at
        WHERE ctid IN (
            SELECT ctid FROM app_items
            WHERE synced_at IS NULL
            LIMIT 10000
        );
        GET DIAGNOSTICS updated = ROW_COUNT;
        EXIT WHEN updated = 0;
        total := total + updated;
    END LOOP;
    RAISE NOTICE '[033] app_items backfilled: % rows', total;
END $$;

-- ─────────────────────────────────────────────────────────────
-- 2) Defensive backfill for the other nullable sync tables (historical rows
--    predating later ALTERs may hold NULLs; these tables' writers already stamp
--    synced_at on INSERT).
-- ─────────────────────────────────────────────────────────────
DO $$
DECLARE
    updated INTEGER;
BEGIN
    LOOP
        UPDATE device_hardware_info
        SET synced_at = created_at
        WHERE ctid IN (SELECT ctid FROM device_hardware_info WHERE synced_at IS NULL LIMIT 10000);
        GET DIAGNOSTICS updated = ROW_COUNT;
        EXIT WHEN updated = 0;
    END LOOP;
    LOOP
        UPDATE installed_applications
        SET synced_at = created_at
        WHERE ctid IN (SELECT ctid FROM installed_applications WHERE synced_at IS NULL LIMIT 10000);
        GET DIAGNOSTICS updated = ROW_COUNT;
        EXIT WHEN updated = 0;
    END LOOP;
    LOOP
        UPDATE installed_packages
        SET synced_at = created_at
        WHERE ctid IN (SELECT ctid FROM installed_packages WHERE synced_at IS NULL LIMIT 10000);
        GET DIAGNOSTICS updated = ROW_COUNT;
        EXIT WHEN updated = 0;
    END LOOP;
    LOOP
        UPDATE network_info
        SET synced_at = created_at
        WHERE ctid IN (SELECT ctid FROM network_info WHERE synced_at IS NULL LIMIT 10000);
        GET DIAGNOSTICS updated = ROW_COUNT;
        EXIT WHEN updated = 0;
    END LOOP;
    LOOP
        UPDATE session_events
        SET synced_at = created_at
        WHERE ctid IN (SELECT ctid FROM session_events WHERE synced_at IS NULL LIMIT 10000);
        GET DIAGNOSTICS updated = ROW_COUNT;
        EXIT WHEN updated = 0;
    END LOOP;
    LOOP
        UPDATE app_sessions
        SET synced_at = created_at
        WHERE ctid IN (SELECT ctid FROM app_sessions WHERE synced_at IS NULL LIMIT 10000);
        GET DIAGNOSTICS updated = ROW_COUNT;
        EXIT WHEN updated = 0;
    END LOOP;
    LOOP
        UPDATE hardware_devices
        SET synced_at = created_at
        WHERE ctid IN (SELECT ctid FROM hardware_devices WHERE synced_at IS NULL LIMIT 10000);
        GET DIAGNOSTICS updated = ROW_COUNT;
        EXIT WHEN updated = 0;
    END LOOP;
    LOOP
        UPDATE permission_status
        SET synced_at = created_at
        WHERE ctid IN (SELECT ctid FROM permission_status WHERE synced_at IS NULL LIMIT 10000);
        GET DIAGNOSTICS updated = ROW_COUNT;
        EXIT WHEN updated = 0;
    END LOOP;
    LOOP
        UPDATE storage_devices
        SET synced_at = created_at
        WHERE ctid IN (SELECT ctid FROM storage_devices WHERE synced_at IS NULL LIMIT 10000);
        GET DIAGNOSTICS updated = ROW_COUNT;
        EXIT WHEN updated = 0;
    END LOOP;
    LOOP
        UPDATE location_samples
        SET synced_at = created_at
        WHERE ctid IN (SELECT ctid FROM location_samples WHERE synced_at IS NULL LIMIT 10000);
        GET DIAGNOSTICS updated = ROW_COUNT;
        EXIT WHEN updated = 0;
    END LOOP;
END $$;

-- ─────────────────────────────────────────────────────────────
-- 3) Harden app_items.synced_at: DEFAULT protects every future INSERT path;
--    NOT NULL turns any unknown writer that omits the column into a loud
--    failure instead of a silent NULL (all known writers stamp explicitly).
-- ─────────────────────────────────────────────────────────────
ALTER TABLE app_items ALTER COLUMN synced_at SET DEFAULT now();
ALTER TABLE app_items ALTER COLUMN synced_at SET NOT NULL;
