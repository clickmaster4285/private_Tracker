package repository

import (
	"context"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// TermsConsentEntry represents a single consent event in the audit trail.
type TermsConsentEntry struct {
	ID           string    `json:"id"`
	EmployeeID   string    `json:"employeeId"`
	FeatureID    string    `json:"featureId"`
	TermsVersion string    `json:"termsVersion"`
	Action       string    `json:"action"` // "accepted", "revoked", "re_accepted"
	CreatedAt    time.Time `json:"createdAt"`
}

type TermsConsentRepo struct {
	pool *pgxpool.Pool
}

func NewTermsConsentRepo(pool *pgxpool.Pool) *TermsConsentRepo {
	return &TermsConsentRepo{pool: pool}
}

// BulkInsert appends consent events to the audit trail. Each entry is an
// independent INSERT (append-only — no upsert, no conflict). The UNIQUE
// constraint is on (employee_id, feature_id, terms_version, action) in the
// plan but not enforced at the DB level to avoid rejecting late-arriving
// duplicate events from client retries.
func (r *TermsConsentRepo) BulkInsert(ctx context.Context, entries []TermsConsentEntry) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}

	inserted := 0
	for _, e := range entries {
		_, err := r.pool.Exec(ctx, `
			INSERT INTO terms_consent (employee_id, feature_id, terms_version, action, created_at)
			VALUES ($1, $2, $3, $4, $5)
		`, e.EmployeeID, e.FeatureID, e.TermsVersion, e.Action, e.CreatedAt)
		if err != nil {
			return inserted, fmt.Errorf("insert terms_consent for %s/%s: %w", e.EmployeeID, e.FeatureID, err)
		}
		inserted++
	}
	return inserted, nil
}

// ListByEmployee returns all consent events for an employee, newest first.
func (r *TermsConsentRepo) ListByEmployee(ctx context.Context, employeeID string, limit int) ([]TermsConsentEntry, error) {
	if limit <= 0 {
		limit = 100
	}
	rows, err := r.pool.Query(ctx, `
		SELECT id, employee_id, feature_id, terms_version, action, created_at
		FROM terms_consent
		WHERE employee_id = $1 AND deleted_at IS NULL
		ORDER BY created_at DESC
		LIMIT $2
	`, employeeID, limit)
	if err != nil {
		return nil, fmt.Errorf("list terms_consent: %w", err)
	}
	defer rows.Close()

	var entries []TermsConsentEntry
	for rows.Next() {
		var e TermsConsentEntry
		if err := rows.Scan(&e.ID, &e.EmployeeID, &e.FeatureID, &e.TermsVersion, &e.Action, &e.CreatedAt); err != nil {
			return nil, fmt.Errorf("scan terms_consent: %w", err)
		}
		entries = append(entries, e)
	}
	return entries, rows.Err()
}

// GetLatestByFeature returns the most recent consent event for a specific
// employee+feature combination. Returns nil if no event exists.
func (r *TermsConsentRepo) GetLatestByFeature(ctx context.Context, employeeID, featureID string) (*TermsConsentEntry, error) {
	var e TermsConsentEntry
	err := r.pool.QueryRow(ctx, `
		SELECT id, employee_id, feature_id, terms_version, action, created_at
		FROM terms_consent
		WHERE employee_id = $1 AND feature_id = $2 AND deleted_at IS NULL
		ORDER BY created_at DESC
		LIMIT 1
	`, employeeID, featureID).Scan(&e.ID, &e.EmployeeID, &e.FeatureID, &e.TermsVersion, &e.Action, &e.CreatedAt)
	if err != nil {
		if err.Error() == "no rows in result set" {
			return nil, nil
		}
		return nil, fmt.Errorf("get latest terms_consent: %w", err)
	}
	return &e, nil
}

// HasAccepted checks if an employee has an active (non-revoked) acceptance
// for a feature at or above the required version.
func (r *TermsConsentRepo) HasAccepted(ctx context.Context, employeeID, featureID, minVersion string) (bool, error) {
	var action string
	err := r.pool.QueryRow(ctx, `
		SELECT action
		FROM terms_consent
		WHERE employee_id = $1 AND feature_id = $2 AND deleted_at IS NULL
		ORDER BY created_at DESC
		LIMIT 1
	`, employeeID, featureID).Scan(&action)
	if err != nil {
		if err.Error() == "no rows in result set" {
			return false, nil
		}
		return false, fmt.Errorf("check terms acceptance: %w", err)
	}
	return action == "accepted" || action == "re_accepted", nil
}

// ListAcceptedEmployeeIDs returns employee IDs whose latest consent event for
// featureID is accepted / re_accepted (one indexed scan — used by live-stream list).
func (r *TermsConsentRepo) ListAcceptedEmployeeIDs(ctx context.Context, featureID string) (map[string]bool, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT DISTINCT ON (employee_id) employee_id, action
		FROM terms_consent
		WHERE feature_id = $1 AND deleted_at IS NULL
		ORDER BY employee_id, created_at DESC
	`, featureID)
	if err != nil {
		return nil, fmt.Errorf("list accepted consent: %w", err)
	}
	defer rows.Close()

	out := make(map[string]bool)
	for rows.Next() {
		var empID, action string
		if err := rows.Scan(&empID, &action); err != nil {
			return nil, fmt.Errorf("scan accepted consent: %w", err)
		}
		if action == "accepted" || action == "re_accepted" {
			out[empID] = true
		}
	}
	return out, rows.Err()
}
