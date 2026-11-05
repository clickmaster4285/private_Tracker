-- Migration 036: Per-feature Terms & Conditions consent audit table
-- Append-only audit trail for employee consent events (accept/revoke).
-- The client is the authority for feature activation; this table is for
-- compliance evidence only.

CREATE TABLE IF NOT EXISTS terms_consent (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    employee_id     VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    feature_id      TEXT NOT NULL,
    terms_version   TEXT NOT NULL,
    action          TEXT NOT NULL CHECK (action IN ('accepted', 'revoked', 're_accepted')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_terms_consent_employee ON terms_consent(employee_id);
CREATE INDEX IF NOT EXISTS idx_terms_consent_feature ON terms_consent(feature_id);
CREATE INDEX IF NOT EXISTS idx_terms_consent_created ON terms_consent(created_at DESC);

-- Soft-delete support for retention policies (not used in v1 but schema-ready)
ALTER TABLE terms_consent ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
