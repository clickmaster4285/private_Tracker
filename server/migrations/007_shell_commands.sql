-- 007_shell_commands.sql
-- Shell command collection/sync was REMOVED from the product (2026-07-30).
-- This migration now only drops the legacy table so existing databases are cleaned.
DROP TABLE IF EXISTS shell_commands CASCADE;
