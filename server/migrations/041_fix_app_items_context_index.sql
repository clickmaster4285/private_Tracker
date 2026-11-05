-- 041_fix_app_items_context_index.sql
-- Same failure mode as 040: identifier often holds a full URL/path and can exceed
-- Postgres btree key limit (~2704 bytes) when indexed with employee_id + item_type.
-- List/search does not need exact identifier equality in the btree; keep a compact
-- index on (employee_id, item_type) for type-scoped employee reads.

DROP INDEX IF EXISTS idx_app_items_context;

CREATE INDEX IF NOT EXISTS idx_app_items_emp_type
    ON app_items(employee_id, item_type);
