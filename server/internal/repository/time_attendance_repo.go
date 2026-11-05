package repository

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type ScheduleRecord struct {
	ShiftID      int
	StartTime    string
	EndTime      string
	WorkingDays  string
	Timezone     string
	GraceMinutes int
	ValidFrom    time.Time
}

type HolidayRecord struct {
	ID     int
	Date   time.Time
	Label  string
	Status string
}

type TimeAttendanceRepo struct {
	pool *pgxpool.Pool
}

func NewTimeAttendanceRepo(pool *pgxpool.Pool) *TimeAttendanceRepo {
	return &TimeAttendanceRepo{pool: pool}
}

func (r *TimeAttendanceRepo) GetScheduleForEmployee(ctx context.Context, employeeID string) (*ScheduleRecord, error) {
	var row ScheduleRecord
	err := r.pool.QueryRow(ctx, `
		SELECT s.id, s.start_time::TEXT, s.end_time::TEXT,
		       s.working_days, s.timezone, s.grace_minutes, e.created_at
		FROM employees e
		JOIN shifts s ON s.id = e.shift_id AND s.deleted_at IS NULL
		WHERE e.employee_id = $1 AND e.deleted_at IS NULL
	`, employeeID).Scan(
		&row.ShiftID, &row.StartTime, &row.EndTime, &row.WorkingDays,
		&row.Timezone, &row.GraceMinutes, &row.ValidFrom,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("get employee schedule: %w", err)
	}
	return &row, nil
}

// ListHolidays returns APPROVED holidays in [from, to] for schedule mirror + attendance.
func (r *TimeAttendanceRepo) ListHolidays(ctx context.Context, from, to time.Time) ([]HolidayRecord, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, holiday_date, label, status
		FROM company_holidays
		WHERE deleted_at IS NULL
		  AND status = 'approved'
		  AND holiday_date >= $1::DATE AND holiday_date <= $2::DATE
		ORDER BY holiday_date ASC
	`, from, to)
	if err != nil {
		return nil, fmt.Errorf("list holidays: %w", err)
	}
	defer rows.Close()

	result := make([]HolidayRecord, 0)
	for rows.Next() {
		var h HolidayRecord
		if err := rows.Scan(&h.ID, &h.Date, &h.Label, &h.Status); err != nil {
			return nil, fmt.Errorf("scan holiday: %w", err)
		}
		result = append(result, h)
	}
	return result, rows.Err()
}

// ListAllHolidays returns every non-deleted holiday for the admin UI (all statuses).
func (r *TimeAttendanceRepo) ListAllHolidays(ctx context.Context) ([]HolidayRecord, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, holiday_date, label, status
		FROM company_holidays
		WHERE deleted_at IS NULL
		ORDER BY holiday_date ASC
	`)
	if err != nil {
		return nil, fmt.Errorf("list all holidays: %w", err)
	}
	defer rows.Close()

	result := make([]HolidayRecord, 0)
	for rows.Next() {
		var h HolidayRecord
		if err := rows.Scan(&h.ID, &h.Date, &h.Label, &h.Status); err != nil {
			return nil, fmt.Errorf("scan holiday: %w", err)
		}
		result = append(result, h)
	}
	return result, rows.Err()
}

func (r *TimeAttendanceRepo) CreateHoliday(ctx context.Context, date time.Time, label, status string) (*HolidayRecord, error) {
	var h HolidayRecord
	err := r.pool.QueryRow(ctx, `
		INSERT INTO company_holidays (holiday_date, label, status)
		VALUES ($1::DATE, $2, $3)
		RETURNING id, holiday_date, label, status
	`, date, label, status).Scan(&h.ID, &h.Date, &h.Label, &h.Status)
	if err != nil {
		return nil, fmt.Errorf("create holiday: %w", err)
	}
	return &h, nil
}

