package repository

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type FaceRepo struct {
	pool *pgxpool.Pool
}

func NewFaceRepo(pool *pgxpool.Pool) *FaceRepo {
	return &FaceRepo{pool: pool}
}

func (r *FaceRepo) CreateJob(ctx context.Context, employeeID, createdBy string) (*models.FaceEnrollmentJob, error) {
	row := r.pool.QueryRow(ctx, `
		INSERT INTO face_enrollment_jobs (employee_id, status, created_by)
		VALUES ($1, 'pending', $2)
		RETURNING id::text, employee_id, profile_id::text, status, attempt_count,
		          locked_by, locked_at, error_code, error_message, created_by, created_at, finished_at,
		          processed_samples, rejected_samples
	`, employeeID, createdBy)
	return scanJob(row)
}

func (r *FaceRepo) GetJob(ctx context.Context, jobID string) (*models.FaceEnrollmentJob, error) {
	row := r.pool.QueryRow(ctx, `
		SELECT j.id::text, j.employee_id, j.profile_id::text, j.status, j.attempt_count,
		       j.locked_by, j.locked_at, j.error_code, j.error_message, j.created_by, j.created_at, j.finished_at,
		       COALESCE((SELECT COUNT(*) FROM face_enrollment_samples s WHERE s.job_id = j.id), 0),
		       j.processed_samples, j.rejected_samples
		FROM face_enrollment_jobs j
		WHERE j.id = $1::uuid
	`, jobID)
	job, err := scanJobWithCount(row)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	return job, err
}

func (r *FaceRepo) InsertSample(ctx context.Context, s *models.FaceEnrollmentSample) error {
	_, err := r.pool.Exec(ctx, `
		INSERT INTO face_enrollment_samples (
			id, job_id, employee_id, pose_id, rel_path, content_type,
			byte_size, width, height, quality_score, client_sample_id
		) VALUES (
			$1::uuid, $2::uuid, $3, $4, $5, $6, $7, $8, $9, $10, $11
		)
	`, s.ID, s.JobID, s.EmployeeID, s.PoseID, s.RelPath, s.ContentType,
		s.ByteSize, s.Width, s.Height, s.QualityScore, s.ClientSampleID)
	return err
}

func (r *FaceRepo) ListSamplesByJob(ctx context.Context, jobID string) ([]models.FaceEnrollmentSample, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id::text, job_id::text, employee_id, pose_id, rel_path, content_type,
		       byte_size, width, height, quality_score, client_sample_id,
		       process_status, reject_reason, face_count, created_at
		FROM face_enrollment_samples
		WHERE job_id = $1::uuid
		ORDER BY created_at ASC
	`, jobID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []models.FaceEnrollmentSample
	for rows.Next() {
		var s models.FaceEnrollmentSample
		if err := rows.Scan(
			&s.ID, &s.JobID, &s.EmployeeID, &s.PoseID, &s.RelPath, &s.ContentType,
			&s.ByteSize, &s.Width, &s.Height, &s.QualityScore, &s.ClientSampleID,
			&s.ProcessStatus, &s.RejectReason, &s.FaceCount, &s.CreatedAt,
		); err != nil {
			return nil, err
		}
		out = append(out, s)
	}
	return out, rows.Err()
}

func (r *FaceRepo) GetSample(ctx context.Context, sampleID string) (*models.FaceEnrollmentSample, error) {
	row := r.pool.QueryRow(ctx, `
		SELECT id::text, job_id::text, employee_id, pose_id, rel_path, content_type,
		       byte_size, width, height, quality_score, client_sample_id,
		       process_status, reject_reason, face_count, created_at
		FROM face_enrollment_samples WHERE id = $1::uuid
	`, sampleID)
	var s models.FaceEnrollmentSample
	err := row.Scan(
		&s.ID, &s.JobID, &s.EmployeeID, &s.PoseID, &s.RelPath, &s.ContentType,
		&s.ByteSize, &s.Width, &s.Height, &s.QualityScore, &s.ClientSampleID,
		&s.ProcessStatus, &s.RejectReason, &s.FaceCount, &s.CreatedAt,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &s, nil
}

// UpdateSampleProcessResult stores one-by-one FaceWorker outcome for a sample.
func (r *FaceRepo) UpdateSampleProcessResult(ctx context.Context, sampleID, status, rejectReason string, faceCount int) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE face_enrollment_samples
		SET process_status = $2, reject_reason = $3, face_count = $4
		WHERE id = $1::uuid
	`, sampleID, status, rejectReason, faceCount)
	return err
}

