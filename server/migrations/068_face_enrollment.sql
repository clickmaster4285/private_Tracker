-- Face enrollment / biometric profiles (Phase 2–3).
-- Original images live on disk under FACE_MEDIA_DIR; embeddings are encrypted BYTEA.
-- Attendance punch tables are intentionally NOT in this migration.

CREATE TABLE IF NOT EXISTS face_model_versions (
    id                  TEXT PRIMARY KEY,
    model_id            TEXT NOT NULL,
    model_version       TEXT NOT NULL,
    role                TEXT NOT NULL CHECK (role IN ('recognition', 'detection', 'alignment')),
    embedding_dims      INT,
    preprocess_version  TEXT NOT NULL DEFAULT '',
    normalization       TEXT NOT NULL DEFAULT '',
    package_sha256      TEXT NOT NULL DEFAULT '',
    signature           TEXT NOT NULL DEFAULT '',
    package_rel_path    TEXT NOT NULL DEFAULT '',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS employee_face_profiles (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    employee_id         TEXT NOT NULL,
    status              TEXT NOT NULL DEFAULT 'pending'
                        CHECK (status IN ('pending', 'processing', 'active', 'failed', 'revoked')),
    profile_version     INT NOT NULL DEFAULT 1,
    enrollment_version  INT NOT NULL DEFAULT 1,
    model_id            TEXT NOT NULL DEFAULT '',
    model_version       TEXT NOT NULL DEFAULT '',
    preprocess_version  TEXT NOT NULL DEFAULT '',
    embedding_dims      INT NOT NULL DEFAULT 0,
    activated_at        TIMESTAMPTZ,
    revoked_at          TIMESTAMPTZ,
    last_error          TEXT NOT NULL DEFAULT '',
    created_by          TEXT NOT NULL DEFAULT '',
    updated_by          TEXT NOT NULL DEFAULT '',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_face_profiles_employee_active
    ON employee_face_profiles (employee_id)
    WHERE status = 'active';

CREATE INDEX IF NOT EXISTS idx_face_profiles_employee_version
    ON employee_face_profiles (employee_id, profile_version DESC);

CREATE INDEX IF NOT EXISTS idx_face_profiles_status
    ON employee_face_profiles (status);

CREATE TABLE IF NOT EXISTS face_enrollment_jobs (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    employee_id         TEXT NOT NULL,
    profile_id          UUID REFERENCES employee_face_profiles(id) ON DELETE SET NULL,
    status              TEXT NOT NULL DEFAULT 'pending'
                        CHECK (status IN ('pending', 'processing', 'succeeded', 'failed', 'needs_more_samples')),
    attempt_count       INT NOT NULL DEFAULT 0,
    locked_by           TEXT NOT NULL DEFAULT '',
    locked_at           TIMESTAMPTZ,
    error_code          TEXT NOT NULL DEFAULT '',
    error_message       TEXT NOT NULL DEFAULT '',
    created_by          TEXT NOT NULL DEFAULT '',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    finished_at         TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_face_jobs_status_created
    ON face_enrollment_jobs (status, created_at ASC);

CREATE INDEX IF NOT EXISTS idx_face_jobs_employee
    ON face_enrollment_jobs (employee_id, created_at DESC);

CREATE TABLE IF NOT EXISTS face_enrollment_samples (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    job_id              UUID NOT NULL REFERENCES face_enrollment_jobs(id) ON DELETE CASCADE,
    employee_id         TEXT NOT NULL,
    pose_id             TEXT NOT NULL,
    rel_path            TEXT NOT NULL,
    content_type        TEXT NOT NULL DEFAULT 'image/jpeg',
    byte_size           BIGINT NOT NULL DEFAULT 0,
    width               INT NOT NULL DEFAULT 0,
    height              INT NOT NULL DEFAULT 0,
    quality_score       REAL NOT NULL DEFAULT 0,
    client_sample_id    TEXT NOT NULL DEFAULT '',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT face_enrollment_samples_rel_path_unique UNIQUE (rel_path)
);

CREATE INDEX IF NOT EXISTS idx_face_samples_job
    ON face_enrollment_samples (job_id);

CREATE UNIQUE INDEX IF NOT EXISTS uq_face_samples_client_id
    ON face_enrollment_samples (job_id, client_sample_id)
    WHERE client_sample_id <> '';

CREATE TABLE IF NOT EXISTS employee_face_embeddings (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id              UUID NOT NULL REFERENCES employee_face_profiles(id) ON DELETE CASCADE,
    employee_id             TEXT NOT NULL,
    sample_id               UUID NOT NULL REFERENCES face_enrollment_samples(id) ON DELETE CASCADE,
    model_id                TEXT NOT NULL,
    model_version           TEXT NOT NULL,
    preprocess_version      TEXT NOT NULL DEFAULT '',
    embedding_dims          INT NOT NULL,
    embedding_ciphertext    BYTEA NOT NULL,
    embedding_nonce         BYTEA NOT NULL,
    quality_score           REAL NOT NULL DEFAULT 0,
    pose_id                 TEXT NOT NULL DEFAULT '',
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_face_embeddings_profile
    ON employee_face_embeddings (profile_id);

CREATE INDEX IF NOT EXISTS idx_face_embeddings_employee
    ON employee_face_embeddings (employee_id);

CREATE TABLE IF NOT EXISTS face_enrollment_audit (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    employee_id     TEXT NOT NULL,
    actor_user_id   TEXT NOT NULL DEFAULT '',
    action          TEXT NOT NULL,
    metadata_json   JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_face_audit_employee_created
    ON face_enrollment_audit (employee_id, created_at DESC);

-- Seed the planned recognition model pin (package bytes shipped later in Phase 4).
INSERT INTO face_model_versions (
    id, model_id, model_version, role, embedding_dims,
    preprocess_version, normalization, package_sha256, signature, package_rel_path
) VALUES (
    'adaface-ir50@pending',
    'adaface-ir50',
    'pending-license',
    'recognition',
    512,
    'face-preprocess-v1',
    'l2',
    '',
    '',
    ''
) ON CONFLICT (id) DO NOTHING;
