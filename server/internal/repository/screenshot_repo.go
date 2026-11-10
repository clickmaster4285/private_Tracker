package repository

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type ScreenshotRepo struct {
	pool *pgxpool.Pool
}

func NewScreenshotRepo(pool *pgxpool.Pool) *ScreenshotRepo {
	return &ScreenshotRepo{pool: pool}
}

// InsertIdempotent inserts by client-supplied primary key id.
// On conflict (retry after a lost 200), loads the existing row and returns created=false.
func (r *ScreenshotRepo) InsertIdempotent(ctx context.Context, s *models.Screenshot) (created bool, err error) {
	const q = `
		INSERT INTO screenshots (
			id, employee_id, machine_id, captured_at, rel_path,
			byte_size, width, height, content_type, created_at
		) VALUES (
			$1, $2, $3, $4, $5,
			$6, $7, $8, $9, now()
		)
		ON CONFLICT (id) DO NOTHING
		RETURNING id, created_at`
	err = r.pool.QueryRow(ctx, q,
		s.ID, s.EmployeeID, s.MachineID, s.CapturedAt, s.RelPath,
		s.ByteSize, s.Width, s.Height, s.ContentType,
	).Scan(&s.ID, &s.CreatedAt)
	if err == nil {
		return true, nil
	}
	if err != pgx.ErrNoRows {
		return false, err
	}
	existing, getErr := r.GetByID(ctx, s.ID)
	if getErr != nil {
		return false, getErr
	}
	if existing == nil {
		return false, fmt.Errorf("screenshot conflict but row missing")
	}
	*s = *existing
	return false, nil
}

func (r *ScreenshotRepo) GetByID(ctx context.Context, id string) (*models.Screenshot, error) {
	const q = `
		SELECT id, employee_id, machine_id, captured_at, rel_path,
		       byte_size, width, height, content_type, created_at
		FROM screenshots WHERE id = $1`
	var s models.Screenshot
	err := r.pool.QueryRow(ctx, q, id).Scan(
		&s.ID, &s.EmployeeID, &s.MachineID, &s.CapturedAt, &s.RelPath,
		&s.ByteSize, &s.Width, &s.Height, &s.ContentType, &s.CreatedAt,
	)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &s, nil
}

// ScreenshotListFilter is the server-side list filter (date window + optional clock times).
type ScreenshotListFilter struct {
	EmployeeID string
	From       *time.Time
	To         *time.Time
	// TimeFrom / TimeTo are HH:mm (24h). Empty = not applied.
	// Compared against (captured_at AT TIME ZONE TimeZone)::time so multi-day
	// date windows still restrict to that clock range each day.
	TimeFrom string
	TimeTo   string
	// IANA zone for clock-time filter (e.g. Asia/Karachi). Empty → UTC.
	TimeZone string
	Cursor   *time.Time
	Limit    int
}

// List returns newest-first rows with optional employee + date + clock filters.
// Cursor is an RFC3339 captured_at of the last item (exclusive).
func (r *ScreenshotRepo) List(ctx context.Context, f ScreenshotListFilter) ([]models.ScreenshotListItem, error) {
	limit := f.Limit
	if limit < 1 {
		limit = 40
	}
	if limit > 100 {
		limit = 100
	}

	tz := strings.TrimSpace(f.TimeZone)
	if tz == "" {
		tz = "UTC"
	}

	var b strings.Builder
	args := make([]any, 0, 12)
	b.WriteString(`
		SELECT s.id, s.employee_id, COALESCE(e.name, ''), s.machine_id, s.captured_at,
		       s.byte_size, s.width, s.height, s.content_type
		FROM screenshots s
		LEFT JOIN employees e ON e.employee_id = s.employee_id AND e.deleted_at IS NULL
		WHERE 1=1`)

	if f.EmployeeID != "" {
		args = append(args, f.EmployeeID)
		fmt.Fprintf(&b, ` AND s.employee_id = $%d`, len(args))
	}
	if f.From != nil {
		args = append(args, *f.From)
		fmt.Fprintf(&b, ` AND s.captured_at >= $%d`, len(args))
	}
	if f.To != nil {
		args = append(args, *f.To)
		fmt.Fprintf(&b, ` AND s.captured_at <= $%d`, len(args))
	}
	if f.TimeFrom != "" || f.TimeTo != "" {
		args = append(args, tz)
		tzIdx := len(args)
		localTimeExpr := fmt.Sprintf(`((s.captured_at AT TIME ZONE $%d)::time)`, tzIdx)
		if f.TimeFrom != "" {
			args = append(args, f.TimeFrom)
			fmt.Fprintf(&b, ` AND %s >= $%d::time`, localTimeExpr, len(args))
		}
		if f.TimeTo != "" {
			args = append(args, f.TimeTo)
			fmt.Fprintf(&b, ` AND %s <= $%d::time`, localTimeExpr, len(args))
		}
	}
	if f.Cursor != nil {
		args = append(args, *f.Cursor)
		fmt.Fprintf(&b, ` AND s.captured_at < $%d`, len(args))
	}
	args = append(args, limit)
	fmt.Fprintf(&b, ` ORDER BY s.captured_at DESC LIMIT $%d`, len(args))

	rows, err := r.pool.Query(ctx, b.String(), args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	out := make([]models.ScreenshotListItem, 0, limit)
	for rows.Next() {
		var it models.ScreenshotListItem
		if err := rows.Scan(
			&it.ID, &it.EmployeeID, &it.EmployeeName, &it.MachineID, &it.CapturedAt,
			&it.ByteSize, &it.Width, &it.Height, &it.ContentType,
		); err != nil {
			return nil, err
		}
		out = append(out, it)
	}
	return out, rows.Err()
}
