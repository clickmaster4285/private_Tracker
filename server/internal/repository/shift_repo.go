package repository

import (
	"context"
	"errors"
	"fmt"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

// ShiftRepo handles database operations for the shift catalog (see migration
// 027_shifts.sql). The shift_id column on `employees` is the sole assignment
// surface; this repo manages the catalog rows and projects the per-shift
// employee count for the web admin page.
type ShiftRepo struct {
	pool *pgxpool.Pool
}

// NewShiftRepo creates a new ShiftRepo.
func NewShiftRepo(pool *pgxpool.Pool) *ShiftRepo {
	return &ShiftRepo{pool: pool}
}

// ShiftListParams filters the shift list.
type ShiftListParams struct {
	Search  string
	Page    int
	PerPage int
}

// ShiftListResult is the paginated shift list response.
type ShiftListResult struct {
	Shifts     []models.Shift
	Total      int
	Page       int
	PerPage    int
	TotalPages int
}

// List returns all non-deleted shifts with their assigned-employee counts.
func (r *ShiftRepo) List(ctx context.Context, params ShiftListParams) (*ShiftListResult, error) {
	if params.Page < 1 {
		params.Page = 1
	}
	if params.PerPage < 1 || params.PerPage > 100 {
		params.PerPage = 20
	}

	var conditions []string
	var args []interface{}
	argIdx := 1
	conditions = append(conditions, "s.deleted_at IS NULL")

	if params.Search != "" {
		conditions = append(conditions, fmt.Sprintf("LOWER(s.name) LIKE LOWER($%d)", argIdx))
		args = append(args, "%"+params.Search+"%")
		argIdx++
	}

	whereClause := "WHERE " + strings.Join(conditions, " AND ")

	var total int
	if err := r.pool.QueryRow(ctx,
		fmt.Sprintf("SELECT COUNT(*) FROM shifts s %s", whereClause),
		args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count shifts: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	query := fmt.Sprintf(`
		SELECT s.id, s.name,
		       s.start_time::TEXT, s.end_time::TEXT,
		       COALESCE(s.working_days, '') AS working_days,
		       COALESCE(s.timezone, 'UTC') AS timezone,
		       COALESCE(s.grace_minutes, 0) AS grace_minutes,
		       COALESCE(s.overtime_hours, 0) AS overtime_hours,
		       COALESCE(s.description, ''),
		       COALESCE(e.count, 0) AS employee_count,
		       s.created_at, s.updated_at, s.deleted_at
		FROM shifts s
		LEFT JOIN (
			SELECT shift_id, COUNT(*) AS count
			FROM employees
			WHERE deleted_at IS NULL AND shift_id IS NOT NULL
			GROUP BY shift_id
		) e ON e.shift_id = s.id
		%s
		ORDER BY LOWER(s.name) ASC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list shifts: %w", err)
	}
	defer rows.Close()

	var shifts []models.Shift
	for rows.Next() {
		var s models.Shift
		if err := rows.Scan(
			&s.ID, &s.Name,
			&s.StartTime, &s.EndTime,
			&s.WorkingDays, &s.Timezone, &s.GraceMinutes, &s.OvertimeHours,
			&s.Description, &s.EmployeeCount,
			&s.CreatedAt, &s.UpdatedAt, &s.DeletedAt,
		); err != nil {
			return nil, fmt.Errorf("scan shift: %w", err)
		}
		shifts = append(shifts, s)
	}

	return &ShiftListResult{
		Shifts:     shifts,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}

// ListAll returns every non-deleted shift (used by the employee form to populate
// the shift dropdown). Ordered by name for stable UI.
func (r *ShiftRepo) ListAll(ctx context.Context) ([]models.Shift, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT s.id, s.name,
		       s.start_time::TEXT, s.end_time::TEXT,
		       COALESCE(s.working_days, '') AS working_days,
		       COALESCE(s.timezone, 'UTC') AS timezone,
		       COALESCE(s.grace_minutes, 0) AS grace_minutes,
		       COALESCE(s.overtime_hours, 0) AS overtime_hours,
		       COALESCE(s.description, ''),
		       0 AS employee_count,
		       s.created_at, s.updated_at, s.deleted_at
		FROM shifts s
		WHERE s.deleted_at IS NULL
		ORDER BY LOWER(s.name) ASC
	`)
	if err != nil {
		return nil, fmt.Errorf("list all shifts: %w", err)
	}
	defer rows.Close()

	var shifts []models.Shift
	for rows.Next() {
		var s models.Shift
		if err := rows.Scan(
			&s.ID, &s.Name,
			&s.StartTime, &s.EndTime,
			&s.WorkingDays, &s.Timezone, &s.GraceMinutes, &s.OvertimeHours,
			&s.Description, &s.EmployeeCount,
			&s.CreatedAt, &s.UpdatedAt, &s.DeletedAt,
		); err != nil {
			return nil, fmt.Errorf("scan shift: %w", err)
		}
		shifts = append(shifts, s)
	}
	return shifts, rows.Err()
}

// GetByID returns a single shift by id.
func (r *ShiftRepo) GetByID(ctx context.Context, id int) (*models.Shift, error) {
	var s models.Shift
	err := r.pool.QueryRow(ctx, `
		SELECT id, name,
		       start_time::TEXT, end_time::TEXT,
		       COALESCE(working_days, '') AS working_days,
		       COALESCE(timezone, 'UTC') AS timezone,
		       COALESCE(grace_minutes, 0) AS grace_minutes,
		       COALESCE(overtime_hours, 0) AS overtime_hours,
		       COALESCE(description, ''),
		       0 AS employee_count,
		       created_at, updated_at, deleted_at
		FROM shifts
		WHERE id = $1 AND deleted_at IS NULL
	`, id).Scan(
		&s.ID, &s.Name,
		&s.StartTime, &s.EndTime,
		&s.WorkingDays, &s.Timezone, &s.GraceMinutes, &s.OvertimeHours,
		&s.Description, &s.EmployeeCount,
		&s.CreatedAt, &s.UpdatedAt, &s.DeletedAt,
	)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, fmt.Errorf("get shift: %w", err)
	}
	return &s, nil
}

// Create inserts a new shift and returns it.
func (r *ShiftRepo) Create(ctx context.Context, s *models.Shift) (*models.Shift, error) {
	var created models.Shift
	err := r.pool.QueryRow(ctx, `
		INSERT INTO shifts (name, start_time, end_time, working_days, timezone, grace_minutes, overtime_hours, description)
		VALUES ($1, $2::TIME, $3::TIME, $4, $5, $6, $7, $8)
		RETURNING id, name,
		          start_time::TEXT, end_time::TEXT,
		          COALESCE(working_days, '') AS working_days,
		          COALESCE(timezone, 'UTC') AS timezone,
		          COALESCE(grace_minutes, 0) AS grace_minutes,
		          COALESCE(overtime_hours, 0) AS overtime_hours,
		          COALESCE(description, ''),
		          0 AS employee_count,
		          created_at, updated_at, NULL::TIMESTAMPTZ AS deleted_at
	`, s.Name, s.StartTime, s.EndTime, s.WorkingDays, s.Timezone, s.GraceMinutes, s.OvertimeHours, s.Description,
	).Scan(
		&created.ID, &created.Name,
		&created.StartTime, &created.EndTime,
		&created.WorkingDays, &created.Timezone, &created.GraceMinutes, &created.OvertimeHours,
		&created.Description, &created.EmployeeCount,
		&created.CreatedAt, &created.UpdatedAt, &created.DeletedAt,
	)
	if err != nil {
		return nil, fmt.Errorf("create shift: %w", err)
	}
	return &created, nil
}

// Update modifies a shift and returns the refreshed row.
func (r *ShiftRepo) Update(ctx context.Context, id int, s *models.Shift) (*models.Shift, error) {
	var updated models.Shift
	err := r.pool.QueryRow(ctx, `
		UPDATE shifts SET
			name = $1,
			start_time = $2::TIME,
			end_time = $3::TIME,
			working_days = $4,
			timezone = $5,
			grace_minutes = $6,
			overtime_hours = $7,
			description = $8
		WHERE id = $9 AND deleted_at IS NULL
		RETURNING id, name,
		          start_time::TEXT, end_time::TEXT,
		          COALESCE(working_days, '') AS working_days,
		          COALESCE(timezone, 'UTC') AS timezone,
		          COALESCE(grace_minutes, 0) AS grace_minutes,
		          COALESCE(overtime_hours, 0) AS overtime_hours,
		          COALESCE(description, ''),
		          0 AS employee_count,
		          created_at, updated_at, NULL::TIMESTAMPTZ AS deleted_at
	`, s.Name, s.StartTime, s.EndTime, s.WorkingDays, s.Timezone, s.GraceMinutes, s.OvertimeHours, s.Description, id,
	).Scan(
		&updated.ID, &updated.Name,
		&updated.StartTime, &updated.EndTime,
		&updated.WorkingDays, &updated.Timezone, &updated.GraceMinutes, &updated.OvertimeHours,
		&updated.Description, &updated.EmployeeCount,
		&updated.CreatedAt, &updated.UpdatedAt, &updated.DeletedAt,
	)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, fmt.Errorf("update shift: %w", err)
	}
	return &updated, nil
}

