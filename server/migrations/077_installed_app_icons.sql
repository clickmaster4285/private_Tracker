-- App icons for the company-global application catalog (Allowlists → Apps picker,
-- Configuration → Applications). The Windows client extracts a small PNG (≤ 64 KB,
-- normally 32×32) from each Start-menu app — desktop EXE or packaged (Store) app —
-- and sends it base64 in installed-apps sync. Last writer wins; NULL = no icon yet.

ALTER TABLE installed_applications ADD COLUMN IF NOT EXISTS icon_png BYTEA;
ALTER TABLE installed_applications ADD COLUMN IF NOT EXISTS icon_updated_at TIMESTAMPTZ;
