-- 011_app_items_url_domain.sql
-- Add url and domain columns to app_items for proper URL tracking from browser extension

ALTER TABLE app_items ADD COLUMN IF NOT EXISTS url TEXT NOT NULL DEFAULT '';
ALTER TABLE app_items ADD COLUMN IF NOT EXISTS domain TEXT NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS idx_app_items_url ON app_items(url);
CREATE INDEX IF NOT EXISTS idx_app_items_domain ON app_items(domain);
