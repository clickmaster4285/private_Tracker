-- 056_device_tracking_control.sql
-- Fixed/Roaming tracking control plane (newplan.md P0).
-- Seeds org default mode = fixed so every existing machine stays unstoppable until
-- an admin opts a laptop into roaming (P2+). keep_dlp_when_stopped defaults TRUE
-- (tracking Stop must not release USB/web enforcement).

CREATE TABLE IF NOT EXISTS device_control_meta (
    id           SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    global_epoch BIGINT NOT NULL DEFAULT 0,
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
INSERT INTO device_control_meta (id, global_epoch)
VALUES (1, 1)
ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS device_tracking_policies (
    id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    scope_type               VARCHAR(12) NOT NULL
                               CHECK (scope_type IN ('org', 'department', 'employee')),
    scope_id                 VARCHAR(64) NOT NULL DEFAULT '',
    tracking_mode            VARCHAR(8)  NOT NULL
                               CHECK (tracking_mode IN ('fixed', 'roaming')),
    stop_window              VARCHAR(16) NOT NULL DEFAULT 'anytime'
                               CHECK (stop_window IN ('anytime', 'outside_shift')),
    require_stop_reason      BOOLEAN NOT NULL DEFAULT FALSE,
    keep_dlp_when_stopped    BOOLEAN NOT NULL DEFAULT TRUE,
    max_stopped_hours        INT NULL
                               CHECK (max_stopped_hours IS NULL OR max_stopped_hours > 0),
    updated_by               UUID NULL,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at               TIMESTAMPTZ,
    UNIQUE (scope_type, scope_id)
);

-- Fail-safe org default: fixed (not stoppable).
INSERT INTO device_tracking_policies (scope_type, scope_id, tracking_mode)
VALUES ('org', '', 'fixed')
ON CONFLICT (scope_type, scope_id) DO NOTHING;

ALTER TABLE employee_machines
    ADD COLUMN IF NOT EXISTS tracking_mode_override VARCHAR(8)
        CHECK (tracking_mode_override IS NULL OR tracking_mode_override IN ('fixed', 'roaming')),
    ADD COLUMN IF NOT EXISTS tracking_desired_state VARCHAR(10) NOT NULL DEFAULT 'running'
        CHECK (tracking_desired_state IN ('running', 'stopped')),
    ADD COLUMN IF NOT EXISTS tracking_phase VARCHAR(12) NOT NULL DEFAULT 'collecting'
        CHECK (tracking_phase IN ('starting', 'collecting', 'draining', 'idle', 'error')),
    ADD COLUMN IF NOT EXISTS tracking_state_since TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ADD COLUMN IF NOT EXISTS last_control_seen_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS control_version BIGINT NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS device_tracking_events (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    employee_id       VARCHAR(20) NOT NULL REFERENCES employees(employee_id) ON DELETE CASCADE,
    machine_id        VARCHAR(255) NOT NULL,
    event_type        VARCHAR(8)  NOT NULL CHECK (event_type IN ('start', 'stop')),
    occurred_at       TIMESTAMPTZ NOT NULL,
    received_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    client_event_id   UUID NOT NULL UNIQUE,
    reason            VARCHAR(24),
    work_location     VARCHAR(8) CHECK (work_location IS NULL OR work_location IN ('on_site', 'remote', 'unknown')),
    location_verified BOOLEAN NOT NULL DEFAULT FALSE,
    flush_result      VARCHAR(10) CHECK (flush_result IS NULL OR flush_result IN ('complete', 'partial', 'pending')),
    unsynced_remaining INT,
    client_version    VARCHAR(32)
);

CREATE INDEX IF NOT EXISTS idx_dte_machine_time
    ON device_tracking_events (machine_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_dte_employee_time
    ON device_tracking_events (employee_id, occurred_at DESC);

CREATE TABLE IF NOT EXISTS device_policy_audit (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    actor_user_id UUID,
    action       VARCHAR(32) NOT NULL,
    scope_type   VARCHAR(12),
    scope_id     VARCHAR(64),
    machine_id   VARCHAR(255),
    employee_id  VARCHAR(20),
    old_value    JSONB,
    new_value    JSONB,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_device_policy_audit_created
    ON device_policy_audit (created_at DESC);
