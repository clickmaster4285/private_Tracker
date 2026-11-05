-- 049_employee_machines.sql
-- Canonical per-machine lifecycle registry (one row per physical machine an employee used).
--
-- Before this migration a machine's lifecycle was implicit in employee_devices, but that
-- table is the AUTH-TOKEN registry: UpsertDevice revokes the previous (employee, machine)
-- row and inserts a NEW one on every login, so a single physical PC can appear as many
-- employee_devices rows over time. The web Device Specs dropdown could not tell one machine
-- from another, and there was nowhere to store an explicit active|old status.
--
-- employee_machines is that missing record: exactly one row per (employee_id, machine_id)
-- with an explicit status and first/last track timestamps. employee_devices keeps owning
-- the token/revocation state; the two are joined on (employee_id, machine_id).
--
-- wipe_requested_at is the "Remove current device" trigger: an admin removal stamps it, and
-- the machine's next sync (authenticated by its now-revoked token) persists its outstanding
-- data and is answered with action=wipe_local so the client clears its local DB (see
-- DeviceAuth + the sync handlers). It is inherently one-shot: once the client wipes, it no
-- longer holds a token to sync with and a fresh login creates a brand-new machine.

CREATE TABLE IF NOT EXISTS employee_machines (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    employee_id       VARCHAR(20) NOT NULL REFERENCES employees(employee_id) ON DELETE CASCADE,
    machine_id        VARCHAR(255) NOT NULL,
    status            VARCHAR(10) NOT NULL DEFAULT 'active'
                        CHECK (status IN ('active', 'old')),
    first_track_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_track_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    wipe_requested_at TIMESTAMPTZ,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at        TIMESTAMPTZ
);

-- One live row per (employee, machine). Partial so a soft-deleted row never blocks re-creation.
CREATE UNIQUE INDEX IF NOT EXISTS ux_employee_machines_emp_machine
    ON employee_machines(employee_id, machine_id) WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_employee_machines_employee
    ON employee_machines(employee_id);

-- Backfill from the device registry: one row per machine, folding the revoked/re-issued
-- registrations together. A machine is 'active' when it has at least one unrevoked,
-- still-active registration; otherwise it is 'old'. first_track_at = earliest registration,
-- last_track_at = most recent sighting. ON CONFLICT DO NOTHING keeps the migration safe to
-- re-run and never clobbers a row the application already created.
INSERT INTO employee_machines (employee_id, machine_id, status, first_track_at, last_track_at)
SELECT d.employee_id,
       d.machine_id,
       CASE
           WHEN BOOL_OR(d.revoked_at IS NULL AND COALESCE(d.is_active_device, TRUE)) THEN 'active'
           ELSE 'old'
       END,
       MIN(d.created_at),
       MAX(d.last_seen_at)
FROM employee_devices d
WHERE d.machine_id <> ''
GROUP BY d.employee_id, d.machine_id
ON CONFLICT DO NOTHING;