// CountShiftUsage returns how many non-deleted employees currently use this shift.
func (r *ShiftRepo) CountShiftUsage(ctx context.Context, id int) (int, error) {
	var count int
	err := r.pool.QueryRow(ctx,
		"SELECT COUNT(*) FROM employees WHERE shift_id = $1 AND deleted_at IS NULL", id,
	).Scan(&count)
	if err != nil {
		return 0, fmt.Errorf("count shift usage: %w", err)
	}
	return count, nil
}

// Delete soft-deletes a shift. Returns "shift not found" if no row matched.
// The FK on employees.shift_id is ON DELETE SET NULL, so any in-use shift
// gracefully detaches its employees on hard delete; we forbid hard delete
// only when the shift is in use so the soft-delete path keeps the link
// visible until the admin re-assigns.
func (r *ShiftRepo) Delete(ctx context.Context, id int) error {
	tag, err := r.pool.Exec(ctx,
		"UPDATE shifts SET deleted_at = NOW() WHERE id = $1 AND deleted_at IS NULL", id,
	)
	if err != nil {
		return fmt.Errorf("delete shift: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("shift not found")
	}
	return nil
}

// ApplyDefaultTimezone replaces the migration placeholder UTC on every active
// shift row. Idempotent — safe to run on every boot when DEFAULT_SHIFT_TIMEZONE
// is configured.
func (r *ShiftRepo) ApplyDefaultTimezone(ctx context.Context, timezone string) (int64, error) {
	tag, err := r.pool.Exec(ctx, `
		UPDATE shifts
		SET timezone = $1, updated_at = NOW()
		WHERE deleted_at IS NULL AND timezone = 'UTC'
	`, timezone)
	if err != nil {
		return 0, fmt.Errorf("apply default shift timezone: %w", err)
	}
	return tag.RowsAffected(), nil
}
