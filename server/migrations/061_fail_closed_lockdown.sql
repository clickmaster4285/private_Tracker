-- 061_fail_closed_lockdown.sql
-- Product default: published snapshots fail-closed (lockdown ON). Agents block
-- all web after login without a prior Publish; Publish only expands allow.hosts.
-- Flip historical open snapshots so a pull / reinstall does not re-open the fleet.
-- Admins who need an open PC publish lockdown=false from Web Allow Lists.

UPDATE dlp_policy_lockdown
SET lockdown = TRUE
WHERE lockdown = FALSE;

UPDATE dlp_lockdown_settings
SET lockdown = TRUE, updated_at = NOW()
WHERE id = 1 AND lockdown = FALSE;