func (r *FaceRepo) BumpJobProgress(ctx context.Context, jobID string) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE face_enrollment_jobs j
		SET processed_samples = (
		        SELECT COUNT(*) FROM face_enrollment_samples s
		        WHERE s.job_id = j.id AND s.process_status IN ('validated', 'embedded', 'rejected', 'processing')
		    ),
		    rejected_samples = (
		        SELECT COUNT(*) FROM face_enrollment_samples s
		        WHERE s.job_id = j.id AND s.process_status = 'rejected'
		    )
		WHERE j.id = $1::uuid
	`, jobID)
	return err
}

func (r *FaceRepo) CompleteJob(ctx context.Context, jobID, status, code, message string) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE face_enrollment_jobs
		SET status = $2, error_code = $3, error_message = $4, finished_at = now(),
		    locked_by = '', locked_at = NULL
		WHERE id = $1::uuid
	`, jobID, status, code, message)
	return err
}

func (r *FaceRepo) MarkJobNeedsMoreSamples(ctx context.Context, jobID, message string) error {
	return r.CompleteJob(ctx, jobID, "needs_more_samples", "needs_more_samples", message)
}

func (r *FaceRepo) GetActiveProfile(ctx context.Context, employeeID string) (*models.EmployeeFaceProfile, error) {
	return r.getProfileWhere(ctx, `employee_id = $1 AND status = 'active'`, employeeID)
}

func (r *FaceRepo) GetLatestProfile(ctx context.Context, employeeID string) (*models.EmployeeFaceProfile, error) {
	return r.getProfileWhere(ctx, `employee_id = $1 ORDER BY profile_version DESC LIMIT 1`, employeeID)
}

func (r *FaceRepo) getProfileWhere(ctx context.Context, where string, args ...any) (*models.EmployeeFaceProfile, error) {
	q := fmt.Sprintf(`
		SELECT p.id::text, p.employee_id, p.status, p.profile_version, p.enrollment_version,
		       p.model_id, p.model_version, p.preprocess_version, p.embedding_dims,
		       p.activated_at, p.revoked_at, p.last_error, p.created_by, p.updated_by,
		       p.created_at, p.updated_at,
		       COALESCE((SELECT COUNT(*) FROM employee_face_embeddings e WHERE e.profile_id = p.id), 0)
		FROM employee_face_profiles p
		WHERE %s
	`, where)
	row := r.pool.QueryRow(ctx, q, args...)
	var p models.EmployeeFaceProfile
	var profileIDNull *string
	_ = profileIDNull
	err := row.Scan(
		&p.ID, &p.EmployeeID, &p.Status, &p.ProfileVersion, &p.EnrollmentVersion,
		&p.ModelID, &p.ModelVersion, &p.PreprocessVersion, &p.EmbeddingDims,
		&p.ActivatedAt, &p.RevokedAt, &p.LastError, &p.CreatedBy, &p.UpdatedBy,
		&p.CreatedAt, &p.UpdatedAt, &p.EmbeddingCount,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &p, nil
}

func (r *FaceRepo) CreatePendingProfile(ctx context.Context, employeeID, createdBy string, nextVersion int) (*models.EmployeeFaceProfile, error) {
	row := r.pool.QueryRow(ctx, `
		INSERT INTO employee_face_profiles (
			employee_id, status, profile_version, enrollment_version, created_by, updated_by
		) VALUES ($1, 'pending', $2, $2, $3, $3)
		RETURNING id::text, employee_id, status, profile_version, enrollment_version,
		          model_id, model_version, preprocess_version, embedding_dims,
		          activated_at, revoked_at, last_error, created_by, updated_by, created_at, updated_at
	`, employeeID, nextVersion, createdBy)
	var p models.EmployeeFaceProfile
	err := row.Scan(
		&p.ID, &p.EmployeeID, &p.Status, &p.ProfileVersion, &p.EnrollmentVersion,
		&p.ModelID, &p.ModelVersion, &p.PreprocessVersion, &p.EmbeddingDims,
		&p.ActivatedAt, &p.RevokedAt, &p.LastError, &p.CreatedBy, &p.UpdatedBy,
		&p.CreatedAt, &p.UpdatedAt,
	)
	if err != nil {
		return nil, err
	}
	return &p, nil
}

func (r *FaceRepo) MaxProfileVersion(ctx context.Context, employeeID string) (int, error) {
	var n *int
	err := r.pool.QueryRow(ctx, `
		SELECT MAX(profile_version) FROM employee_face_profiles WHERE employee_id = $1
	`, employeeID).Scan(&n)
	if err != nil {
		return 0, err
	}
	if n == nil {
		return 0, nil
	}
	return *n, nil
}

func (r *FaceRepo) AttachJobProfile(ctx context.Context, jobID, profileID string) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE face_enrollment_jobs SET profile_id = $2::uuid WHERE id = $1::uuid
	`, jobID, profileID)
	return err
}

func (r *FaceRepo) RevokeActiveProfiles(ctx context.Context, employeeID, actor string) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE employee_face_profiles
		SET status = 'revoked', revoked_at = now(), updated_at = now(), updated_by = $2
		WHERE employee_id = $1 AND status = 'active'
	`, employeeID, actor)
	return err
}

