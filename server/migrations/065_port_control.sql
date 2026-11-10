-- 065_port_control.sql
-- Hardware & Port Control.
--   machine_ports            : per-machine scanned inventory of every port/device
--                              (3 categories), one row per (employee, machine, instance_id).
--   device_control_rules     : admin draft — block/allow/alert a device class or instance
--                              (fleet-wide v1; per-employee scope is a later assignment table).
--   dlp_policy_device_control: immutable publish snapshot folded into the policy bundle.

CREATE TABLE IF NOT EXISTS machine_ports (
    id            BIGSERIAL PRIMARY KEY,
    employee_id   VARCHAR(20) NOT NULL REFERENCES employees(employee_id) ON DELETE CASCADE,
    machine_id    TEXT        NOT NULL DEFAULT '',
    instance_id   TEXT        NOT NULL,              -- stable PnP DeviceInstanceId
    category      SMALLINT    NOT NULL DEFAULT 1,    -- 1=external 2=internal 3=wireless/virtual
    pnp_class     TEXT        NOT NULL DEFAULT '',
    class_guid    TEXT        NOT NULL DEFAULT '',
    bus_type      TEXT        NOT NULL DEFAULT '',   -- NVMe/SATA/USB/PCIe/…
    friendly_name TEXT        NOT NULL DEFAULT '',
    manufacturer  TEXT        NOT NULL DEFAULT '',
    present       BOOLEAN     NOT NULL DEFAULT TRUE, -- occupied/attached now
    status        TEXT        NOT NULL DEFAULT '',   -- OK/Error/Disabled/…
    blockable     BOOLEAN     NOT NULL DEFAULT TRUE, -- false = video-out / system disk (alert only)
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT machine_ports_identity UNIQUE (employee_id, machine_id, instance_id)
);

CREATE INDEX IF NOT EXISTS idx_machine_ports_emp_machine
    ON machine_ports (employee_id, machine_id);
CREATE INDEX IF NOT EXISTS idx_machine_ports_present
    ON machine_ports (employee_id, machine_id, present);

-- Draft rules. target_kind: 'class' (pnp class or setup-class GUID) | 'instance'.
-- scope: who the rule applies to — 'global' (whole fleet), 'department', 'employee',
-- or 'machine'. scope_value holds the department id / employee_id / machine_id
-- ('' for global). Global rules are baked into the published bundle; scoped rules are
-- resolved per-device at policy pull time.
CREATE TABLE IF NOT EXISTS device_control_rules (
    id            BIGSERIAL PRIMARY KEY,
    target_kind   TEXT        NOT NULL CHECK (target_kind IN ('class','instance')),
    target_value  TEXT        NOT NULL,              -- class name/GUID, or instance id
    label         TEXT        NOT NULL DEFAULT '',   -- friendly name for the UI
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
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT device_control_rules_identity UNIQUE (target_kind, target_value, scope, scope_value)
);

CREATE INDEX IF NOT EXISTS idx_device_control_rules_enabled
    ON device_control_rules (enabled);
CREATE INDEX IF NOT EXISTS idx_device_control_rules_scope
    ON device_control_rules (scope, scope_value) WHERE enabled;

-- Immutable snapshot folded into the bundle at publish time.
CREATE TABLE IF NOT EXISTS dlp_policy_device_control (
    policy_version BIGINT NOT NULL REFERENCES dlp_policy_versions(version) ON DELETE CASCADE,
    seq            INT    NOT NULL,
    target_kind    TEXT   NOT NULL,
    target_value   TEXT   NOT NULL,
    pnp_class      TEXT   NOT NULL DEFAULT '',
    action         TEXT   NOT NULL DEFAULT 'block',
    PRIMARY KEY (policy_version, seq)
);
