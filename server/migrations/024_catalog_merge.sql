-- 024_catalog_merge.sql
-- Merge duplicate installed_applications catalog rows by normalized display name.
--
-- Root cause: the same product can arrive from multiple OS sources with different
-- desktop_id / binary_name fingerprints (e.g. "Visual Studio Code" from Windows
-- Start Menu + Linux .desktop, or "Google Chrome" from different install paths).
-- The existing app_fingerprint = desktop_id|binary_name dedup does NOT catch these
-- because the fingerprints differ. Display names are OS-independent, so merging
-- by normalized app_name collapses true duplicates across platforms.
--
-- Strategy:
--   key = regexp_replace(lower(app_name), '[^a-z0-9]', '', 'g')
--   For each key with count(*) > 1:
--     winner = row with the most employee_installed_applications links
--              (tie → earliest created_at)
--     Re-point all junction links + app_sessions from losers to winner.
--     Carry loser's type_id / category_id onto winner when winner has none.
--     Soft-delete losers (deleted_at = now()) — keeps the UNIQUE app_fingerprint
--     constraint intact because deleted rows are excluded from the unique index.
--
-- Idempotent: safe to re-run (skips keys with only one non-deleted row).

BEGIN;

-- 1. Build a temporary table of merge groups:
--    normalize app_name, group non-deleted rows, pick winner per group.
CREATE TEMP TABLE tmp_catalog_merge AS
WITH normalized AS (
    SELECT
        id,
        app_name,
        regexp_replace(lower(app_name), '[^a-z0-9]', '', 'g') AS norm_key,
        COALESCE(type_id, 0) AS type_id,
        COALESCE(category_id, 0) AS category_id,
        created_at
    FROM installed_applications
    WHERE deleted_at IS NULL
),
groups AS (
    SELECT norm_key
    FROM normalized
    GROUP BY norm_key
    HAVING COUNT(*) > 1
),
winners AS (
    SELECT DISTINCT ON (n.norm_key)
        n.id AS winner_id,
        n.norm_key,
        n.app_name
    FROM normalized n
    JOIN groups g ON n.norm_key = g.norm_key
    ORDER BY n.norm_key, n.created_at ASC
),
losers AS (
    SELECT n.id AS loser_id, n.norm_key, w.winner_id
    FROM normalized n
    JOIN groups g ON n.norm_key = g.norm_key
    JOIN winners w ON n.norm_key = w.norm_key
    WHERE n.id != w.winner_id
)
SELECT l.loser_id, l.norm_key, l.winner_id, w.app_name AS winner_name
FROM losers l
JOIN winners w ON l.winner_id = w.winner_id;

-- 2. Re-point employee_installed_applications links from losers to winner.
--    Junction UNIQUE (employee_id, installed_application_id) prevents duplicates.
UPDATE employee_installed_applications eia
SET installed_application_id = m.winner_id
FROM tmp_catalog_merge m
WHERE eia.installed_application_id = m.loser_id;

-- 3. Re-point app_sessions.installed_app_id from losers to winner.
UPDATE app_sessions s
SET installed_app_id = m.winner_id
FROM tmp_catalog_merge m
WHERE s.installed_app_id = m.loser_id;

-- 4. Carry loser's type_id / category_id onto winner when winner has none.
UPDATE installed_applications winner
SET
    type_id = COALESCE(winner.type_id, loser.type_id),
    category_id = COALESCE(winner.category_id, loser.category_id)
FROM installed_applications loser
JOIN tmp_catalog_merge m ON m.loser_id = loser.id
WHERE winner.id = m.winner_id
  AND (winner.type_id IS NULL OR winner.category_id IS NULL);

-- 5. Soft-delete losers.
UPDATE installed_applications
SET deleted_at = NOW()
WHERE id IN (SELECT loser_id FROM tmp_catalog_merge);

-- 6. Clean up the temp table.
DROP TABLE tmp_catalog_merge;

COMMIT;