func (r *FaceRepo) InsertAudit(ctx context.Context, employeeID, actor, action string, meta map[string]any) error {
	if meta == nil {
		meta = map[string]any{}
	}
	b, err := json.Marshal(meta)
	if err != nil {
		return err
	}
	_, err = r.pool.Exec(ctx, `
		INSERT INTO face_enrollment_audit (employee_id, actor_user_id, action, metadata_json)
		VALUES ($1, $2, $3, $4::jsonb)
	`, employeeID, actor, action, string(b))
	return err
}

// ClaimJobByID leases a specific pending (or stale processing) job for push-mode FaceWorker.
func (r *FaceRepo) ClaimJobByID(ctx context.Context, jobID, workerID string, leaseOlderThan time.Duration) (*models.FaceEnrollmentJob, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback(ctx)

	leaseSec := int(leaseOlderThan.Seconds())
	if leaseSec < 60 {
		leaseSec = 60
	}
	row := tx.QueryRow(ctx, `
		SELECT id::text FROM face_enrollment_jobs
		WHERE id = $1::uuid
		  AND (
		        status = 'pending'
		     OR (status = 'processing' AND locked_at IS NOT NULL
		         AND locked_at < now() - ($2::text || ' seconds')::interval)
		  )
		FOR UPDATE SKIP LOCKED
	`, jobID, strconv.Itoa(leaseSec))

	var id string
	if err := row.Scan(&id); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}

	_, err = tx.Exec(ctx, `
		UPDATE face_enrollment_jobs
		SET status = 'processing', locked_by = $2, locked_at = now(), attempt_count = attempt_count + 1
		WHERE id = $1::uuid
	`, id, workerID)
	if err != nil {
		return nil, err
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return r.GetJob(ctx, id)
}

// ClaimNextJob leases one pending job for the FaceWorker (SKIP LOCKED).
func (r *FaceRepo) ClaimNextJob(ctx context.Context, workerID string, leaseOlderThan time.Duration) (*models.FaceEnrollmentJob, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback(ctx)

	leaseSec := int(leaseOlderThan.Seconds())
	if leaseSec < 60 {
		leaseSec = 60
	}
	row := tx.QueryRow(ctx, `
		SELECT id::text FROM face_enrollment_jobs
		WHERE status = 'pending'
		   OR (status = 'processing' AND locked_at IS NOT NULL
		       AND locked_at < now() - ($1::text || ' seconds')::interval)
		ORDER BY created_at ASC
		FOR UPDATE SKIP LOCKED
		LIMIT 1
	`, strconv.Itoa(leaseSec))

	var jobID string
	if err := row.Scan(&jobID); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}

	_, err = tx.Exec(ctx, `
		UPDATE face_enrollment_jobs
		SET status = 'processing', locked_by = $2, locked_at = now(), attempt_count = attempt_count + 1
		WHERE id = $1::uuid
	`, jobID, workerID)
	if err != nil {
		return nil, err
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return r.GetJob(ctx, jobID)
}

