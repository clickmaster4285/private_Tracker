-- 021_employee_devices.sql
-- Employee devices tracking table for long-lived, revocable device authentication.

CREATE TABLE IF NOT EXISTS employee_devices (
  id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  employee_id    VARCHAR(20) NOT NULL REFERENCES employees(employee_id) ON DELETE CASCADE,
  machine_id     VARCHAR(255) NOT NULL,
  platform       VARCHAR(50) NOT NULL,
  client_version VARCHAR(50) NOT NULL,
  device_name    VARCHAR(255) NOT NULL DEFAULT '',
  token_hash     VARCHAR(64) NOT NULL UNIQUE,
  created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_seen_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  expires_at     TIMESTAMPTZ,
  revoked_at     TIMESTAMPTZ
);

-- Index for fast token authentication (only unrevoked devices)
CREATE INDEX IF NOT EXISTS idx_employee_devices_token_hash ON employee_devices(token_hash) WHERE revoked_at IS NULL;

-- Unique index per employee & machine for active registrations
CREATE UNIQUE INDEX IF NOT EXISTS idx_employee_devices_active ON employee_devices(employee_id, machine_id) WHERE revoked_at IS NULL;

-- Index for listing employee devices
CREATE INDEX IF NOT EXISTS idx_employee_devices_employee_id ON employee_devices(employee_id);
