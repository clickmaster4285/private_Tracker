package repository

import (
	"context"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// LogGraphicalRepo runs time-series aggregates for GET /logs/graphical.
type LogGraphicalRepo struct {
	pool *pgxpool.Pool
}

// NewLogGraphicalRepo constructs a LogGraphicalRepo.
func NewLogGraphicalRepo(pool *pgxpool.Pool) *LogGraphicalRepo {
	return &LogGraphicalRepo{pool: pool}
}

// LogGraphicalParams mirrors insights filters; Trunc is "hour" or "day".
type LogGraphicalParams struct {
	From         time.Time
	To           time.Time
	DepartmentID *int
	EmployeeID   string
	Trunc        string // hour | day
	TopN         int
}

// LogGraphicalBucketRow is one raw SQL bucket (UTC trunc start).
type LogGraphicalBucketRow struct {
	At    time.Time
	Count int
}

// LogGraphicalProdBucketRow is classified duration for one bucket.
type LogGraphicalProdBucketRow struct {
	At                  time.Time
	ProductiveSeconds   int64
	UnproductiveSeconds int64
	NeutralSeconds      int64
}

func (r *LogGraphicalRepo) SessionSeries(ctx context.Context, p LogGraphicalParams) ([]LogGraphicalBucketRow, error) {
	q := fmt.Sprintf(`
		SELECT date_trunc('%s', s.started_at AT TIME ZONE 'UTC') AS bucket, COUNT(*)::int
		FROM app_sessions s
		WHERE s.deleted_at IS NULL
		  AND s.started_at >= $1 AND s.started_at < $2
		  AND ($3::text = '' OR s.employee_id = $3)
		  AND ($4::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = s.employee_id AND e.deleted_at IS NULL AND e.department_id = $4
		  ))
		GROUP BY 1
		ORDER BY 1
	`, p.Trunc)
	return r.scanBucketCounts(ctx, q, p)
}

func (r *LogGraphicalRepo) WebSeries(ctx context.Context, p LogGraphicalParams) ([]LogGraphicalBucketRow, error) {
	q := fmt.Sprintf(`
		SELECT date_trunc('%s', i.opened_at AT TIME ZONE 'UTC') AS bucket, COUNT(*)::int
		FROM app_items i
		WHERE i.deleted_at IS NULL
		  AND i.item_type = 'browser_tab'
		  AND i.opened_at >= $1 AND i.opened_at < $2
		  AND ($3::text = '' OR i.employee_id = $3)
		  AND ($4::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = i.employee_id AND e.deleted_at IS NULL AND e.department_id = $4
		  ))
		GROUP BY 1
		ORDER BY 1
	`, p.Trunc)
	return r.scanBucketCounts(ctx, q, p)
}

func (r *LogGraphicalRepo) IdleSeries(ctx context.Context, p LogGraphicalParams) ([]LogGraphicalBucketRow, error) {
	q := fmt.Sprintf(`
		SELECT date_trunc('%s', se.event_at AT TIME ZONE 'UTC') AS bucket, COUNT(*)::int
		FROM session_events se
		WHERE se.deleted_at IS NULL
		  AND se.event_type = 'idle_start'
		  AND se.event_at >= $1 AND se.event_at < $2
		  AND ($3::text = '' OR se.employee_id = $3)
		  AND ($4::int IS NULL OR EXISTS (
		      SELECT 1 FROM employees e
		      WHERE e.employee_id = se.employee_id AND e.deleted_at IS NULL AND e.department_id = $4
		  ))
		GROUP BY 1
		ORDER BY 1
	`, p.Trunc)
	return r.scanBucketCounts(ctx, q, p)
}

func (r *LogGraphicalRepo) scanBucketCounts(ctx context.Context, q string, p LogGraphicalParams) ([]LogGraphicalBucketRow, error) {
	rows, err := r.pool.Query(ctx, q, p.From, p.To, p.EmployeeID, p.DepartmentID)
	if err != nil {
		return nil, fmt.Errorf("log graphical series: %w", err)
	}
	defer rows.Close()
	out := make([]LogGraphicalBucketRow, 0)
	for rows.Next() {
		var row LogGraphicalBucketRow
		if err := rows.Scan(&row.At, &row.Count); err != nil {
			return nil, fmt.Errorf("scan graphical bucket: %w", err)
		}
		out = append(out, row)
	}
	return out, rows.Err()
}

// ProductivitySeries groups classified session duration by time bucket.
// Sessions must START in range (same rule as /app-sessions/usage). Each
// session's interval is split across the hour/day buckets it overlaps; within
// a bucket, concurrent apps are scaled so productive+unproductive+neutral
// never exceeds the bucket length (matches hours-insights chart capping).
func (r *LogGraphicalRepo) ProductivitySeries(ctx context.Context, p LogGraphicalParams) ([]LogGraphicalProdBucketRow, error) {
	bucketInterval := "1 day"
	if p.Trunc == "hour" {
		bucketInterval = "1 hour"
	}
	q := fmt.Sprintf(`
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
		buckets AS (
			SELECT generate_series(
				date_trunc('%s', $1::timestamptz AT TIME ZONE 'UTC') AT TIME ZONE 'UTC',
				date_trunc('%s', ($2::timestamptz - INTERVAL '1 microsecond') AT TIME ZONE 'UTC') AT TIME ZONE 'UTC',
				'%s'::interval
			) AS bucket_start
		),
		raw_buckets AS (
			SELECT
				b.bucket_start AS bucket,
				COALESCE(SUM(CASE WHEN ac.type_name = 'Productive'
					THEN GREATEST(0, EXTRACT(EPOCH FROM (
						LEAST(v.eff_end, b.bucket_start + '%s'::interval) -
						GREATEST(v.eff_start, b.bucket_start)
					))) ELSE 0 END), 0) AS productive,
				COALESCE(SUM(CASE WHEN ac.type_name = 'Unproductive'
					THEN GREATEST(0, EXTRACT(EPOCH FROM (
						LEAST(v.eff_end, b.bucket_start + '%s'::interval) -
						GREATEST(v.eff_start, b.bucket_start)
					))) ELSE 0 END), 0) AS unproductive,
				COALESCE(SUM(CASE WHEN ac.type_name IS NULL OR ac.type_name NOT IN ('Productive', 'Unproductive')
					THEN GREATEST(0, EXTRACT(EPOCH FROM (
						LEAST(v.eff_end, b.bucket_start + '%s'::interval) -
						GREATEST(v.eff_start, b.bucket_start)
					))) ELSE 0 END), 0) AS neutral
			FROM buckets b
			JOIN valid v ON v.eff_start < b.bucket_start + '%s'::interval AND v.eff_end > b.bucket_start
			LEFT JOIN app_cat ac ON ac.app_key = lower(v.app_display_name)
			GROUP BY b.bucket_start
		)
		SELECT
			bucket,
			(productive * LEAST(1.0, EXTRACT(EPOCH FROM '%s'::interval) / NULLIF(productive + unproductive + neutral, 0)))::bigint,
			(unproductive * LEAST(1.0, EXTRACT(EPOCH FROM '%s'::interval) / NULLIF(productive + unproductive + neutral, 0)))::bigint,
			(neutral * LEAST(1.0, EXTRACT(EPOCH FROM '%s'::interval) / NULLIF(productive + unproductive + neutral, 0)))::bigint
		FROM raw_buckets
		WHERE productive + unproductive + neutral > 0
		ORDER BY bucket
	`, p.Trunc, p.Trunc, bucketInterval, bucketInterval, bucketInterval, bucketInterval, bucketInterval, bucketInterval, bucketInterval, bucketInterval)

	rows, err := r.pool.Query(ctx, q, p.From, p.To, p.EmployeeID, p.DepartmentID)
	if err != nil {
		return nil, fmt.Errorf("log graphical productivity series: %w", err)
	}
	defer rows.Close()
	out := make([]LogGraphicalProdBucketRow, 0)
	for rows.Next() {
		var row LogGraphicalProdBucketRow
		if err := rows.Scan(&row.At, &row.ProductiveSeconds, &row.UnproductiveSeconds, &row.NeutralSeconds); err != nil {
			return nil, fmt.Errorf("scan prod bucket: %w", err)
		}
		out = append(out, row)
	}
	return out, rows.Err()
}

// TopApps / TopDomains reuse the same shape as insights (copied filter SQL).
func (r *LogGraphicalRepo) TopApps(ctx context.Context, p LogGraphicalParams) ([]DashboardTopAppRow, error) {
	insights := &LogInsightsRepo{pool: r.pool}
	return insights.TopApps(ctx, LogInsightsParams{
		From: p.From, To: p.To, DepartmentID: p.DepartmentID,
		EmployeeID: p.EmployeeID, TopN: p.TopN,
	})
}

func (r *LogGraphicalRepo) TopDomains(ctx context.Context, p LogGraphicalParams) ([]DashboardTopDomainRow, error) {
	insights := &LogInsightsRepo{pool: r.pool}
	return insights.TopDomains(ctx, LogInsightsParams{
		From: p.From, To: p.To, DepartmentID: p.DepartmentID,
		EmployeeID: p.EmployeeID, TopN: p.TopN,
	})
}