func (r *FaceRepo) FailJob(ctx context.Context, jobID, code, message string) error {
	return r.CompleteJob(ctx, jobID, "failed", code, message)
}

// GetLatestJob returns the most recent enrollment job for an employee (any status).
func (r *FaceRepo) GetLatestJob(ctx context.Context, employeeID string) (*models.FaceEnrollmentJob, error) {
	row := r.pool.QueryRow(ctx, `
		SELECT j.id::text, j.employee_id, j.profile_id::text, j.status, j.attempt_count,
		       j.locked_by, j.locked_at, j.error_code, j.error_message, j.created_by, j.created_at, j.finished_at,
		       COALESCE((SELECT COUNT(*) FROM face_enrollment_samples s WHERE s.job_id = j.id), 0),
		       j.processed_samples, j.rejected_samples
		FROM face_enrollment_jobs j
		WHERE j.employee_id = $1
		ORDER BY j.created_at DESC
		LIMIT 1
	`, employeeID)
	job, err := scanJobWithCount(row)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	return job, err
}

// ListSamplesByEmployee returns every enrollment sample for an employee (newest first).
func (r *FaceRepo) ListSamplesByEmployee(ctx context.Context, employeeID string) ([]models.FaceEnrollmentSample, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id::text, job_id::text, employee_id, pose_id, rel_path, content_type,
		       byte_size, width, height, quality_score, client_sample_id,
		       process_status, reject_reason, face_count, created_at
		FROM face_enrollment_samples
		WHERE employee_id = $1
		ORDER BY created_at DESC
	`, employeeID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []models.FaceEnrollmentSample
	for rows.Next() {
		var s models.FaceEnrollmentSample
		if err := rows.Scan(
			&s.ID, &s.JobID, &s.EmployeeID, &s.PoseID, &s.RelPath, &s.ContentType,
			&s.ByteSize, &s.Width, &s.Height, &s.QualityScore, &s.ClientSampleID,
			&s.ProcessStatus, &s.RejectReason, &s.FaceCount, &s.CreatedAt,
		); err != nil {
			return nil, err
		}
		out = append(out, s)
	}
	return out, rows.Err()
}

// UpdateSampleAfterReplace overwrites metadata and resets process state after a retry upload.
func (r *FaceRepo) UpdateSampleAfterReplace(ctx context.Context, s *models.FaceEnrollmentSample) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE face_enrollment_samples
		SET content_type = $2, byte_size = $3, width = $4, height = $5,
		    quality_score = $6, client_sample_id = $7,
		    process_status = 'uploaded', reject_reason = '', face_count = 0
		WHERE id = $1::uuid
	`, s.ID, s.ContentType, s.ByteSize, s.Width, s.Height, s.QualityScore, s.ClientSampleID)
	return err
}

