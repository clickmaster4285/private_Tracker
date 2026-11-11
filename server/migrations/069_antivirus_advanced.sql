-- 069_antivirus_advanced.sql
-- Advanced antivirus hardening state reported by the Windows DlpService AntivirusGuardWorker:
--   Attack Surface Reduction rule coverage, Controlled Folder Access, network-protection mode,
--   SmartScreen, memory integrity (HVCI), Credential Guard, email scanning, exploit protection,
--   and the live quarantined-threat count. All additive columns on the existing fleet row.

ALTER TABLE antivirus_status
    ADD COLUMN IF NOT EXISTS asr_configured             INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS asr_block                  INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS controlled_folder_access   TEXT    NOT NULL DEFAULT 'off',
    ADD COLUMN IF NOT EXISTS network_protection_mode    TEXT    NOT NULL DEFAULT 'off',
    ADD COLUMN IF NOT EXISTS smart_screen_enabled       BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS memory_integrity_enabled   BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS credential_guard_enabled   BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS email_scan_enabled         BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS exploit_protection_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS quarantined_threat_count   INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS hardening_enabled          BOOLEAN NOT NULL DEFAULT TRUE;
