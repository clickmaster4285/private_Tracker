-- 071_antivirus_threats.sql
-- Per-threat inventory + per-threat remediation for the 24/7 antivirus guard.
--   antivirus_threats : one row per (employee, machine, Defender ThreatID) — the threat list the
--                       Windows DlpService AntivirusGuardWorker reads from Get-MpThreat +
--                       Get-MpThreatDetection and syncs as a full per-machine SNAPSHOT
--                       (POST /api/v1/antivirus/threats/sync, DeviceAuth). A row that disappears
--                       from the snapshot (Defender history purged) is deleted for that machine.
--   antivirus_commands: two new command types —
--                       remove_threat -> arg = ThreatID: scan + kill + delete only the files
--                                        Defender attributed to that ThreatID
--                       scan_offline  -> Microsoft Defender Offline scan (PC reboots)

CREATE TABLE IF NOT EXISTS antivirus_threats (
    id                     BIGSERIAL PRIMARY KEY,
    employee_id            VARCHAR(20) NOT NULL REFERENCES employees(employee_id) ON DELETE CASCADE,
    machine_id             TEXT        NOT NULL DEFAULT '',
    threat_id              BIGINT      NOT NULL,
    threat_name            TEXT        NOT NULL DEFAULT '',
    severity               TEXT        NOT NULL DEFAULT 'unknown'
                           CHECK (severity IN ('low', 'medium', 'high', 'critical', 'unknown')),
    category               TEXT        NOT NULL DEFAULT '',
    status                 TEXT        NOT NULL DEFAULT 'unknown'
                           CHECK (status IN ('detected', 'cleaned', 'quarantined', 'removed', 'allowed',
                                             'blocked', 'failed', 'unknown')),
    is_active              BOOLEAN     NOT NULL DEFAULT FALSE,
    did_execute            BOOLEAN     NOT NULL DEFAULT FALSE,
    resources              TEXT        NOT NULL DEFAULT '',   -- newline-separated Defender resources
    process_name           TEXT        NOT NULL DEFAULT '',
    detection_user         TEXT        NOT NULL DEFAULT '',
    detection_source       TEXT        NOT NULL DEFAULT '',
    initial_detection_at   TIMESTAMPTZ,
    last_status_change_at  TIMESTAMPTZ,
    first_synced_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (employee_id, machine_id, threat_id)
);

-- Fleet threat list: newest first, optional active / status filter.
CREATE INDEX IF NOT EXISTS idx_antivirus_threats_recent
    ON antivirus_threats (last_status_change_at DESC NULLS LAST, id DESC);
CREATE INDEX IF NOT EXISTS idx_antivirus_threats_active
    ON antivirus_threats (is_active, status);

ALTER TABLE antivirus_commands DROP CONSTRAINT IF EXISTS antivirus_commands_type_check;
ALTER TABLE antivirus_commands ADD CONSTRAINT antivirus_commands_type_check
    CHECK (type IN ('scan_quick', 'scan_full', 'scan_custom', 'scan_offline', 'update_signatures',
                    'remove_threats', 'remove_threat', 'restore_threat'));

-- Fleet-wide command history (web /antivirus?tab=commands).
CREATE INDEX IF NOT EXISTS idx_antivirus_commands_recent
    ON antivirus_commands (created_at DESC, id DESC);