// ReopenJobPending clears a finished job so FaceWorker can re-claim after retries.
func (r *FaceRepo) ReopenJobPending(ctx context.Context, jobID string) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE face_enrollment_jobs
		SET status = 'pending', locked_by = '', locked_at = NULL, finished_at = NULL,
		    error_code = '', error_message = ''
		WHERE id = $1::uuid
	`, jobID)
	if err != nil {
		return err
	}
	return r.BumpJobProgress(ctx, jobID)
}

// DeleteEmbeddingsByProfile removes all templates for a profile (before re-embed).
func (r *FaceRepo) DeleteEmbeddingsByProfile(ctx context.Context, profileID string) error {
	_, err := r.pool.Exec(ctx, `DELETE FROM employee_face_embeddings WHERE profile_id = $1::uuid`, profileID)
	return err
}

// InsertEmbedding stores one encrypted template row.
func (r *FaceRepo) InsertEmbedding(ctx context.Context, profileID, employeeID, sampleID, modelID, modelVersion, preprocess string, dims int, ciphertext, nonce []byte, quality float64, poseID string) (string, error) {
	var id string
	err := r.pool.QueryRow(ctx, `
		INSERT INTO employee_face_embeddings (
			profile_id, employee_id, sample_id, model_id, model_version, preprocess_version,
			embedding_dims, embedding_ciphertext, embedding_nonce, quality_score, pose_id
		) VALUES (
			$1::uuid, $2, $3::uuid, $4, $5, $6, $7, $8, $9, $10, $11
		)
		RETURNING id::text
	`, profileID, employeeID, sampleID, modelID, modelVersion, preprocess,
		dims, ciphertext, nonce, quality, poseID).Scan(&id)
	return id, err
}

// ActivateProfile revokes prior active profiles for the employee and marks this one active.
func (r *FaceRepo) ActivateProfile(ctx context.Context, profileID, employeeID, modelID, modelVersion, preprocess string, dims int) error {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)

	_, err = tx.Exec(ctx, `
		UPDATE employee_face_profiles
		SET status = 'revoked', revoked_at = now(), updated_at = now()
		WHERE employee_id = $1 AND status = 'active' AND id <> $2::uuid
	`, employeeID, profileID)
	if err != nil {
		return err
	}
	tag, err := tx.Exec(ctx, `
		UPDATE employee_face_profiles
		SET status = 'active',
		    model_id = $3,
		    model_version = $4,
		    preprocess_version = $5,
		    embedding_dims = $6,
		    activated_at = now(),
		    revoked_at = NULL,
		    last_error = '',
		    updated_at = now()
		WHERE id = $1::uuid AND employee_id = $2
	`, profileID, employeeID, modelID, modelVersion, preprocess, dims)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("profile not found")
	}
	return tx.Commit(ctx)
}

// MarkProfileFailed sets pending/processing profile to failed with an error message.
func (r *FaceRepo) MarkProfileFailed(ctx context.Context, profileID, message string) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE employee_face_profiles
		SET status = 'failed', last_error = $2, updated_at = now()
		WHERE id = $1::uuid AND status IN ('pending', 'processing')
	`, profileID, message)
	return err
}

// ListEncryptedEmbeddingsByEmployee returns ciphertext rows for DeviceAuth decrypt+download.
func (r *FaceRepo) ListEncryptedEmbeddingsByEmployee(ctx context.Context, employeeID string) ([]EncryptedEmbeddingRow, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT e.id::text, e.profile_id::text, e.employee_id, e.sample_id::text,
		       e.model_id, e.model_version, e.preprocess_version, e.embedding_dims,
		       e.embedding_ciphertext, e.embedding_nonce, e.quality_score, e.pose_id, e.created_at
		FROM employee_face_embeddings e
		INNER JOIN employee_face_profiles p ON p.id = e.profile_id
		WHERE e.employee_id = $1 AND p.status = 'active'
		ORDER BY e.created_at ASC
	`, employeeID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []EncryptedEmbeddingRow
	for rows.Next() {
		var row EncryptedEmbeddingRow
		if err := rows.Scan(
			&row.ID, &row.ProfileID, &row.EmployeeID, &row.SampleID,
			&row.ModelID, &row.ModelVersion, &row.PreprocessVersion, &row.EmbeddingDims,
			&row.Ciphertext, &row.Nonce, &row.QualityScore, &row.PoseID, &row.CreatedAt,
		); err != nil {
			return nil, err
		}
		out = append(out, row)
	}
	return out, rows.Err()
}

// UpsertRecognitionModel updates/inserts the recognition model catalog row used at activate time.
func (r *FaceRepo) UpsertRecognitionModel(ctx context.Context, id, modelID, modelVersion, preprocess, sha256, relPath string, dims int) error {
	_, err := r.pool.Exec(ctx, `
		INSERT INTO face_model_versions (
			id, model_id, model_version, role, embedding_dims,
			preprocess_version, normalization, package_sha256, signature, package_rel_path
		) VALUES ($1, $2, $3, 'recognition', $4, $5, 'l2', $6, '', $7)
		ON CONFLICT (id) DO UPDATE SET
			model_version = EXCLUDED.model_version,
			embedding_dims = EXCLUDED.embedding_dims,
			preprocess_version = EXCLUDED.preprocess_version,
			package_sha256 = EXCLUDED.package_sha256,
			package_rel_path = EXCLUDED.package_rel_path
	`, id, modelID, modelVersion, dims, preprocess, sha256, relPath)
	return err
}

// EncryptedEmbeddingRow is the DB shape before AES-GCM decrypt.
type EncryptedEmbeddingRow struct {
	ID                string
	ProfileID         string
	EmployeeID        string
	SampleID          string
	ModelID           string
	ModelVersion      string
	PreprocessVersion string
	EmbeddingDims     int
	Ciphertext        []byte
	Nonce             []byte
	QualityScore      float64
	PoseID            string
	CreatedAt         time.Time
}

func (r *FaceRepo) ListModelManifest(ctx context.Context) ([]models.FaceModelVersion, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, model_id, model_version, role, embedding_dims, preprocess_version,
		       normalization, package_sha256, signature, package_rel_path, created_at
		FROM face_model_versions
		ORDER BY created_at ASC
	`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []models.FaceModelVersion
	for rows.Next() {
		var m models.FaceModelVersion
		if err := rows.Scan(
			&m.ID, &m.ModelID, &m.ModelVersion, &m.Role, &m.EmbeddingDims,
			&m.PreprocessVersion, &m.Normalization, &m.PackageSHA256, &m.Signature,
			&m.PackageRelPath, &m.CreatedAt,
		); err != nil {
			return nil, err
		}
		out = append(out, m)
	}
	return out, rows.Err()
}

