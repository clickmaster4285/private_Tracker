-- Device acknowledgements for downloaded / installed / verified DLP policy states.
-- Clients POST after pull + after EnforceWorker observes firewall/AppLocker/WDAC health.

CREATE TABLE IF NOT EXISTS dlp_policy_device_ack (
    machine_id      TEXT NOT NULL,
    employee_id     TEXT NOT NULL DEFAULT '',
    policy_version  BIGINT NOT NULL,
    policy_hash     TEXT NOT NULL DEFAULT '',
    downloaded_at   TIMESTAMPTZ,
    installed_at    TIMESTAMPTZ,
    verified_at     TIMESTAMPTZ,
    verify_detail   TEXT NOT NULL DEFAULT '',
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (machine_id, policy_version)
);

CREATE INDEX IF NOT EXISTS idx_dlp_policy_device_ack_updated
    ON dlp_policy_device_ack (updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_dlp_policy_device_ack_employee
    ON dlp_policy_device_ack (employee_id, updated_at DESC);
