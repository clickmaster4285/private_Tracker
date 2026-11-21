package repository

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5"
)

// ShiftExtensionRecord is one "give extra time" grant (migration 079).
type ShiftExtensionRecord struct {
	ID            int64
	EmployeeID    string
	StartDate     time.Time
	EndDate       time.Time
	BeforeMinutes int
	AfterMinutes  int
	Reason        string
	CreatedBy     string
	CreatedAt     time.Time
}

const shiftExtensionCols = `id, employee_id, start_date, end_date, before_minutes, after_minutes, reason, created_by, created_at`

func scanShiftExtension(row pgx.Row) (*ShiftExtensionRecord, error) {
	var r ShiftExtensionRecord
	if err := row.Scan(&r.ID, &r.EmployeeID, &r.StartDate, &r.EndDate, &r.BeforeMinutes,
		&r.AfterMinutes, &r.Reason, &r.CreatedBy, &r.CreatedAt); err != nil {
		return nil, err
	}
	return &r, nil
}

// ListShiftExtensions returns the employee's active grants that overlap [from, to] (dates).
func (r *TimeAttendanceRepo) ListShiftExtensions(ctx context.Context, employeeID string, from, to time.Time) ([]ShiftExtensionRecord, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT `+shiftExtensionCols+`
		FROM shift_extensions
		WHERE employee_id = $1 AND deleted_at IS NULL
		  AND end_date >= $2::DATE AND start_date <= $3::DATE
		ORDER BY start_date ASC, id ASC
	`, employeeID, from, to)
	if err != nil {
		return nil, fmt.Errorf("list shift extensions: %w", err)
	}
	defer rows.Close()
	out := make([]ShiftExtensionRecord, 0)
	for rows.Next() {
		rec, err := scanShiftExtension(rows)
		if err != nil {
			return nil, fmt.Errorf("scan shift extension: %w", err)
		}
		out = append(out, *rec)
	}
	return out, rows.Err()
}

// CreateShiftExtension stores a grant.
func (r *TimeAttendanceRepo) CreateShiftExtension(ctx context.Context, rec ShiftExtensionRecord) (*ShiftExtensionRecord, error) {
	row := r.pool.QueryRow(ctx, `
		INSERT INTO shift_extensions (employee_id, start_date, end_date, before_minutes, after_minutes, reason, created_by)
		VALUES ($1, $2::DATE, $3::DATE, $4, $5, $6, $7)
		RETURNING `+shiftExtensionCols,
		rec.EmployeeID, rec.StartDate, rec.EndDate, rec.BeforeMinutes, rec.AfterMinutes, rec.Reason, rec.CreatedBy)
	out, err := scanShiftExtension(row)
	if err != nil {
		return nil, fmt.Errorf("create shift extension: %w", err)
	}
	return out, nil
}

// DeleteShiftExtension soft-deletes a grant; returns its employee id ("" when not found).
func (r *TimeAttendanceRepo) DeleteShiftExtension(ctx context.Context, id int64) (string, error) {
	var employeeID string
	err := r.pool.QueryRow(ctx, `
		UPDATE shift_extensions SET deleted_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING employee_id
	`, id).Scan(&employeeID)
	if errors.Is(err, pgx.ErrNoRows) {
		return "", nil
	}
	if err != nil {
		return "", fmt.Errorf("delete shift extension: %w", err)
	}
	return employeeID, nil
}
