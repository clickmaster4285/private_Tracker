-- 070_antivirus_commands.sql
-- Admin-side remote antivirus actions. An admin queues a command for one machine on the web
-- /antivirus page (JWT); the Windows DlpService AntivirusGuardWorker pulls its machine's
-- pending commands (DeviceAuth), runs them through Microsoft Defender, and reports the result.
--
-- type: scan_quick | scan_full | scan_custom | update_signatures | remove_threats | restore_threat
--   scan_custom  -> arg = absolute path to scan
--   restore_threat -> arg = Defender ThreatID to restore from quarantine
-- status: pending -> running -> done | failed   (expired = never picked up in time)

CREATE TABLE IF NOT EXISTS antivirus_commands (
    id            BIGSERIAL PRIMARY KEY,
    employee_id   VARCHAR(20) NOT NULL REFERENCES employees(employee_id) ON DELETE CASCADE,
    machine_id    TEXT        NOT NULL DEFAULT '',
    type          TEXT        NOT NULL
                  CHECK (type IN ('scan_quick', 'scan_full', 'scan_custom', 'update_signatures',
                                  'remove_threats', 'restore_threat')),
    arg           TEXT        NOT NULL DEFAULT '',
    status        TEXT        NOT NULL DEFAULT 'pending'
                  CHECK (status IN ('pending', 'running', 'done', 'failed', 'expired')),
    result        TEXT        NOT NULL DEFAULT '',
    requested_by  UUID        REFERENCES users(id) ON DELETE SET NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    dispatched_at TIMESTAMPTZ,
    completed_at  TIMESTAMPTZ
);

-- Device pull: newest pending command per machine.
CREATE INDEX IF NOT EXISTS idx_antivirus_commands_pending
    ON antivirus_commands (employee_id, machine_id, status, created_at);
-- Admin list: recent commands per machine.
CREATE INDEX IF NOT EXISTS idx_antivirus_commands_machine
    ON antivirus_commands (employee_id, machine_id, created_at DESC);
