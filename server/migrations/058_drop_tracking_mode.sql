-- 058_drop_tracking_mode.sql
-- Tracking mode (Fixed/Roaming Start/Stop) is removed from the product.
-- Collectors always run while the employee is logged in. Migration 056 stays
-- in the tree as the history of how these objects were created.

DROP TABLE IF EXISTS device_tracking_events;
DROP TABLE IF EXISTS device_policy_audit;
DROP TABLE IF EXISTS device_tracking_policies;
DROP TABLE IF EXISTS device_control_meta;

ALTER TABLE employee_machines
    DROP COLUMN IF EXISTS tracking_mode_override,
    DROP COLUMN IF EXISTS tracking_desired_state,
    DROP COLUMN IF EXISTS tracking_phase,
    DROP COLUMN IF EXISTS tracking_state_since,
    DROP COLUMN IF EXISTS last_control_seen_at,
    DROP COLUMN IF EXISTS control_version;
