-- 057_drop_terms.sql
-- Terms & Conditions is removed from the product. Consent no longer gates
-- live stream or any other page. Historical rows are dropped with the tables.
-- Migrations 036–039 stay in the tree as the history of how the tables were created.

DROP TABLE IF EXISTS terms_consent;
DROP TABLE IF EXISTS terms_content;