func (r *TimeAttendanceRepo) UpdateHoliday(ctx context.Context, id int, date time.Time, label, status string) (*HolidayRecord, error) {
	var h HolidayRecord
	err := r.pool.QueryRow(ctx, `
		UPDATE company_holidays SET holiday_date = $2::DATE, label = $3, status = $4
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING id, holiday_date, label, status
	`, id, date, label, status).Scan(&h.ID, &h.Date, &h.Label, &h.Status)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("update holiday: %w", err)
	}
	return &h, nil
}

func (r *TimeAttendanceRepo) DeleteHoliday(ctx context.Context, id int) (bool, error) {
	tag, err := r.pool.Exec(ctx, `
		UPDATE company_holidays SET deleted_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL
	`, id)
	if err != nil {
		return false, fmt.Errorf("delete holiday: %w", err)
	}
	return tag.RowsAffected() > 0, nil
}

func (r *TimeAttendanceRepo) ListSessionEvents(
	ctx context.Context, employeeID string, from, to time.Time,
) ([]models.SessionEvent, error) {
	rows, err := r.pool.Query(ctx, `
		WITH prior_state AS (
			SELECT DISTINCT ON (
				CASE WHEN event_type IN ('idle_start', 'idle_end') THEN 'idle' ELSE 'lock' END
			)
			       id, employee_id, event_type, os_username, event_at,
			       event_count, COALESCE(first_at, event_at) AS first_at,
			       COALESCE(last_at, event_at) AS last_at, synced_at, created_at
			FROM session_events
			WHERE employee_id = $1 AND deleted_at IS NULL AND event_at < $2
			  AND event_type IN ('idle_start', 'idle_end', 'screen_lock', 'screen_unlock')
			ORDER BY
				CASE WHEN event_type IN ('idle_start', 'idle_end') THEN 'idle' ELSE 'lock' END,
				event_at DESC
		),
		current_events AS (
			SELECT id, employee_id, event_type, os_username, event_at,
			       event_count, COALESCE(first_at, event_at) AS first_at,
			       COALESCE(last_at, event_at) AS last_at, synced_at, created_at
			FROM session_events
			WHERE employee_id = $1 AND deleted_at IS NULL
			  AND event_at >= $2 AND event_at < $3
		)
		SELECT * FROM (
			SELECT * FROM prior_state
			UNION ALL
			SELECT * FROM current_events
		) attendance_events
		ORDER BY event_at ASC
	`, employeeID, from, to)
	if err != nil {
		return nil, fmt.Errorf("list attendance events: %w", err)
	}
	defer rows.Close()

	result := make([]models.SessionEvent, 0)
	for rows.Next() {
		var e models.SessionEvent
		if err := rows.Scan(
			&e.ID, &e.EmployeeID, &e.EventType, &e.OsUsername, &e.EventAt,
			&e.EventCount, &e.FirstAt, &e.LastAt, &e.SyncedAt, &e.CreatedAt,
		); err != nil {
			return nil, fmt.Errorf("scan attendance event: %w", err)
		}
		result = append(result, e)
	}
	return result, rows.Err()
}