// InsertVerifyEventIdempotent upserts by client_event_id; returns whether a new row was inserted.
func (r *FaceRepo) InsertVerifyEventIdempotent(ctx context.Context, e *models.FaceVerifyEvent) (bool, error) {
	tag, err := r.pool.Exec(ctx, `
		INSERT INTO face_verify_events (
			client_event_id, employee_id, machine_id, mode, trigger, pose_id,
			result, score, threshold, detail, client_created_at
		) VALUES (
			$1::uuid, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11
		)
		ON CONFLICT (client_event_id) DO NOTHING
	`, e.ClientEventID, e.EmployeeID, e.MachineID, e.Mode, e.Trigger, e.PoseID,
		e.Result, e.Score, e.Threshold, e.Detail, e.ClientCreatedAt)
	if err != nil {
		return false, err
	}
	return tag.RowsAffected() > 0, nil
}

type FaceVerifyListFilter struct {
	EmployeeID string
	Result     string
	Mode       string
	DateFrom   *time.Time
	DateTo     *time.Time
	Q          string
	Page       int
	PerPage    int
}

func (r *FaceRepo) ListVerifyEvents(ctx context.Context, f FaceVerifyListFilter) ([]models.FaceVerifyEvent, int, error) {
	if f.Page < 1 {
		f.Page = 1
	}
	if f.PerPage < 1 || f.PerPage > 100 {
		f.PerPage = 30
	}
	offset := (f.Page - 1) * f.PerPage

	where := []string{"1=1"}
	args := []any{}
	argN := 1
	add := func(cond string, v any) {
		where = append(where, cond)
		args = append(args, v)
		argN++
	}
	if f.EmployeeID != "" {
		add(fmt.Sprintf("e.employee_id = $%d", argN), f.EmployeeID)
	}
	if f.Result != "" {
		add(fmt.Sprintf("e.result = $%d", argN), f.Result)
	}
	if f.Mode != "" {
		add(fmt.Sprintf("e.mode = $%d", argN), f.Mode)
	}
	if f.DateFrom != nil {
		add(fmt.Sprintf("e.client_created_at >= $%d", argN), *f.DateFrom)
	}
	if f.DateTo != nil {
		add(fmt.Sprintf("e.client_created_at < $%d", argN), *f.DateTo)
	}
	if q := strings.TrimSpace(f.Q); q != "" {
		like := "%" + q + "%"
		where = append(where, fmt.Sprintf(
			"(e.employee_id ILIKE $%d OR COALESCE(emp.name, '') ILIKE $%d OR e.machine_id ILIKE $%d OR e.detail ILIKE $%d)",
			argN, argN, argN, argN))
		args = append(args, like)
		argN++
	}
	whereSQL := strings.Join(where, " AND ")

	countSQL := `
		SELECT COUNT(*)
		FROM face_verify_events e
		LEFT JOIN employees emp ON emp.employee_id = e.employee_id
		WHERE ` + whereSQL
	var total int
	if err := r.pool.QueryRow(ctx, countSQL, args...).Scan(&total); err != nil {
		return nil, 0, err
	}

	listArgs := append(append([]any{}, args...), f.PerPage, offset)
	listSQL := fmt.Sprintf(`
		SELECT e.id::text, e.client_event_id::text, e.employee_id, COALESCE(emp.name, ''),
		       e.machine_id, e.mode, e.trigger, e.pose_id, e.result,
		       e.score, e.threshold, e.detail, e.client_created_at, e.synced_at
		FROM face_verify_events e
		LEFT JOIN employees emp ON emp.employee_id = e.employee_id
		WHERE %s
		ORDER BY e.client_created_at DESC
		LIMIT $%d OFFSET $%d
	`, whereSQL, argN, argN+1)

	rows, err := r.pool.Query(ctx, listSQL, listArgs...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()
	var out []models.FaceVerifyEvent
	for rows.Next() {
		var ev models.FaceVerifyEvent
		if err := rows.Scan(
			&ev.ID, &ev.ClientEventID, &ev.EmployeeID, &ev.EmployeeName,
			&ev.MachineID, &ev.Mode, &ev.Trigger, &ev.PoseID, &ev.Result,
			&ev.Score, &ev.Threshold, &ev.Detail, &ev.ClientCreatedAt, &ev.SyncedAt,
		); err != nil {
			return nil, 0, err
		}
		out = append(out, ev)
	}
	return out, total, rows.Err()
}

func (r *FaceRepo) GetVerifyEvent(ctx context.Context, id string) (*models.FaceVerifyEvent, error) {
	row := r.pool.QueryRow(ctx, `
		SELECT e.id::text, e.client_event_id::text, e.employee_id, COALESCE(emp.name, ''),
		       e.machine_id, e.mode, e.trigger, e.pose_id, e.result,
		       e.score, e.threshold, e.detail, e.client_created_at, e.synced_at
		FROM face_verify_events e
		LEFT JOIN employees emp ON emp.employee_id = e.employee_id
		WHERE e.id = $1::uuid
	`, id)
	var ev models.FaceVerifyEvent
	err := row.Scan(
		&ev.ID, &ev.ClientEventID, &ev.EmployeeID, &ev.EmployeeName,
		&ev.MachineID, &ev.Mode, &ev.Trigger, &ev.PoseID, &ev.Result,
		&ev.Score, &ev.Threshold, &ev.Detail, &ev.ClientCreatedAt, &ev.SyncedAt,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &ev, nil
}

type jobScanner interface {
	Scan(dest ...any) error
}

func scanJob(row jobScanner) (*models.FaceEnrollmentJob, error) {
	var j models.FaceEnrollmentJob
	var profileID *string
	err := row.Scan(
		&j.ID, &j.EmployeeID, &profileID, &j.Status, &j.AttemptCount,
		&j.LockedBy, &j.LockedAt, &j.ErrorCode, &j.ErrorMessage, &j.CreatedBy, &j.CreatedAt, &j.FinishedAt,
		&j.ProcessedSamples, &j.RejectedSamples,
	)
	if err != nil {
		return nil, err
	}
	j.ProfileID = profileID
	return &j, nil
}

func scanJobWithCount(row jobScanner) (*models.FaceEnrollmentJob, error) {
	var j models.FaceEnrollmentJob
	var profileID *string
	err := row.Scan(
		&j.ID, &j.EmployeeID, &profileID, &j.Status, &j.AttemptCount,
		&j.LockedBy, &j.LockedAt, &j.ErrorCode, &j.ErrorMessage, &j.CreatedBy, &j.CreatedAt, &j.FinishedAt,
		&j.SampleCount, &j.ProcessedSamples, &j.RejectedSamples,
	)
	if err != nil {
		return nil, err
	}
	j.ProfileID = profileID
	return &j, nil
}
