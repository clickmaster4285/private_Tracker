package repository

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/alpha-ai-tracker/server/internal/models"
)

// EmployeeRepo handles database operations for employees.
type EmployeeRepo struct {
	pool *pgxpool.Pool
}

// NewEmployeeRepo creates a new EmployeeRepo.
func NewEmployeeRepo(pool *pgxpool.Pool) *EmployeeRepo {
	return &EmployeeRepo{pool: pool}
}

// ListParams defines query parameters for listing employees.
type EmployeeListParams struct {
	Search     string
	Department string
	Status     string // "tracked" or "untracked"
	Page       int
	PerPage    int
}

// EmployeeListResult holds paginated results.
type EmployeeListResult struct {
	Employees  []models.Employee
	Total      int
	Page       int
	PerPage    int
	TotalPages int
}

// List returns a paginated, filtered list of employees.
func (r *EmployeeRepo) List(ctx context.Context, params EmployeeListParams) (*EmployeeListResult, error) {
	if params.Page < 1 {
		params.Page = 1
	}
	if params.PerPage < 1 || params.PerPage > 100 {
		params.PerPage = 10
	}

	var conditions []string
	var args []interface{}
	argIdx := 1

	conditions = append(conditions, "e.deleted_at IS NULL")

	if params.Search != "" {
		conditions = append(conditions, fmt.Sprintf("(LOWER(e.name) LIKE LOWER($%d) OR LOWER(e.email) LIKE LOWER($%d) OR LOWER(e.employee_id) LIKE LOWER($%d))", argIdx, argIdx, argIdx))
		args = append(args, "%"+params.Search+"%")
		argIdx++
	}
	if params.Department != "" {
		conditions = append(conditions, fmt.Sprintf("d.name = $%d", argIdx))
		args = append(args, params.Department)
		argIdx++
	}
	if params.Status == "tracked" {
		conditions = append(conditions, "tracking_status = 'tracked'")
	} else if params.Status == "untracked" {
		conditions = append(conditions, "tracking_status = 'untracked'")
	}

	whereClause := ""
	if len(conditions) > 0 {
		whereClause = "WHERE " + strings.Join(conditions, " AND ")
	}

	// Count total
	countQuery := fmt.Sprintf("SELECT COUNT(*) FROM employees e LEFT JOIN departments d ON e.department_id = d.id %s", whereClause)
	var total int
	if err := r.pool.QueryRow(ctx, countQuery, args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count employees: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	// Fetch page — use manual scan to avoid RowToStructByName issues with nullable columns
	query := fmt.Sprintf(`
		SELECT e.id, e.employee_id, e.name, e.email,
		       COALESCE(d.name, '') AS department,
		       e.department_id, e.shift_id, COALESCE(s.name, '') AS shift,
		       e.tracking_enabled, e.tracking_status, e.is_online,
		           COALESCE(e.avatar, '') AS avatar, COALESCE(e.avatar_color, '') AS avatar_color,
		       e.created_at, e.updated_at, e.deleted_at,
		       COALESCE((SELECT ed.client_version FROM employee_devices ed
		                 WHERE ed.employee_id = e.employee_id AND ed.revoked_at IS NULL
		                 ORDER BY ed.last_seen_at DESC LIMIT 1), '') AS client_version,
		       EXISTS(SELECT 1 FROM users u
		              WHERE u.employee_id = e.employee_id
		                AND u.deleted_at IS NULL) AS has_user_login
		FROM employees e
		LEFT JOIN departments d ON e.department_id = d.id
		LEFT JOIN shifts s ON s.id = e.shift_id AND s.deleted_at IS NULL
		%s
		ORDER BY e.created_at DESC, e.id DESC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list employees: %w", err)
	}
	defer rows.Close()

	var employees []models.Employee
	for rows.Next() {
		var e models.Employee
		if err := rows.Scan(
			&e.ID, &e.EmployeeID, &e.Name, &e.Email,
			&e.Department, &e.DepartmentID, &e.ShiftID, &e.Shift,
			&e.TrackingEnabled, &e.TrackingStatus, &e.IsOnline,
			&e.Avatar, &e.AvatarColor,
			&e.CreatedAt, &e.UpdatedAt, &e.DeletedAt,
			&e.ClientVersion,
			&e.HasUserLogin,
		); err != nil {
			return nil, fmt.Errorf("scan employee row: %w", err)
		}
		employees = append(employees, e)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate employees: %w", err)
	}

	return &EmployeeListResult{
		Employees:  employees,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}

// getByID is an internal helper to fetch a single employee row.
func (r *EmployeeRepo) getByID(ctx context.Context, query string, args ...interface{}) (*models.Employee, error) {
	return execGetByID(ctx, r.pool, query, args...)
}

// GetByID returns an employee by UUID.
func (r *EmployeeRepo) GetByID(ctx context.Context, id string) (*models.Employee, error) {
	return r.getByID(ctx, `
		SELECT e.id, e.employee_id, e.name, e.email,
		       COALESCE(d.name, '') AS department,
		       e.department_id, e.shift_id, COALESCE(s.name, '') AS shift,
		       e.tracking_enabled, e.tracking_status, e.is_online,
		           COALESCE(e.avatar, '') AS avatar, COALESCE(e.avatar_color, '') AS avatar_color,
		       e.created_at, e.updated_at, e.deleted_at,
		       COALESCE((SELECT ed.client_version FROM employee_devices ed
		                 WHERE ed.employee_id = e.employee_id AND ed.revoked_at IS NULL
		                 ORDER BY ed.last_seen_at DESC LIMIT 1), '') AS client_version,
		       EXISTS(SELECT 1 FROM users u
		              WHERE u.employee_id = e.employee_id
		                AND u.deleted_at IS NULL) AS has_user_login
		FROM employees e
		LEFT JOIN departments d ON e.department_id = d.id
		LEFT JOIN shifts s ON s.id = e.shift_id AND s.deleted_at IS NULL
		WHERE e.id = $1 AND e.deleted_at IS NULL
	`, id)
}

// GetByEmployeeID returns an employee by their employee ID (EMP-XXXXX).
func (r *EmployeeRepo) GetByEmployeeID(ctx context.Context, employeeID string) (*models.Employee, error) {
	return r.getByID(ctx, `
		SELECT e.id, e.employee_id, e.name, e.email,
		       COALESCE(d.name, '') AS department,
		       e.department_id, e.shift_id, COALESCE(s.name, '') AS shift,
		       e.tracking_enabled, e.tracking_status, e.is_online,
		           COALESCE(e.avatar, '') AS avatar, COALESCE(e.avatar_color, '') AS avatar_color,
		       e.created_at, e.updated_at, e.deleted_at,
		       COALESCE((SELECT ed.client_version FROM employee_devices ed
		                 WHERE ed.employee_id = e.employee_id AND ed.revoked_at IS NULL
		                 ORDER BY ed.last_seen_at DESC LIMIT 1), '') AS client_version,
		       EXISTS(SELECT 1 FROM users u
		              WHERE u.employee_id = e.employee_id
		                AND u.deleted_at IS NULL) AS has_user_login
		FROM employees e
		LEFT JOIN departments d ON e.department_id = d.id
		LEFT JOIN shifts s ON s.id = e.shift_id AND s.deleted_at IS NULL
		WHERE e.employee_id = $1 AND e.deleted_at IS NULL
	`, employeeID)
}

// GetByEmail returns an employee by email.
func (r *EmployeeRepo) GetByEmail(ctx context.Context, email string) (*models.Employee, error) {
	return r.getByID(ctx, `
		SELECT e.id, e.employee_id, e.name, e.email,
		       COALESCE(d.name, '') AS department,
		       e.department_id, e.shift_id, COALESCE(s.name, '') AS shift,
		       e.tracking_enabled, e.tracking_status, e.is_online,
		           COALESCE(e.avatar, '') AS avatar, COALESCE(e.avatar_color, '') AS avatar_color,
		       e.created_at, e.updated_at, e.deleted_at,
		       COALESCE((SELECT ed.client_version FROM employee_devices ed
		                 WHERE ed.employee_id = e.employee_id AND ed.revoked_at IS NULL
		                 ORDER BY ed.last_seen_at DESC LIMIT 1), '') AS client_version,
		       EXISTS(SELECT 1 FROM users u
		              WHERE u.employee_id = e.employee_id
		                AND u.deleted_at IS NULL) AS has_user_login
		FROM employees e
		LEFT JOIN departments d ON e.department_id = d.id
		LEFT JOIN shifts s ON s.id = e.shift_id AND s.deleted_at IS NULL
		WHERE e.email = $1 AND e.deleted_at IS NULL
	`, email)
}

// Create inserts a new employee and returns it.
// Uses a transaction for atomicity.
func (r *EmployeeRepo) Create(ctx context.Context, e *models.Employee) (*models.Employee, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	query := `
		INSERT INTO employees (employee_id, name, email, department_id, shift_id,
		                       tracking_enabled, tracking_status, is_online)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
		RETURNING id, employee_id, name, email,
		          COALESCE((SELECT name FROM departments WHERE id = $4), '') AS department,
		          $4 AS department_id, shift_id, COALESCE((SELECT name FROM shifts WHERE id = employees.shift_id AND deleted_at IS NULL), '') AS shift,
		          tracking_enabled, tracking_status, is_online,
		          COALESCE(avatar, '') AS avatar, COALESCE(avatar_color, '') AS avatar_color,
		          created_at, updated_at, deleted_at,
		          COALESCE((SELECT ed.client_version FROM employee_devices ed
		                    WHERE ed.employee_id = employees.employee_id AND ed.revoked_at IS NULL
		                    ORDER BY ed.last_seen_at DESC LIMIT 1), '') AS client_version,
		          EXISTS(SELECT 1 FROM users u
		                 WHERE u.employee_id = employees.employee_id
		                   AND u.deleted_at IS NULL) AS has_user_login
	`
	emp, err := execGetByID(ctx, tx, query,
		e.EmployeeID, e.Name, e.Email, e.DepartmentID, e.ShiftID,
		e.TrackingEnabled, e.TrackingStatus, e.IsOnline,
	)
	if err != nil {
		// Check for duplicate key violations
		if isDuplicateKeyError(err) {
			return nil, fmt.Errorf("duplicate employee record: %w", err)
		}
		return nil, err
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit tx: %w", err)
	}
	return emp, nil
}

// ListAll returns every non-deleted employee ordered by name (Excel export).
func (r *EmployeeRepo) ListAll(ctx context.Context) ([]models.Employee, error) {
	query := `
		SELECT e.id, e.employee_id, e.name, e.email,
		       COALESCE(d.name, '') AS department,
		       e.department_id, e.shift_id, COALESCE(s.name, '') AS shift,
		       e.tracking_enabled, e.tracking_status, e.is_online,
		           COALESCE(e.avatar, '') AS avatar, COALESCE(e.avatar_color, '') AS avatar_color,
		       e.created_at, e.updated_at, e.deleted_at,
		       COALESCE((SELECT ed.client_version FROM employee_devices ed
		                 WHERE ed.employee_id = e.employee_id AND ed.revoked_at IS NULL
		                 ORDER BY ed.last_seen_at DESC LIMIT 1), '') AS client_version,
		       EXISTS(SELECT 1 FROM users u
		              WHERE u.employee_id = e.employee_id
		                AND u.deleted_at IS NULL) AS has_user_login
		FROM employees e
		LEFT JOIN departments d ON e.department_id = d.id
		LEFT JOIN shifts s ON s.id = e.shift_id AND s.deleted_at IS NULL
		WHERE e.deleted_at IS NULL
		ORDER BY e.name ASC
	`
	rows, err := r.pool.Query(ctx, query)
	if err != nil {
		return nil, fmt.Errorf("list all employees: %w", err)
	}
	defer rows.Close()

	var employees []models.Employee
	for rows.Next() {
		var e models.Employee
		if err := rows.Scan(
			&e.ID, &e.EmployeeID, &e.Name, &e.Email,
			&e.Department, &e.DepartmentID, &e.ShiftID, &e.Shift,
			&e.TrackingEnabled, &e.TrackingStatus, &e.IsOnline,
			&e.Avatar, &e.AvatarColor,
			&e.CreatedAt, &e.UpdatedAt, &e.DeletedAt,
			&e.ClientVersion,
			&e.HasUserLogin,
		); err != nil {
			return nil, fmt.Errorf("scan employee row: %w", err)
		}
		employees = append(employees, e)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate employees: %w", err)
	}
	return employees, nil
}

// ImportEmployeeItem is a single employee row to import (upsert by employee_id).
// `Shift` is the human-readable name from the spreadsheet; the importer
// resolves it to shift_id via the shifts catalog (best-effort; empty or
// unknown → NULL — no hardcoded default).
type ImportEmployeeItem struct {
	EmployeeID string
	Name       string
	Email      string
	Department string
	Shift      string
}

// ImportOutcome reports how a single import row was handled.
type ImportOutcome struct {
	Status string // imported | updated | skipped
	Reason string
}

// Import upserts employees in ONE transaction: departments are get-or-created
// (a missing name is created first, then attached via department_id), shifts
// are resolved by name (a missing name keeps the default), and every row is
// upserted by its exact employee_id. Soft-deleted employees/departments are
// revived so an Excel re-import is idempotent.
func (r *EmployeeRepo) Import(ctx context.Context, items []ImportEmployeeItem) ([]ImportOutcome, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	// Resolve every referenced department once (get-or-create, revive if soft-deleted).
	deptIDs := make(map[string]int)
	for _, it := range items {
		name := strings.TrimSpace(it.Department)
		if name == "" {
			name = "Engineering"
		}
		if _, ok := deptIDs[name]; ok {
			continue
		}
		id, err := getOrCreateDepartment(ctx, tx, name)
		if err != nil {
			return nil, err
		}
		deptIDs[name] = id
	}

	// Resolve every referenced shift once (by name, case-insensitive).
	// An empty cell or a name that doesn't match any non-deleted shift row
	// leaves the assignment NULL. We deliberately do NOT fall back to any
	// hardcoded shift name — the shifts catalog is the single source of
	// truth (No-Hardcoded-Names Rule, AGENTS.md §6).
	shiftIDs := make(map[string]int)
	for _, it := range items {
		name := strings.TrimSpace(it.Shift)
		if name == "" {
			// Empty cell → no assignment. Skip the catalog lookup.
			continue
		}
		if _, ok := shiftIDs[name]; ok {
			continue
		}
		var id int
		err := tx.QueryRow(ctx,
			"SELECT id FROM shifts WHERE LOWER(name) = LOWER($1) AND deleted_at IS NULL", name,
		).Scan(&id)
		if err != nil {
			if errors.Is(err, pgx.ErrNoRows) {
				// Unknown shift name → leave the assignment NULL.
				shiftIDs[name] = 0
				continue
			}
			return nil, fmt.Errorf("resolve shift %q: %w", name, err)
		}
		shiftIDs[name] = id
	}

	outcomes := make([]ImportOutcome, len(items))
	for i, it := range items {
		deptName := strings.TrimSpace(it.Department)
		if deptName == "" {
			deptName = "Engineering"
		}

		// Email may only be reused by the same employee_id.
		if it.Email != "" {
			var existingID string
			err := tx.QueryRow(ctx,
				"SELECT employee_id FROM employees WHERE email = $1 AND deleted_at IS NULL", it.Email,
			).Scan(&existingID)
			if err == nil && existingID != it.EmployeeID {
				outcomes[i] = ImportOutcome{Status: "skipped", Reason: "email already in use by " + existingID}
				continue
			} else if err != nil && !errors.Is(err, pgx.ErrNoRows) {
				return nil, fmt.Errorf("check email: %w", err)
			}
		}

		// An empty shift cell or an unknown shift name → no assignment
		// (NULL). We never substitute a hardcoded fallback here — the
		// shifts catalog is the single source of truth.
		shiftName := strings.TrimSpace(it.Shift)
		var shiftID interface{}
		if shiftName != "" {
			if id, ok := shiftIDs[shiftName]; ok && id > 0 {
				shiftID = id
			}
			// else: unknown name (already recorded with id=0 in the map)
			// → shiftID stays nil.
		}

		// xmax=0 → freshly inserted; xmax≠0 → updated by ON CONFLICT.
		var inserted bool
		err = tx.QueryRow(ctx, `
			INSERT INTO employees (employee_id, name, email, department_id, shift_id,
			                       tracking_enabled, tracking_status, is_online)
			VALUES ($1, $2, $3, $4, $5, true, 'untracked', false)
			ON CONFLICT (employee_id) DO UPDATE SET
				name = EXCLUDED.name,
				email = EXCLUDED.email,
				department_id = EXCLUDED.department_id,
				shift_id = EXCLUDED.shift_id,
				deleted_at = NULL,
				updated_at = NOW()
			RETURNING (xmax = 0) AS inserted
		`, it.EmployeeID, it.Name, it.Email, deptIDs[deptName], shiftID).Scan(&inserted)
		if err != nil {
			if isDuplicateKeyError(err) {
				outcomes[i] = ImportOutcome{Status: "skipped", Reason: "duplicate record"}
				continue
			}
			return nil, fmt.Errorf("import employee %s: %w", it.EmployeeID, err)
		}

		if inserted {
			outcomes[i] = ImportOutcome{Status: "imported"}
		} else {
			outcomes[i] = ImportOutcome{Status: "updated"}
		}
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit import tx: %w", err)
	}
	return outcomes, nil
}

// getOrCreateDepartment returns the id of the named department, creating it if
// missing (or reviving it if it was soft-deleted). Runs inside a caller tx.
func getOrCreateDepartment(ctx context.Context, tx pgx.Tx, name string) (int, error) {
	var id int
	var deletedAt *time.Time
	err := tx.QueryRow(ctx, "SELECT id, deleted_at FROM departments WHERE name = $1", name).Scan(&id, &deletedAt)
	if err == nil {
		if deletedAt != nil {
			if _, err := tx.Exec(ctx, "UPDATE departments SET deleted_at = NULL WHERE id = $1", id); err != nil {
				return 0, fmt.Errorf("revive department: %w", err)
			}
		}
		return id, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return 0, fmt.Errorf("find department: %w", err)
	}

	err = tx.QueryRow(ctx,
		"INSERT INTO departments (name) VALUES ($1) ON CONFLICT (name) DO NOTHING RETURNING id", name,
	).Scan(&id)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) { // race: inserted between SELECT and INSERT
			if err := tx.QueryRow(ctx, "SELECT id FROM departments WHERE name = $1", name).Scan(&id); err != nil {
				return 0, fmt.Errorf("re-select department: %w", err)
			}
			return id, nil
		}
		return 0, fmt.Errorf("create department: %w", err)
	}
	return id, nil
}

// Update partially updates an employee and returns the updated record.
// Uses a transaction for atomicity.
func (r *EmployeeRepo) Update(ctx context.Context, id string, updates map[string]interface{}) (*models.Employee, error) {
	if len(updates) == 0 {
		return r.GetByID(ctx, id)
	}

	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	var setClauses []string
	var args []interface{}
	argIdx := 1
	args = append(args, id)

	allowedFields := map[string]string{
		"name": "name", "email": "email",
		"department_id": "department_id",
		"shift_id": "shift_id",
		"tracking_enabled": "tracking_enabled",
		"tracking_status": "tracking_status", "is_online": "is_online",
	}

	for field, dbCol := range allowedFields {
		if val, ok := updates[field]; ok {
			setClauses = append(setClauses, fmt.Sprintf("%s = $%d", dbCol, argIdx+1))
			args = append(args, val)
			argIdx++
		}
	}

	if len(setClauses) == 0 {
		return r.GetByID(ctx, id)
	}

	setClauses = append(setClauses, "updated_at = NOW()")

	query := fmt.Sprintf(`
		UPDATE employees SET %s
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING id, employee_id, name, email,
		          COALESCE((SELECT name FROM departments WHERE id = employees.department_id), '') AS department,
		          department_id, shift_id, COALESCE((SELECT name FROM shifts WHERE id = employees.shift_id AND deleted_at IS NULL), '') AS shift,
		          tracking_enabled, tracking_status, is_online,
		          COALESCE(avatar, '') AS avatar, COALESCE(avatar_color, '') AS avatar_color,
		          created_at, updated_at, deleted_at,
		          COALESCE((SELECT ed.client_version FROM employee_devices ed
		                    WHERE ed.employee_id = employees.employee_id AND ed.revoked_at IS NULL
		                    ORDER BY ed.last_seen_at DESC LIMIT 1), '') AS client_version,
		          EXISTS(SELECT 1 FROM users u
		                 WHERE u.employee_id = employees.employee_id
		                   AND u.deleted_at IS NULL) AS has_user_login
	`, strings.Join(setClauses, ", "))

	emp, err := execGetByID(ctx, tx, query, args...)
	if err != nil {
		return nil, err
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit tx: %w", err)
	}
	return emp, nil
}

// Delete removes an employee by ID.
// Uses a transaction for atomicity.
func (r *EmployeeRepo) Delete(ctx context.Context, id string) error {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	tag, err := tx.Exec(ctx, "UPDATE employees SET deleted_at = NOW() WHERE id = $1 AND deleted_at IS NULL", id)
	if err != nil {
		return fmt.Errorf("delete employee: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("employee not found")
	}

	if err := tx.Commit(ctx); err != nil {
		return fmt.Errorf("commit tx: %w", err)
	}
	return nil
}

// GetDepartments returns all department names.
func (r *EmployeeRepo) GetDepartments(ctx context.Context) ([]string, error) {
	rows, err := r.pool.Query(ctx, "SELECT name FROM departments WHERE deleted_at IS NULL ORDER BY name")
	if err != nil {
		return nil, fmt.Errorf("get departments: %w", err)
	}
	defer rows.Close()

	var depts []string
	for rows.Next() {
		var d string
		if err := rows.Scan(&d); err != nil {
			return nil, fmt.Errorf("scan department: %w", err)
		}
		depts = append(depts, d)
	}
	return depts, nil
}

// ────────────────────────────────
// Transaction-aware helpers
// ────────────────────────────────

// isDuplicateKeyError checks if an error is a PostgreSQL unique constraint violation.
func isDuplicateKeyError(err error) bool {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) {
		return pgErr.Code == "23505"
	}
	return false
}

// scanEmployeeRow scans a single employee row using manual field scan.
// Uses manual scanning to avoid RowToStructByName issues with nullable columns.
// Note: rows are closed by CollectOneRow internally.
func scanEmployeeRow(rows pgx.Rows) (*models.Employee, error) {
	emp, err := pgx.CollectOneRow(rows, func(row pgx.CollectableRow) (models.Employee, error) {
		var e models.Employee
		err := row.Scan(
			&e.ID, &e.EmployeeID, &e.Name, &e.Email,
			&e.Department, &e.DepartmentID, &e.ShiftID, &e.Shift,
			&e.TrackingEnabled, &e.TrackingStatus, &e.IsOnline,
			&e.Avatar, &e.AvatarColor,
			&e.CreatedAt, &e.UpdatedAt, &e.DeletedAt,
			&e.ClientVersion,
			&e.HasUserLogin,
		)
		return e, err
	})
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, fmt.Errorf("scan employee: %w", err)
	}
	return &emp, nil
}

// execGetByID executes a query against a transaction and scans a single employee row.
type queryable interface {
	Query(ctx context.Context, sql string, args ...interface{}) (pgx.Rows, error)
}

func execGetByID(ctx context.Context, q queryable, query string, args ...interface{}) (*models.Employee, error) {
	rows, err := q.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("query employee: %w", err)
	}
	return scanEmployeeRow(rows)
}

// GenerateEmployeeID generates the next employee ID in EMP-XXXXX format.
func (r *EmployeeRepo) GenerateEmployeeID(ctx context.Context) (string, error) {
	var id string
	err := r.pool.QueryRow(ctx, "SELECT 'EMP-' || LPAD(NEXTVAL('employee_id_seq')::TEXT, 5, '0')").Scan(&id)
	if err != nil {
		return "", fmt.Errorf("generate employee id: %w", err)
	}
	return id, nil
}
