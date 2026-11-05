package repository

import (
	"context"
	"errors"
	"fmt"
	"strings"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/alpha-ai-tracker/server/internal/models"
)

// UserRepo handles database operations for users.
type UserRepo struct {
	pool *pgxpool.Pool
}

// NewUserRepo creates a new UserRepo.
func NewUserRepo(pool *pgxpool.Pool) *UserRepo {
	return &UserRepo{pool: pool}
}

// ListParams defines query parameters for listing users.
type ListParams struct {
	Search string
	RoleID int
	// Status filters tracking_status: "tracked" or "untracked".
	Status string
	// AccountStatus filters the ACCOUNT status: "active" or "suspended".
	// Deliberately separate from Status — a suspended user keeps tracking_status.
	AccountStatus string
	Page          int
	PerPage       int
}

// ListResult holds paginated results.
type ListResult struct {
	Users      []models.User
	Total      int
	Page       int
	PerPage    int
	TotalPages int
}

// List returns a paginated, filtered list of users.
func (r *UserRepo) List(ctx context.Context, params ListParams) (*ListResult, error) {
	if params.Page < 1 {
		params.Page = 1
	}
	if params.PerPage < 1 || params.PerPage > 100 {
		params.PerPage = 10
	}

	var conditions []string
	var args []interface{}
	argIdx := 1

	// Qualified with u. — roles also has deleted_at, so a bare reference is
	// ambiguous once the roles JOIN is applied.
	conditions = append(conditions, "u.deleted_at IS NULL")

	if params.Search != "" {
		conditions = append(conditions, fmt.Sprintf("(LOWER(u.name) LIKE LOWER($%d) OR LOWER(u.email) LIKE LOWER($%d) OR LOWER(u.employee_id) LIKE LOWER($%d))", argIdx, argIdx, argIdx))
		args = append(args, "%"+params.Search+"%")
		argIdx++
	}
	if params.RoleID > 0 {
		conditions = append(conditions, fmt.Sprintf("u.role_id = $%d", argIdx))
		args = append(args, params.RoleID)
		argIdx++
	}
	if params.Status == "tracked" {
		conditions = append(conditions, "u.tracking_status = 'tracked'")
	} else if params.Status == "untracked" {
		conditions = append(conditions, "u.tracking_status = 'untracked'")
	}
	if params.AccountStatus == "active" || params.AccountStatus == "suspended" {
		conditions = append(conditions, fmt.Sprintf("u.status = $%d", argIdx))
		args = append(args, params.AccountStatus)
		argIdx++
	}

	whereClause := ""
	if len(conditions) > 0 {
		whereClause = "WHERE " + strings.Join(conditions, " AND ")
	}

	// Count total
	countQuery := fmt.Sprintf("SELECT COUNT(*) FROM users u %s", whereClause)
	var total int
	if err := r.pool.QueryRow(ctx, countQuery, args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count users: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	// Fetch page (role name resolved via JOIN — never stored on the row).
	// avatar/avatar_color are NULLable in the schema (001_init.sql) — COALESCE
	// keeps pgx from failing to scan NULL into a plain string.
	query := fmt.Sprintf(`
		SELECT u.id, u.employee_id, u.name, u.email, u.password_hash,
		       u.role_id, COALESCE(r.name, ''), u.shift,
		       u.tracking_enabled, u.tracking_status, u.status, u.is_online,
		       COALESCE(u.avatar, ''), COALESCE(u.avatar_color, ''),
		       u.created_at, u.updated_at, u.deleted_at
		FROM users u LEFT JOIN roles r ON r.id = u.role_id
		%s
		ORDER BY u.created_at DESC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list users: %w", err)
	}
	defer rows.Close()

	var users []models.User
	for rows.Next() {
		var u models.User
		if err := rows.Scan(
			&u.ID, &u.EmployeeID, &u.Name, &u.Email, &u.PasswordHash,
			&u.RoleID, &u.RoleName, &u.Shift,
			&u.TrackingEnabled, &u.TrackingStatus, &u.Status, &u.IsOnline,
			&u.Avatar, &u.AvatarColor,
			&u.CreatedAt, &u.UpdatedAt, &u.DeletedAt,
		); err != nil {
			return nil, fmt.Errorf("scan user row: %w", err)
		}
		users = append(users, u)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate users: %w", err)
	}

	return &ListResult{
		Users:      users,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}

// scanUserRow scans a single user row using manual field scan.
func scanUserRow(rows pgx.Rows) (*models.User, error) {
	user, err := pgx.CollectOneRow(rows, func(row pgx.CollectableRow) (models.User, error) {
		var u models.User
		err := row.Scan(
			&u.ID, &u.EmployeeID, &u.Name, &u.Email, &u.PasswordHash,
			&u.RoleID, &u.RoleName, &u.Shift,
			&u.TrackingEnabled, &u.TrackingStatus, &u.Status, &u.IsOnline,
			&u.Avatar, &u.AvatarColor,
			&u.CreatedAt, &u.UpdatedAt, &u.DeletedAt,
		)
		return u, err
	})
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, fmt.Errorf("scan user: %w", err)
	}
	return &user, nil
}

// execUserQuery executes a query against a pool or tx and scans a single user row.
func execUserQuery(ctx context.Context, q interface {
	Query(ctx context.Context, sql string, args ...interface{}) (pgx.Rows, error)
}, query string, args ...interface{}) (*models.User, error) {
	rows, err := q.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("query user: %w", err)
	}
	return scanUserRow(rows)
}

// getByID returns a user by their UUID (internal helper).
func (r *UserRepo) getByID(ctx context.Context, query string, args ...interface{}) (*models.User, error) {
	return execUserQuery(ctx, r.pool, query, args...)
}

// userSelectColumns is the shared projection for single-user reads.
const userSelectColumns = `
		SELECT u.id, u.employee_id, u.name, u.email, u.password_hash,
		       u.role_id, COALESCE(r.name, ''), u.shift,
		       u.tracking_enabled, u.tracking_status, u.status, u.is_online,
		       COALESCE(u.avatar, ''), COALESCE(u.avatar_color, ''),
		       u.created_at, u.updated_at, u.deleted_at
		FROM users u LEFT JOIN roles r ON r.id = u.role_id`

// GetByID returns a user by their UUID.
func (r *UserRepo) GetByID(ctx context.Context, id string) (*models.User, error) {
	return r.getByID(ctx, userSelectColumns+`
		WHERE u.id = $1 AND u.deleted_at IS NULL
	`, id)
}

// GetByEmail returns a user by their email.
func (r *UserRepo) GetByEmail(ctx context.Context, email string) (*models.User, error) {
	return r.getByID(ctx, userSelectColumns+`
		WHERE u.email = $1 AND u.deleted_at IS NULL
	`, email)
}

// Create inserts a new user and returns it.
func (r *UserRepo) Create(ctx context.Context, u *models.User) (*models.User, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	// `status` is deliberately NOT in the INSERT: migration 044 gives it
	// NOT NULL DEFAULT 'active', so every insert path (service create, admin
	// bootstrap) starts active without callers having to remember it. It IS in
	// the RETURNING so the response carries the value.
	query := `
		INSERT INTO users (name, email, password_hash, employee_id, role_id, shift,
		                   tracking_enabled, tracking_status, is_online)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
		RETURNING id, employee_id, name, email, password_hash,
		          role_id, (SELECT name FROM roles WHERE id = role_id), shift,
		          tracking_enabled, tracking_status, status, is_online,
		          COALESCE(avatar, ''), COALESCE(avatar_color, ''),
		          created_at, updated_at, deleted_at
	`
	user, err := execUserQuery(ctx, tx, query,
		u.Name, u.Email, u.PasswordHash, u.EmployeeID, u.RoleID, u.Shift,
		u.TrackingEnabled, u.TrackingStatus, u.IsOnline,
	)
	if err != nil {
		if isDuplicateKeyErr(err) {
			return nil, fmt.Errorf("email already exists")
		}
		return nil, err
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit tx: %w", err)
	}
	return user, nil
}

// Update partially updates a user and returns the updated record.
func (r *UserRepo) Update(ctx context.Context, id string, updates map[string]interface{}) (*models.User, error) {
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
		"name": "name", "email": "email", "role_id": "role_id",
		"password_hash": "password_hash", "shift": "shift",
		"tracking_enabled": "tracking_enabled",
		"tracking_status":  "tracking_status", "is_online": "is_online",
		"status": "status",
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
		UPDATE users u SET %s
		WHERE u.id = $1 AND u.deleted_at IS NULL
		RETURNING u.id, u.employee_id, u.name, u.email, u.password_hash,
		          u.role_id, (SELECT name FROM roles WHERE id = u.role_id), u.shift,
		          u.tracking_enabled, u.tracking_status, u.status, u.is_online,
		          COALESCE(u.avatar, ''), COALESCE(u.avatar_color, ''),
		          u.created_at, u.updated_at, u.deleted_at
	`, strings.Join(setClauses, ", "))

	user, err := execUserQuery(ctx, tx, query, args...)
	if err != nil {
		if isDuplicateKeyErr(err) {
			return nil, fmt.Errorf("email already exists")
		}
		return nil, err
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit tx: %w", err)
	}
	return user, nil
}

// Delete softly removes a user by ID (sets deleted_at).
func (r *UserRepo) Delete(ctx context.Context, id string) error {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	tag, err := tx.Exec(ctx, "UPDATE users SET deleted_at = NOW() WHERE id = $1 AND deleted_at IS NULL", id)
	if err != nil {
		return fmt.Errorf("delete user: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("user not found")
	}

	if err := tx.Commit(ctx); err != nil {
		return fmt.Errorf("commit tx: %w", err)
	}
	return nil
}

// IsUniqueEmail checks if an email is already taken (excluding a given user ID).
func (r *UserRepo) IsUniqueEmail(ctx context.Context, email, excludeID string) (bool, error) {
	var exists bool
	var err error
	if excludeID != "" {
		err = r.pool.QueryRow(ctx, "SELECT EXISTS(SELECT 1 FROM users WHERE email = $1 AND id != $2 AND deleted_at IS NULL)", email, excludeID).Scan(&exists)
	} else {
		err = r.pool.QueryRow(ctx, "SELECT EXISTS(SELECT 1 FROM users WHERE email = $1 AND deleted_at IS NULL)", email).Scan(&exists)
	}
	if err != nil {
		return false, fmt.Errorf("check email uniqueness: %w", err)
	}
	return !exists, nil
}

// isDuplicateKeyErr checks if an error is a PostgreSQL unique constraint violation.
func isDuplicateKeyErr(err error) bool {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) {
		return pgErr.Code == "23505"
	}
	return false
}
