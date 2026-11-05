-- 048_add_device_activation_columns.sql
-- Add is_active_device and remove_at columns to employee_devices for device removal tracking.

-- is_active_device tracks whether the device is still actively reporting.
-- remove_at records when the device was marked inactive (for audit trail).
ALTER TABLE employee_devices
    ADD COLUMN IF NOT EXISTS is_active_device BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS remove_at TIMESTAMPTZ;

-- Backfill existing rows: set is_active_device = TRUE and remove_at = NULL for all existing devices.
UPDATE employee_devices SET is_active_device = TRUE WHERE is_active_device IS NULL;

-- Mark the bootstrap admin's device as inactive if it was the only tracked device,
-- preserving its last-known inventory under the Retired-Device History Rule (§6).
-- This is handled by the application logic on device removal, not the migration itself.