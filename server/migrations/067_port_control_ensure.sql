-- 067_port_control_ensure.sql
-- Idempotent re-create of the Hardware & Port Control tables.
-- 065 was recorded as applied on some DB states where machine_ports ended up absent
-- (so it is never re-run). This migration guarantees the tables + columns exist, and
-- is safe to run whether or not 065 already created them (IF NOT EXISTS everywhere).

CREATE TABLE IF NOT EXISTS machine_ports (
    id            BIGSERIAL PRIMARY KEY,
    employee_id   VARCHAR(20) NOT NULL REFERENCES employees(employee_id) ON DELETE CASCADE,
    machine_id    TEXT        NOT NULL DEFAULT '',
    instance_id   TEXT        NOT NULL,
    category      SMALLINT    NOT NULL DEFAULT 1,
    pnp_class     TEXT        NOT NULL DEFAULT '',
    class_guid    TEXT        NOT NULL DEFAULT '',
    bus_type      TEXT        NOT NULL DEFAULT '',
    friendly_name TEXT        NOT NULL DEFAULT '',
    manufacturer  TEXT        NOT NULL DEFAULT '',
    present       BOOLEAN     NOT NULL DEFAULT TRUE,
    status        TEXT        NOT NULL DEFAULT '',
    blockable     BOOLEAN     NOT NULL DEFAULT TRUE,
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT machine_ports_identity UNIQUE (employee_id, machine_id, instance_id)
);

CREATE INDEX IF NOT EXISTS idx_machine_ports_emp_machine
    ON machine_ports (employee_id, machine_id);
CREATE INDEX IF NOT EXISTS idx_machine_ports_present
    ON machine_ports (employee_id, machine_id, present);

CREATE TABLE IF NOT EXISTS device_control_rules (
    id            BIGSERIAL PRIMARY KEY,
    target_kind   TEXT        NOT NULL CHECK (target_kind IN ('class','instance')),
    target_value  TEXT        NOT NULL,
    label         TEXT        NOT NULL DEFAULT '',
    pnp_class     TEXT        NOT NULL DEFAULT '',
    action        TEXT        NOT NULL DEFAULT 'block' CHECK (action IN ('block','allow','alert')),
    scope         TEXT        NOT NULL DEFAULT 'global'
                  CHECK (scope IN ('global','department','employee','machine')),
    scope_value   TEXT        NOT NULL DEFAULT '',
    enabled       BOOLEAN     NOT NULL DEFAULT TRUE,
    note          TEXT        NOT NULL DEFAULT '',
    created_by    UUID,
    updated_by    UUID,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- In case device_control_rules pre-existed WITHOUT the scope columns (early 065).
ALTER TABLE device_control_rules ADD COLUMN IF NOT EXISTS scope       TEXT NOT NULL DEFAULT 'global';
ALTER TABLE device_control_rules ADD COLUMN IF NOT EXISTS scope_value TEXT NOT NULL DEFAULT '';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'device_control_rules_identity'
    ) THEN
        ALTER TABLE device_control_rules
            ADD CONSTRAINT device_control_rules_identity
            UNIQUE (target_kind, target_value, scope, scope_value);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_device_control_rules_enabled
    ON device_control_rules (enabled);
CREATE INDEX IF NOT EXISTS idx_device_control_rules_scope
    ON device_control_rules (scope, scope_value) WHERE enabled;

CREATE TABLE IF NOT EXISTS dlp_policy_device_control (
    policy_version BIGINT NOT NULL REFERENCES dlp_policy_versions(version) ON DELETE CASCADE,
    seq            INT    NOT NULL,
    target_kind    TEXT   NOT NULL,
    target_value   TEXT   NOT NULL,
    pnp_class      TEXT   NOT NULL DEFAULT '',
    action         TEXT   NOT NULL DEFAULT 'block',
    PRIMARY KEY (policy_version, seq)
);
