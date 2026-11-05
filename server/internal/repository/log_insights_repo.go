package repository

import (
	"context"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// LogInsightsRepo runs aggregates for GET /logs/insights.
type LogInsightsRepo struct {
	pool *pgxpool.Pool
}

// NewLogInsightsRepo constructs a LogInsightsRepo.
func NewLogInsightsRepo(pool *pgxpool.Pool) *LogInsightsRepo {
	return &LogInsightsRepo{pool: pool}
}

// LogInsightsParams are SQL-side filters (from inclusive, to exclusive).
type LogInsightsParams struct {
	From         time.Time
	To           time.Time
	DepartmentID *int
	EmployeeID   string // optional EMP- code; empty = all
	TopN         int
	OutlierLimit int
}

// LogInsightsProductivityRow is classified session duration totals.
type LogInsightsProductivityRow struct {
	TotalSeconds        int64
	ProductiveSeconds   int64
	UnproductiveSeconds int64
	NeutralSeconds      int64
}

// LogInsightsIdleStats counts idle_start markers in range.
type LogInsightsIdleStats struct {
	IdleEvents        int
	EmployeesWithIdle int
}

// LogInsightsOutlierRow is one employee standing out on a metric.
type LogInsightsOutlierRow struct {
	EmployeeID   string
	EmployeeName string
	Kind         string
	Value        int
}

func (r *LogInsightsRepo) ActiveEmployees(ctx context.Context, p LogInsightsParams) (int, error) {
	q := `
		SELECT COUNT(DISTINCT s.employee_id)
		FROM app_sessions s
		WHERE s.deleted_at IS NULL
		  AND s.started_at >= $1 AND s.started_at < $2
		  AND ($3::text = '' OR s.employee_id = $3)
		  AND ($4::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = s.employee_id AND e.deleted_at IS NULL AND e.department_id = $4
		  ))
	`
	var n int
	if err := r.pool.QueryRow(ctx, q, p.From, p.To, p.EmployeeID, p.DepartmentID).Scan(&n); err != nil {
		return 0, fmt.Errorf("log insights active employees: %w", err)
	}
	return n, nil
}

// ProductivityTotals sums clamped session durations classified via monitoring_types.
// Classification matches hours-insights: DISTINCT ON lower(app_name) preferring typed rows.
// Only sessions that STARTED in range are included; overlapping sessions are
// island-merged per employee (wall-clock) and classified shares are scaled.
func (r *LogInsightsRepo) ProductivityTotals(ctx context.Context, p LogInsightsParams) (LogInsightsProductivityRow, error) {
	q := `
		WITH app_cat AS (
			SELECT DISTINCT ON (lower(ia.app_name)) lower(ia.app_name) AS app_key,
				NULLIF(mt.name, '') AS type_name
			FROM installed_applications ia
			LEFT JOIN monitoring_types mt ON mt.id = ia.type_id AND mt.deleted_at IS NULL
			WHERE ia.deleted_at IS NULL AND ia.app_name <> ''
			ORDER BY lower(ia.app_name), (ia.type_id IS NULL), ia.id
		),
		raw AS (
			SELECT
				s.employee_id,
				COALESCE(NULLIF(s.app_display_name, ''), s.process_name, '') AS app_display_name,
				GREATEST(s.started_at, $1::timestamptz) AS eff_start,
				LEAST(
					CASE
						WHEN s.status = 'ACTIVE' AND s.ended_at IS NULL
							AND s.last_sync_at > NOW() - INTERVAL '10 minutes' THEN NOW()
						ELSE COALESCE(s.ended_at, s.last_sync_at, s.last_activity_at, s.started_at)
					END,
					$2::timestamptz
				) AS eff_end
			FROM app_sessions s
			WHERE s.deleted_at IS NULL
			  AND s.started_at >= $1 AND s.started_at < $2
			  AND ($3::text = '' OR s.employee_id = $3)
			  AND ($4::int IS NULL OR EXISTS (
			      SELECT 1 FROM employees e
			      WHERE e.employee_id = s.employee_id AND e.deleted_at IS NULL AND e.department_id = $4
			  ))
		),
		valid AS (
			SELECT * FROM raw WHERE eff_end > eff_start
		),
		ordered AS (
			SELECT v.*,
			       MAX(eff_end) OVER (
			           PARTITION BY employee_id
			           ORDER BY eff_start, eff_end
			           ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
			       ) AS prior_max_end
			FROM valid v
		),
		marked AS (
			SELECT o.*,
			       CASE WHEN prior_max_end IS NULL OR eff_start > prior_max_end
			            THEN 1 ELSE 0 END AS new_island
			FROM ordered o
		),
		islands AS (
			SELECT m.*,
			       SUM(new_island) OVER (
			           PARTITION BY employee_id
			           ORDER BY eff_start, eff_end
			           ROWS UNBOUNDED PRECEDING
			       ) AS island_id
			FROM marked m
		),
		merged AS (
			SELECT employee_id, island_id,
			       MIN(eff_start) AS eff_start, MAX(eff_end) AS eff_end
			FROM islands
			GROUP BY employee_id, island_id
		),
		wall AS (
			SELECT COALESCE(SUM(GREATEST(0, EXTRACT(EPOCH FROM (eff_end - eff_start)))), 0)::bigint AS wall_sec
			FROM merged
		),
		classified AS (
			SELECT
				COALESCE(SUM(CASE WHEN ac.type_name = 'Productive'
					THEN GREATEST(0, EXTRACT(EPOCH FROM (v.eff_end - v.eff_start))) ELSE 0 END), 0)::bigint AS productive_sec,
				COALESCE(SUM(CASE WHEN ac.type_name = 'Unproductive'
					THEN GREATEST(0, EXTRACT(EPOCH FROM (v.eff_end - v.eff_start))) ELSE 0 END), 0)::bigint AS unproductive_sec,
				COALESCE(SUM(CASE WHEN ac.type_name IS NULL OR ac.type_name NOT IN ('Productive', 'Unproductive')
					THEN GREATEST(0, EXTRACT(EPOCH FROM (v.eff_end - v.eff_start))) ELSE 0 END), 0)::bigint AS neutral_sec
			FROM valid v
			LEFT JOIN app_cat ac ON ac.app_key = lower(v.app_display_name)
		)
		SELECT
			w.wall_sec,
			CASE WHEN (c.productive_sec + c.unproductive_sec + c.neutral_sec) > 0
				THEN (c.productive_sec * w.wall_sec / (c.productive_sec + c.unproductive_sec + c.neutral_sec))
				ELSE 0 END,
			CASE WHEN (c.productive_sec + c.unproductive_sec + c.neutral_sec) > 0
				THEN (c.unproductive_sec * w.wall_sec / (c.productive_sec + c.unproductive_sec + c.neutral_sec))
				ELSE 0 END,
			CASE WHEN (c.productive_sec + c.unproductive_sec + c.neutral_sec) > 0
				THEN (c.neutral_sec * w.wall_sec / (c.productive_sec + c.unproductive_sec + c.neutral_sec))
				ELSE 0 END
		FROM wall w, classified c
	`
	var out LogInsightsProductivityRow
	err := r.pool.QueryRow(ctx, q, p.From, p.To, p.EmployeeID, p.DepartmentID).Scan(
		&out.TotalSeconds, &out.ProductiveSeconds, &out.UnproductiveSeconds, &out.NeutralSeconds,
	)
	if err != nil {
		return out, fmt.Errorf("log insights productivity: %w", err)
	}
	return out, nil
}

func (r *LogInsightsRepo) IdleStats(ctx context.Context, p LogInsightsParams) (LogInsightsIdleStats, error) {
	q := `
		SELECT
			COUNT(*)::int,
			COUNT(DISTINCT se.employee_id)::int
		FROM session_events se
		WHERE se.deleted_at IS NULL
		  AND se.event_type = 'idle_start'
		  AND se.event_at >= $1 AND se.event_at < $2
		  AND ($3::text = '' OR se.employee_id = $3)
		  AND ($4::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = se.employee_id AND e.deleted_at IS NULL AND e.department_id = $4
		  ))
	`
	var out LogInsightsIdleStats
	if err := r.pool.QueryRow(ctx, q, p.From, p.To, p.EmployeeID, p.DepartmentID).Scan(
		&out.IdleEvents, &out.EmployeesWithIdle,
	); err != nil {
		return out, fmt.Errorf("log insights idle stats: %w", err)
	}
	return out, nil
}

// TopIdleEmployees returns employees with the most idle_start events in range.
func (r *LogInsightsRepo) TopIdleEmployees(ctx context.Context, p LogInsightsParams) ([]LogInsightsOutlierRow, error) {
	q := `
		SELECT se.employee_id, COALESCE(e.name, se.employee_id), COUNT(*)::int
		FROM session_events se
		LEFT JOIN employees e ON e.employee_id = se.employee_id AND e.deleted_at IS NULL
		WHERE se.deleted_at IS NULL
		  AND se.event_type = 'idle_start'
		  AND se.event_at >= $1 AND se.event_at < $2
		  AND ($3::text = '' OR se.employee_id = $3)
		  AND ($4::int IS NULL OR e.department_id = $4)
		GROUP BY se.employee_id, e.name
		ORDER BY COUNT(*) DESC, se.employee_id ASC
		LIMIT $5
	`
	rows, err := r.pool.Query(ctx, q, p.From, p.To, p.EmployeeID, p.DepartmentID, p.OutlierLimit)
	if err != nil {
		return nil, fmt.Errorf("log insights top idle: %w", err)
	}
	defer rows.Close()
	out := make([]LogInsightsOutlierRow, 0, p.OutlierLimit)
	for rows.Next() {
		var row LogInsightsOutlierRow
		if err := rows.Scan(&row.EmployeeID, &row.EmployeeName, &row.Value); err != nil {
			return nil, fmt.Errorf("scan idle outlier: %w", err)
		}
		row.Kind = "idle"
		out = append(out, row)
	}
	return out, rows.Err()
}

// TopSessionEmployees returns employees with the most app sessions in range.
func (r *LogInsightsRepo) TopSessionEmployees(ctx context.Context, p LogInsightsParams) ([]LogInsightsOutlierRow, error) {
	q := `
		SELECT s.employee_id, COALESCE(e.name, s.employee_id), COUNT(*)::int
		FROM app_sessions s
		LEFT JOIN employees e ON e.employee_id = s.employee_id AND e.deleted_at IS NULL
		WHERE s.deleted_at IS NULL
		  AND s.started_at >= $1 AND s.started_at < $2
		  AND ($3::text = '' OR s.employee_id = $3)
		  AND ($4::int IS NULL OR e.department_id = $4)
		GROUP BY s.employee_id, e.name
		ORDER BY COUNT(*) DESC, s.employee_id ASC
		LIMIT $5
	`
	rows, err := r.pool.Query(ctx, q, p.From, p.To, p.EmployeeID, p.DepartmentID, p.OutlierLimit)
	if err != nil {
		return nil, fmt.Errorf("log insights top sessions: %w", err)
	}
	defer rows.Close()
	out := make([]LogInsightsOutlierRow, 0, p.OutlierLimit)
	for rows.Next() {
		var row LogInsightsOutlierRow
		if err := rows.Scan(&row.EmployeeID, &row.EmployeeName, &row.Value); err != nil {
			return nil, fmt.Errorf("scan session outlier: %w", err)
		}
		row.Kind = "sessions"
		out = append(out, row)
	}
	return out, rows.Err()
}

// ActivityCounts mirrors dashboard activity with optional employee filter.
func (r *LogInsightsRepo) ActivityCounts(ctx context.Context, p LogInsightsParams) (DashboardActivityCounts, error) {
	var out DashboardActivityCounts
	sessionsQ := `
		SELECT COUNT(*)
		FROM app_sessions s
		WHERE s.deleted_at IS NULL
		  AND s.started_at >= $1 AND s.started_at < $2
		  AND ($3::text = '' OR s.employee_id = $3)
		  AND ($4::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = s.employee_id AND e.deleted_at IS NULL AND e.department_id = $4
		  ))
	`
	if err := r.pool.QueryRow(ctx, sessionsQ, p.From, p.To, p.EmployeeID, p.DepartmentID).Scan(&out.Sessions); err != nil {
		return out, fmt.Errorf("log insights sessions: %w", err)
	}
	webQ := `
		SELECT COUNT(*)
		FROM app_items i
		WHERE i.deleted_at IS NULL
		  AND i.item_type = 'browser_tab'
		  AND i.opened_at >= $1 AND i.opened_at < $2
		  AND ($3::text = '' OR i.employee_id = $3)
		  AND ($4::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = i.employee_id AND e.deleted_at IS NULL AND e.department_id = $4
		  ))
	`
	if err := r.pool.QueryRow(ctx, webQ, p.From, p.To, p.EmployeeID, p.DepartmentID).Scan(&out.WebPages); err != nil {
		return out, fmt.Errorf("log insights web pages: %w", err)
	}
	statusQ := `
		SELECT
			COUNT(*) FILTER (WHERE s.status = 'ACTIVE' AND s.ended_at IS NULL),
			COUNT(*) FILTER (WHERE s.status = 'STALE')
		FROM app_sessions s
		WHERE s.deleted_at IS NULL
		  AND ($1::text = '' OR s.employee_id = $1)
		  AND ($2::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = s.employee_id AND e.deleted_at IS NULL AND e.department_id = $2
		  ))
	`
	if err := r.pool.QueryRow(ctx, statusQ, p.EmployeeID, p.DepartmentID).Scan(&out.OpenSessions, &out.StaleSessions); err != nil {
		return out, fmt.Errorf("log insights open/stale: %w", err)
	}
	return out, nil
}

// TopApps with optional employee filter (same shape as dashboard).
func (r *LogInsightsRepo) TopApps(ctx context.Context, p LogInsightsParams) ([]DashboardTopAppRow, error) {
	q := `
		SELECT
			COALESCE(NULLIF(s.app_display_name, ''), s.process_name) AS app_display_name,
			COALESCE(s.process_name, '') AS process_name,
			COUNT(*) AS session_count,
			COUNT(*) FILTER (WHERE s.status = 'ACTIVE' AND s.ended_at IS NULL) AS open_now
		FROM app_sessions s
		WHERE s.deleted_at IS NULL
		  AND s.started_at >= $1 AND s.started_at < $2
		  AND ($3::text = '' OR s.employee_id = $3)
		  AND ($4::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = s.employee_id AND e.deleted_at IS NULL AND e.department_id = $4
		  ))
		GROUP BY 1, 2
		ORDER BY session_count DESC, app_display_name ASC
		LIMIT $5
	`
	rows, err := r.pool.Query(ctx, q, p.From, p.To, p.EmployeeID, p.DepartmentID, p.TopN)
	if err != nil {
		return nil, fmt.Errorf("log insights top apps: %w", err)
	}
	defer rows.Close()
	out := make([]DashboardTopAppRow, 0, p.TopN)
	for rows.Next() {
		var row DashboardTopAppRow
		if err := rows.Scan(&row.AppDisplayName, &row.ProcessName, &row.SessionCount, &row.OpenNow); err != nil {
			return nil, fmt.Errorf("scan top app: %w", err)
		}
		out = append(out, row)
	}
	return out, rows.Err()
}

func (r *LogInsightsRepo) TopDomains(ctx context.Context, p LogInsightsParams) ([]DashboardTopDomainRow, error) {
	q := `
		SELECT i.domain, COUNT(*) AS visits
		FROM app_items i
		WHERE i.deleted_at IS NULL
		  AND i.item_type = 'browser_tab'
		  AND i.domain IS NOT NULL AND i.domain <> ''
		  AND i.opened_at >= $1 AND i.opened_at < $2
		  AND ($3::text = '' OR i.employee_id = $3)
		  AND ($4::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = i.employee_id AND e.deleted_at IS NULL AND e.department_id = $4
		  ))
		GROUP BY i.domain
		ORDER BY visits DESC, i.domain ASC
		LIMIT $5
	`
	rows, err := r.pool.Query(ctx, q, p.From, p.To, p.EmployeeID, p.DepartmentID, p.TopN)
	if err != nil {
		return nil, fmt.Errorf("log insights top domains: %w", err)
	}
	defer rows.Close()
	out := make([]DashboardTopDomainRow, 0, p.TopN)
	for rows.Next() {
		var row DashboardTopDomainRow
		if err := rows.Scan(&row.Domain, &row.Visits); err != nil {
			return nil, fmt.Errorf("scan top domain: %w", err)
		}
		out = append(out, row)
	}
	return out, rows.Err()
}
