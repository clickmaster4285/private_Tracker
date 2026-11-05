package repository

import (
	"context"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// DashboardRepo runs bounded SQL aggregates for GET /dashboard/summary.
type DashboardRepo struct {
	pool *pgxpool.Pool
}

// NewDashboardRepo constructs a DashboardRepo.
func NewDashboardRepo(pool *pgxpool.Pool) *DashboardRepo {
	return &DashboardRepo{pool: pool}
}

// DashboardSummaryParams are the SQL-side filters for the home aggregate.
type DashboardSummaryParams struct {
	From         time.Time
	To           time.Time
	DepartmentID *int
	TopN         int
	RecentLimit  int
}

// DashboardEmployeeCounts is total / tracked / untracked.
type DashboardEmployeeCounts struct {
	Total     int
	Tracked   int
	Untracked int
}

// DashboardActivityCounts holds range + live session status counts.
type DashboardActivityCounts struct {
	Sessions      int
	WebPages      int
	OpenSessions  int
	StaleSessions int
}

// DashboardMonitoringCounts are unclassified catalog totals.
type DashboardMonitoringCounts struct {
	UnclassifiedApps  int
	UnclassifiedSites int
}

// DashboardDeviceStats is fleet health from employee_devices.
type DashboardDeviceStats struct {
	Active   int
	Seen15m  int
	Seen24h  int
	Stale7d  int
	Versions []DashboardVersionCount
}

// DashboardVersionCount is one client_version histogram bucket.
type DashboardVersionCount struct {
	Version string
	Count   int
}

// DashboardTopAppRow is a light Top-N app aggregate (session counts only).
type DashboardTopAppRow struct {
	AppDisplayName string
	ProcessName    string
	SessionCount   int
	OpenNow        int
}

// DashboardTopDomainRow is a Top-N domain visit aggregate.
type DashboardTopDomainRow struct {
	Domain string
	Visits int
}

// DashboardRecentSessionRow is a recent session with projected employee name.
type DashboardRecentSessionRow struct {
	ID             string
	EmployeeID     string
	EmployeeName   string
	AppDisplayName string
	ProcessName    string
	Status         string
	StartedAt      time.Time
	EndedAt        *time.Time
	LastSyncAt     *time.Time
}

func (r *DashboardRepo) EmployeeCounts(ctx context.Context, departmentID *int) (DashboardEmployeeCounts, error) {
	q := `
		SELECT
			COUNT(*) FILTER (WHERE deleted_at IS NULL),
			COUNT(*) FILTER (WHERE deleted_at IS NULL AND tracking_status = 'tracked'),
			COUNT(*) FILTER (WHERE deleted_at IS NULL AND tracking_status IS DISTINCT FROM 'tracked')
		FROM employees
		WHERE ($1::int IS NULL OR department_id = $1)
	`
	var out DashboardEmployeeCounts
	err := r.pool.QueryRow(ctx, q, departmentID).Scan(&out.Total, &out.Tracked, &out.Untracked)
	if err != nil {
		return out, fmt.Errorf("dashboard employee counts: %w", err)
	}
	return out, nil
}

func (r *DashboardRepo) ActivityCounts(ctx context.Context, p DashboardSummaryParams) (DashboardActivityCounts, error) {
	var out DashboardActivityCounts

	sessionsQ := `
		SELECT COUNT(*)
		FROM app_sessions s
		WHERE s.deleted_at IS NULL
		  AND s.started_at >= $1 AND s.started_at < $2
		  AND ($3::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = s.employee_id AND e.deleted_at IS NULL AND e.department_id = $3
		  ))
	`
	if err := r.pool.QueryRow(ctx, sessionsQ, p.From, p.To, p.DepartmentID).Scan(&out.Sessions); err != nil {
		return out, fmt.Errorf("dashboard sessions count: %w", err)
	}

	webQ := `
		SELECT COUNT(*)
		FROM app_items i
		WHERE i.deleted_at IS NULL
		  AND i.item_type = 'browser_tab'
		  AND i.opened_at >= $1 AND i.opened_at < $2
		  AND ($3::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = i.employee_id AND e.deleted_at IS NULL AND e.department_id = $3
		  ))
	`
	if err := r.pool.QueryRow(ctx, webQ, p.From, p.To, p.DepartmentID).Scan(&out.WebPages); err != nil {
		return out, fmt.Errorf("dashboard web pages count: %w", err)
	}

	statusQ := `
		SELECT
			COUNT(*) FILTER (WHERE s.status = 'ACTIVE' AND s.ended_at IS NULL),
			COUNT(*) FILTER (WHERE s.status = 'STALE')
		FROM app_sessions s
		WHERE s.deleted_at IS NULL
		  AND ($1::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = s.employee_id AND e.deleted_at IS NULL AND e.department_id = $1
		  ))
	`
	if err := r.pool.QueryRow(ctx, statusQ, p.DepartmentID).Scan(&out.OpenSessions, &out.StaleSessions); err != nil {
		return out, fmt.Errorf("dashboard open/stale counts: %w", err)
	}
	return out, nil
}

func (r *DashboardRepo) MonitoringCounts(ctx context.Context) (DashboardMonitoringCounts, error) {
	var out DashboardMonitoringCounts
	q := `
		SELECT
			(SELECT COUNT(*) FROM installed_applications
			 WHERE deleted_at IS NULL AND (type_id IS NULL OR category_id IS NULL)),
			(SELECT COUNT(*) FROM monitoring_sites
			 WHERE deleted_at IS NULL AND (type_id IS NULL OR category_id IS NULL))
	`
	if err := r.pool.QueryRow(ctx, q).Scan(&out.UnclassifiedApps, &out.UnclassifiedSites); err != nil {
		return out, fmt.Errorf("dashboard monitoring counts: %w", err)
	}
	return out, nil
}

func (r *DashboardRepo) DeviceStats(ctx context.Context, departmentID *int) (DashboardDeviceStats, error) {
	var out DashboardDeviceStats
	// Always exclude devices whose employee has been deleted (orphan devices).
	// The department filter is applied inside the EXISTS when set.
	bucketsQ := `
		SELECT
			COUNT(*),
			COUNT(*) FILTER (WHERE d.last_seen_at >= NOW() - INTERVAL '15 minutes'),
			COUNT(*) FILTER (WHERE d.last_seen_at >= NOW() - INTERVAL '24 hours'),
			COUNT(*) FILTER (WHERE d.last_seen_at IS NULL OR d.last_seen_at < NOW() - INTERVAL '7 days')
		FROM employee_devices d
		WHERE d.revoked_at IS NULL
		  AND EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = d.employee_id AND e.deleted_at IS NULL
		        AND ($1::int IS NULL OR e.department_id = $1)
		  )
	`
	if err := r.pool.QueryRow(ctx, bucketsQ, departmentID).Scan(
		&out.Active, &out.Seen15m, &out.Seen24h, &out.Stale7d,
	); err != nil {
		return out, fmt.Errorf("dashboard device buckets: %w", err)
	}

	versionsQ := `
		SELECT COALESCE(NULLIF(TRIM(d.client_version), ''), '(empty)') AS version, COUNT(*) AS cnt
		FROM employee_devices d
		WHERE d.revoked_at IS NULL
		  AND EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = d.employee_id AND e.deleted_at IS NULL
		        AND ($1::int IS NULL OR e.department_id = $1)
		  )
		GROUP BY 1
		ORDER BY cnt DESC, version ASC
		LIMIT 10
	`
	rows, err := r.pool.Query(ctx, versionsQ, departmentID)
	if err != nil {
		return out, fmt.Errorf("dashboard device versions: %w", err)
	}
	defer rows.Close()
	for rows.Next() {
		var v DashboardVersionCount
		if err := rows.Scan(&v.Version, &v.Count); err != nil {
			return out, fmt.Errorf("scan device version: %w", err)
		}
		out.Versions = append(out.Versions, v)
	}
	if out.Versions == nil {
		out.Versions = []DashboardVersionCount{}
	}
	return out, nil
}

func (r *DashboardRepo) TopApps(ctx context.Context, p DashboardSummaryParams) ([]DashboardTopAppRow, error) {
	q := `
		SELECT
			COALESCE(NULLIF(s.app_display_name, ''), s.process_name) AS app_display_name,
			COALESCE(s.process_name, '') AS process_name,
			COUNT(*) AS session_count,
			COUNT(*) FILTER (WHERE s.status = 'ACTIVE' AND s.ended_at IS NULL) AS open_now
		FROM app_sessions s
		WHERE s.deleted_at IS NULL
		  AND s.started_at >= $1 AND s.started_at < $2
		  AND ($3::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = s.employee_id AND e.deleted_at IS NULL AND e.department_id = $3
		  ))
		GROUP BY 1, 2
		ORDER BY session_count DESC, app_display_name ASC
		LIMIT $4
	`
	rows, err := r.pool.Query(ctx, q, p.From, p.To, p.DepartmentID, p.TopN)
	if err != nil {
		return nil, fmt.Errorf("dashboard top apps: %w", err)
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
	return out, nil
}

func (r *DashboardRepo) TopDomains(ctx context.Context, p DashboardSummaryParams) ([]DashboardTopDomainRow, error) {
	q := `
		SELECT i.domain, COUNT(*) AS visits
		FROM app_items i
		WHERE i.deleted_at IS NULL
		  AND i.item_type = 'browser_tab'
		  AND i.domain IS NOT NULL AND i.domain <> ''
		  AND i.opened_at >= $1 AND i.opened_at < $2
		  AND ($3::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = i.employee_id AND e.deleted_at IS NULL AND e.department_id = $3
		  ))
		GROUP BY i.domain
		ORDER BY visits DESC, i.domain ASC
		LIMIT $4
	`
	rows, err := r.pool.Query(ctx, q, p.From, p.To, p.DepartmentID, p.TopN)
	if err != nil {
		return nil, fmt.Errorf("dashboard top domains: %w", err)
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
	return out, nil
}

func (r *DashboardRepo) RecentSessions(ctx context.Context, p DashboardSummaryParams) ([]DashboardRecentSessionRow, error) {
	q := `
		SELECT s.id, s.employee_id, COALESCE(e.name, '') AS employee_name,
		       COALESCE(s.app_display_name, '') AS app_display_name,
		       COALESCE(s.process_name, '') AS process_name,
		       COALESCE(s.status, 'ACTIVE') AS status,
		       s.started_at, s.ended_at, s.last_sync_at
		FROM app_sessions s
		LEFT JOIN employees e ON e.employee_id = s.employee_id AND e.deleted_at IS NULL
		WHERE s.deleted_at IS NULL
		  AND s.started_at >= $1 AND s.started_at < $2
		  AND ($3::int IS NULL OR e.department_id = $3)
		ORDER BY s.started_at DESC
		LIMIT $4
	`
	rows, err := r.pool.Query(ctx, q, p.From, p.To, p.DepartmentID, p.RecentLimit)
	if err != nil {
		return nil, fmt.Errorf("dashboard recent sessions: %w", err)
	}
	defer rows.Close()
	out := make([]DashboardRecentSessionRow, 0, p.RecentLimit)
	for rows.Next() {
		var row DashboardRecentSessionRow
		if err := rows.Scan(
			&row.ID, &row.EmployeeID, &row.EmployeeName,
			&row.AppDisplayName, &row.ProcessName, &row.Status,
			&row.StartedAt, &row.EndedAt, &row.LastSyncAt,
		); err != nil {
			return nil, fmt.Errorf("scan recent session: %w", err)
		}
		out = append(out, row)
	}
	return out, nil
}
