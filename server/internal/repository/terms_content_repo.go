package repository

import (
	"context"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

type TermsContent struct {
	ID           string
	Heading      string
	Body         string
	TermsVersion string
	IsSystem     bool
	FeatureID    string
	TermType     string
	IsActive     int
	SortOrder    int
	UpdatedAt    time.Time
	CreatedAt    time.Time
}

type TermsContentRepo struct {
	pool *pgxpool.Pool
}

func NewTermsContentRepo(pool *pgxpool.Pool) *TermsContentRepo {
	return &TermsContentRepo{pool: pool}
}

func (r *TermsContentRepo) ListAll(ctx context.Context) ([]TermsContent, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order, updated_at, created_at
		FROM terms_content
		WHERE deleted_at IS NULL
		ORDER BY sort_order ASC, created_at ASC
	`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var results []TermsContent
	for rows.Next() {
		var tc TermsContent
		if err := rows.Scan(&tc.ID, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.IsSystem, &tc.FeatureID, &tc.TermType, &tc.IsActive, &tc.SortOrder, &tc.UpdatedAt, &tc.CreatedAt); err != nil {
			return nil, err
		}
		results = append(results, tc)
	}
	return results, nil
}

// ListActive returns only ACTIVE, non-deleted terms — the shape the desktop
// client consumes for its acceptance gate. The client-side is_active == 1 filter
// from the original plan is enforced here instead so the client-facing endpoint
// can never leak admin-drafted (inactive) content to employee machines.
func (r *TermsContentRepo) ListActive(ctx context.Context) ([]TermsContent, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order, updated_at, created_at
		FROM terms_content
		WHERE deleted_at IS NULL AND is_active = 1
		ORDER BY sort_order ASC, created_at ASC
	`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var results []TermsContent
	for rows.Next() {
		var tc TermsContent
		if err := rows.Scan(&tc.ID, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.IsSystem, &tc.FeatureID, &tc.TermType, &tc.IsActive, &tc.SortOrder, &tc.UpdatedAt, &tc.CreatedAt); err != nil {
			return nil, err
		}
		results = append(results, tc)
	}
	return results, nil
}

func (r *TermsContentRepo) GetByID(ctx context.Context, id string) (*TermsContent, error) {
	var tc TermsContent
	err := r.pool.QueryRow(ctx, `
		SELECT id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order, updated_at, created_at
		FROM terms_content
		WHERE id = $1 AND deleted_at IS NULL
	`, id).Scan(&tc.ID, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.IsSystem, &tc.FeatureID, &tc.TermType, &tc.IsActive, &tc.SortOrder, &tc.UpdatedAt, &tc.CreatedAt)
	if err != nil {
		return nil, err
	}
	return &tc, nil
}

func (r *TermsContentRepo) ExistsByFeatureID(ctx context.Context, featureID string) (bool, error) {
	var exists bool
	err := r.pool.QueryRow(ctx, `
		SELECT EXISTS(SELECT 1 FROM terms_content WHERE feature_id = $1 AND deleted_at IS NULL)
	`, featureID).Scan(&exists)
	return exists, err
}

func (r *TermsContentRepo) Update(ctx context.Context, id, heading, body, termsVersion string) (*TermsContent, error) {
	var tc TermsContent
	err := r.pool.QueryRow(ctx, `
		UPDATE terms_content
		SET heading = $2, body = $3, terms_version = $4, updated_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order, updated_at, created_at
	`, id, heading, body, termsVersion).Scan(&tc.ID, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.IsSystem, &tc.FeatureID, &tc.TermType, &tc.IsActive, &tc.SortOrder, &tc.UpdatedAt, &tc.CreatedAt)
	if err != nil {
		return nil, err
	}
	return &tc, nil
}

func (r *TermsContentRepo) UpdateActive(ctx context.Context, id string, isActive int) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE terms_content
		SET is_active = $2, updated_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL
	`, id, isActive)
	return err
}

func (r *TermsContentRepo) Create(ctx context.Context, heading, body, termsVersion string, sortOrder int) (*TermsContent, error) {
	var tc TermsContent
	err := r.pool.QueryRow(ctx, `
		INSERT INTO terms_content (id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order, updated_at, created_at)
		VALUES (gen_random_uuid()::text, $1, $2, $3, false, '', 'manual_based', 1, $4, NOW(), NOW())
		RETURNING id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order, updated_at, created_at
	`, heading, body, termsVersion, sortOrder).Scan(&tc.ID, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.IsSystem, &tc.FeatureID, &tc.TermType, &tc.IsActive, &tc.SortOrder, &tc.UpdatedAt, &tc.CreatedAt)
	if err != nil {
		return nil, err
	}
	return &tc, nil
}

func (r *TermsContentRepo) Delete(ctx context.Context, id string) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE terms_content
		SET deleted_at = NOW()
		WHERE id = $1 AND is_system = false AND deleted_at IS NULL
	`, id)
	return err
}

func (r *TermsContentRepo) GetMaxSortOrder(ctx context.Context) int {
	var maxOrder int
	err := r.pool.QueryRow(ctx, `
		SELECT COALESCE(MAX(sort_order), 0)
		FROM terms_content
		WHERE deleted_at IS NULL
	`).Scan(&maxOrder)
	if err != nil {
		return 0
	}
	return maxOrder
}

func (r *TermsContentRepo) InsertFeaturedTerm(ctx context.Context, tc *TermsContent) (*TermsContent, error) {
	var result TermsContent
	err := r.pool.QueryRow(ctx, `
		INSERT INTO terms_content (id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order, updated_at, created_at)
		VALUES (gen_random_uuid()::text, $1, $2, $3, $4, $5, $6, $7, $8, NOW(), NOW())
		RETURNING id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order, updated_at, created_at
	`, tc.Heading, tc.Body, tc.TermsVersion, tc.IsSystem, tc.FeatureID, tc.TermType, tc.IsActive, tc.SortOrder).Scan(
		&result.ID, &result.Heading, &result.Body, &result.TermsVersion, &result.IsSystem, &result.FeatureID, &result.TermType, &result.IsActive, &result.SortOrder, &result.UpdatedAt, &result.CreatedAt,
	)
	if err != nil {
		return nil, err
	}
	return &result, nil
}
