-- 040_fix_app_items_url_index.sql
-- idx_app_items_url was a btree on unbounded TEXT. Long browser URLs (tracking
-- params, data URLs, SPA fragments) exceed PostgreSQL's btree key limit
-- (~2704 bytes for btree v4) and abort the whole app-items sync batch with
-- SQLSTATE 54000. List/search already uses LIKE on title/identifier/url/domain
-- (leading-wildcard), so a full-url btree never helped those queries. Domain
-- stays indexed; drop the unsafe url index.

DROP INDEX IF EXISTS idx_app_items_url;
