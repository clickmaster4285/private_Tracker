-- 039_terms_content_unique_feature_fix.sql
-- The live DB carried a manually-added UNIQUE index on feature_id
-- (idx_terms_content_feature) intended to enforce "one term per feature" for
-- the FEATURED (system) terms. But manual/admin-created terms are inserted
-- with feature_id = '' (terms_content_repo.Create hardcodes it), so the FIRST
-- manual term consumed the empty string and every subsequent create failed
-- with SQLSTATE 23505 (duplicate key) -> HTTP 500 on POST /terms-content.
--
-- Fix: re-scope the unique index to NON-EMPTY feature_ids only. Featured
-- terms keep their one-term-per-feature guarantee; manual terms (empty
-- feature_id) are exempt and can be created without limit.

DROP INDEX IF EXISTS idx_terms_content_feature;

CREATE UNIQUE INDEX IF NOT EXISTS idx_terms_content_feature
    ON terms_content (feature_id)
    WHERE deleted_at IS NULL AND feature_id <> '';
