-- Per-sample processing status for one-by-one FaceWorker results.

ALTER TABLE face_enrollment_samples
    ADD COLUMN IF NOT EXISTS process_status TEXT NOT NULL DEFAULT 'uploaded'
        CHECK (process_status IN (
            'uploaded', 'processing', 'validated', 'embedded', 'rejected'
        ));

ALTER TABLE face_enrollment_samples
    ADD COLUMN IF NOT EXISTS reject_reason TEXT NOT NULL DEFAULT '';

ALTER TABLE face_enrollment_samples
    ADD COLUMN IF NOT EXISTS face_count INT NOT NULL DEFAULT 0;

ALTER TABLE face_enrollment_jobs
    ADD COLUMN IF NOT EXISTS processed_samples INT NOT NULL DEFAULT 0;

ALTER TABLE face_enrollment_jobs
    ADD COLUMN IF NOT EXISTS rejected_samples INT NOT NULL DEFAULT 0;