// ListDaySessionEvents returns events that occurred in [from, to) — no prior-state rows.
func (r *TimeAttendanceRepo) ListDaySessionEvents(
	ctx context.Context, employeeID string, from, to time.Time,
) ([]models.SessionEvent, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, employee_id, event_type, os_username, event_at,
		       event_count, COALESCE(first_at, event_at) AS first_at,
		       COALESCE(last_at, event_at) AS last_at, synced_at, created_at
		FROM session_events
		WHERE employee_id = $1 AND deleted_at IS NULL
		  AND event_at >= $2 AND event_at < $3
		ORDER BY event_at ASC
		LIMIT 1000
	`, employeeID, from, to)
	if err != nil {
		return nil, fmt.Errorf("list day session events: %w", err)
	}
	defer rows.Close()

	result := make([]models.SessionEvent, 0)
	for rows.Next() {
		var e models.SessionEvent
		if err := rows.Scan(
			&e.ID, &e.EmployeeID, &e.EventType, &e.OsUsername, &e.EventAt,
			&e.EventCount, &e.FirstAt, &e.LastAt, &e.SyncedAt, &e.CreatedAt,
		); err != nil {
			return nil, fmt.Errorf("scan day session event: %w", err)
		}
		result = append(result, e)
	}
	return result, rows.Err()
}

// GetLastHeartbeat returns the freshest last_heartbeat_at across all machines for
// the employee. Uses GREATEST(parsed value, updated_at) so a successful sync counts
// as alive even when the embedded value lags by ~1 collect interval. Without an
// ORDER BY / MAX across machine_id rows, QueryRow used to return an arbitrary
// (often stale) machine's heartbeat after migration 046.
func (r *TimeAttendanceRepo) GetLastHeartbeat(ctx context.Context, employeeID string) (*time.Time, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT value, updated_at
		FROM app_status
		WHERE employee_id = $1 AND key = 'last_heartbeat_at'
	`, employeeID)
	if err != nil {
		return nil, fmt.Errorf("get heartbeat: %w", err)
	}
	defer rows.Close()

	var best *time.Time
	for rows.Next() {
		var value string
		var updatedAt time.Time
		if err := rows.Scan(&value, &updatedAt); err != nil {
			return nil, fmt.Errorf("scan heartbeat: %w", err)
		}
		candidate := heartbeatAliveAt(value, updatedAt)
		if best == nil || candidate.After(*best) {
			copy := candidate
			best = &copy
		}
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("get heartbeat: %w", err)
	}
	return best, nil
}

// GetLastDeviceSeen returns MAX(last_seen_at) across non-revoked device tokens for
// the employee. DeviceAuth TouchLastSeen refreshes this on every sync / presence
// upgrade, so it stays current even when app_status.last_heartbeat_at stalls
// (e.g. post-reboot collect loop gap while app-sessions still drain).
func (r *TimeAttendanceRepo) GetLastDeviceSeen(ctx context.Context, employeeID string) (*time.Time, error) {
	var lastSeen *time.Time
	err := r.pool.QueryRow(ctx, `
		SELECT MAX(last_seen_at)
		FROM employee_devices
		WHERE employee_id = $1 AND revoked_at IS NULL
	`, employeeID).Scan(&lastSeen)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("get device last seen: %w", err)
	}
	if lastSeen == nil || lastSeen.IsZero() {
		return nil, nil
	}
	utc := lastSeen.UTC()
	return &utc, nil
}

// ListLastHeartbeats returns employee_id → best "last seen" time for live-stream online.
// Uses GREATEST(parsed heartbeat value, app_status.updated_at) so a successful sync
// counts as alive even when the embedded value is already ~1 sync-interval old.
// When multiple machine_id rows exist, keeps the freshest per employee.
func (r *TimeAttendanceRepo) ListLastHeartbeats(ctx context.Context) (map[string]time.Time, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT employee_id, value, updated_at
		FROM app_status
		WHERE key = 'last_heartbeat_at'
	`)
	if err != nil {
		return nil, fmt.Errorf("list heartbeats: %w", err)
	}
	defer rows.Close()

	out := make(map[string]time.Time)
	for rows.Next() {
		var empID, value string
		var updatedAt time.Time
		if err := rows.Scan(&empID, &value, &updatedAt); err != nil {
			return nil, fmt.Errorf("scan heartbeat: %w", err)
		}
		best := heartbeatAliveAt(value, updatedAt)
		if prev, ok := out[empID]; !ok || best.After(prev) {
			out[empID] = best
		}
	}
	return out, rows.Err()
}

// heartbeatAliveAt mirrors live-stream online: GREATEST(parsed value, updated_at).
func heartbeatAliveAt(value string, updatedAt time.Time) time.Time {
	best := updatedAt.UTC()
	if parsed, err := time.Parse(time.RFC3339Nano, value); err == nil {
		if parsed.UTC().After(best) {
			best = parsed.UTC()
		}
	} else if parsed, err := time.Parse(time.RFC3339, value); err == nil {
		if parsed.UTC().After(best) {
			best = parsed.UTC()
		}
	}
	return best
}
