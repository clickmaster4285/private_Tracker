package repository

import (
	"context"
	"fmt"
	"strings"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/alpha-ai-tracker/server/internal/models"
)

// RBACRepo handles roles, modules, submodules and role permissions.
type RBACRepo struct {
	pool *pgxpool.Pool
}

// NewRBACRepo creates a new RBACRepo.
func NewRBACRepo(pool *pgxpool.Pool) *RBACRepo {
	return &RBACRepo{pool: pool}
}

// ────────────────────────────────
// Seed helpers (idempotent upserts, run on every boot)
// ────────────────────────────────

// UpsertModule inserts or refreshes a module row by key, returning its id.
func (r *RBACRepo) UpsertModule(ctx context.Context, key, name string, sortOrder int) (int, error) {
	var id int
	err := r.pool.QueryRow(ctx, `
		INSERT INTO modules (key, name, sort_order)
		VALUES ($1, $2, $3)
		ON CONFLICT (key) DO UPDATE SET name = EXCLUDED.name, sort_order = EXCLUDED.sort_order
		RETURNING id
	`, key, name, sortOrder).Scan(&id)
	if err != nil {
		return 0, fmt.Errorf("upsert module %s: %w", key, err)
	}
	return id, nil
}

// UpsertSubmodule inserts or refreshes a submodule row by key, returning its id.
func (r *RBACRepo) UpsertSubmodule(ctx context.Context, moduleID int, key, name, routePath string, sortOrder int) (int, error) {
	var id int
	err := r.pool.QueryRow(ctx, `
		INSERT INTO submodules (module_id, key, name, route_path, sort_order)
		VALUES ($1, $2, $3, $4, $5)
		ON CONFLICT (key) DO UPDATE SET
		  module_id = EXCLUDED.module_id,
		  name = EXCLUDED.name,
		  route_path = EXCLUDED.route_path,
		  sort_order = EXCLUDED.sort_order
		RETURNING id
	`, moduleID, key, name, routePath, sortOrder).Scan(&id)
	if err != nil {
		return 0, fmt.Errorf("upsert submodule %s: %w", key, err)
	}
	return id, nil
}

// DeleteSubmoduleByKey removes a catalog key and its role grants (FK cascade).
// Used when a submodule leaves the product so existing databases drop it on boot.
func (r *RBACRepo) DeleteSubmoduleByKey(ctx context.Context, key string) error {
	_, err := r.pool.Exec(ctx, `DELETE FROM submodules WHERE key = $1`, key)
	if err != nil {
		return fmt.Errorf("delete submodule %s: %w", key, err)
	}
	return nil
}

// EnsureRole creates the role when missing (never renames an existing one), returning its id.
func (r *RBACRepo) EnsureRole(ctx context.Context, name, description string, isSystem bool) (int, error) {
	var id int
	err := r.pool.QueryRow(ctx, `
		INSERT INTO roles (name, description, is_system)
		VALUES ($1, $2, $3)
		ON CONFLICT (name) DO UPDATE SET is_system = roles.is_system OR EXCLUDED.is_system
		RETURNING id
	`, name, description, isSystem).Scan(&id)
	if err != nil {
		return 0, fmt.Errorf("ensure role %s: %w", name, err)
	}
	return id, nil
}

// GrantAllPermissions grants every submodule to the role (missing rows only).
func (r *RBACRepo) GrantAllPermissions(ctx context.Context, roleID int) error {
	_, err := r.pool.Exec(ctx, `
		INSERT INTO role_submodule_permissions (role_id, submodule_id)
		SELECT $1, id FROM submodules
		ON CONFLICT (role_id, submodule_id) DO NOTHING
	`, roleID)
	if err != nil {
		return fmt.Errorf("grant all permissions to role %d: %w", roleID, err)
	}
	return nil
}

// GetRoleByName returns a non-deleted role by its exact name (nil when missing).
func (r *RBACRepo) GetRoleByName(ctx context.Context, name string) (*models.Role, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, name, description, is_system, created_at, updated_at
		FROM roles WHERE name = $1 AND deleted_at IS NULL
	`, name)
	if err != nil {
		return nil, fmt.Errorf("get role by name: %w", err)
	}
	defer rows.Close()

	if !rows.Next() {
		return nil, nil
	}
	var role models.Role
	if err := rows.Scan(&role.ID, &role.Name, &role.Description, &role.IsSystem, &role.CreatedAt, &role.UpdatedAt); err != nil {
		return nil, fmt.Errorf("scan role: %w", err)
	}
	return &role, nil
}

// ────────────────────────────────
// Reads
// ────────────────────────────────

// ListModules returns all modules with their submodules ordered for display.
func (r *RBACRepo) ListModules(ctx context.Context) ([]models.Module, error) {
	modRows, err := r.pool.Query(ctx, `
		SELECT id, key, name, sort_order FROM modules ORDER BY sort_order, id
	`)
	if err != nil {
		return nil, fmt.Errorf("list modules: %w", err)
	}
	defer modRows.Close()

	moduleByID := map[int]*models.Module{}
	var order []int
	for modRows.Next() {
		var m models.Module
		if err := modRows.Scan(&m.ID, &m.Key, &m.Name, &m.SortOrder); err != nil {
			return nil, fmt.Errorf("scan module: %w", err)
		}
		m.Submodules = []models.Submodule{}
		moduleByID[m.ID] = &m
		order = append(order, m.ID)
	}
	if err := modRows.Err(); err != nil {
		return nil, fmt.Errorf("iterate modules: %w", err)
	}

	subRows, err := r.pool.Query(ctx, `
		SELECT id, module_id, key, name, route_path FROM submodules
		ORDER BY sort_order, id
	`)
	if err != nil {
		return nil, fmt.Errorf("list submodules: %w", err)
	}
	defer subRows.Close()

	for subRows.Next() {
		var s models.Submodule
		if err := subRows.Scan(&s.ID, &s.ModuleID, &s.Key, &s.Name, &s.RoutePath); err != nil {
			return nil, fmt.Errorf("scan submodule: %w", err)
		}
		if m, ok := moduleByID[s.ModuleID]; ok {
			m.Submodules = append(m.Submodules, s)
		}
	}
	if err := subRows.Err(); err != nil {
		return nil, fmt.Errorf("iterate submodules: %w", err)
	}

	modules := make([]models.Module, 0, len(order))
	for _, id := range order {
		modules = append(modules, *moduleByID[id])
	}
	return modules, nil
}

// ListRoles returns all non-deleted roles with permission keys/ids and user counts.
func (r *RBACRepo) ListRoles(ctx context.Context) ([]models.Role, error) {
	roleRows, err := r.pool.Query(ctx, `
		SELECT r.id, r.name, r.description, r.is_system, r.created_at, r.updated_at,
		       (SELECT COUNT(*) FROM users u WHERE u.role_id = r.id AND u.deleted_at IS NULL) AS user_count
		FROM roles r
		WHERE r.deleted_at IS NULL
		ORDER BY r.is_system DESC, r.id
	`)
	if err != nil {
		return nil, fmt.Errorf("list roles: %w", err)
	}
	defer roleRows.Close()

	roleByID := map[int]*models.Role{}
	var order []int
	for roleRows.Next() {
		var role models.Role
		if err := roleRows.Scan(&role.ID, &role.Name, &role.Description, &role.IsSystem,
			&role.CreatedAt, &role.UpdatedAt, &role.UserCount); err != nil {
			return nil, fmt.Errorf("scan role: %w", err)
		}
		role.SubmoduleIDs = []int{}
		role.Permissions = []string{}
		roleByID[role.ID] = &role
		order = append(order, role.ID)
	}
	if err := roleRows.Err(); err != nil {
		return nil, fmt.Errorf("iterate roles: %w", err)
	}

	permRows, err := r.pool.Query(ctx, `
		SELECT rsp.role_id, rsp.submodule_id, s.key
		FROM role_submodule_permissions rsp
		JOIN submodules s ON s.id = rsp.submodule_id
		ORDER BY s.sort_order, s.id
	`)
	if err != nil {
		return nil, fmt.Errorf("list role permissions: %w", err)
	}
	defer permRows.Close()

	for permRows.Next() {
		var roleID, subID int
		var key string
		if err := permRows.Scan(&roleID, &subID, &key); err != nil {
			return nil, fmt.Errorf("scan role permission: %w", err)
		}
		if role, ok := roleByID[roleID]; ok {
			role.SubmoduleIDs = append(role.SubmoduleIDs, subID)
			role.Permissions = append(role.Permissions, key)
		}
	}
	if err := permRows.Err(); err != nil {
		return nil, fmt.Errorf("iterate role permissions: %w", err)
	}

	roles := make([]models.Role, 0, len(order))
	for _, id := range order {
		roles = append(roles, *roleByID[id])
	}
	return roles, nil
}

// PermissionKeysForUser resolves the granted submodule keys for a user's role.
func (r *RBACRepo) PermissionKeysForUser(ctx context.Context, userID string) ([]string, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT s.key
		FROM users u
		JOIN role_submodule_permissions rsp ON rsp.role_id = u.role_id
		JOIN submodules s ON s.id = rsp.submodule_id
		WHERE u.id = $1 AND u.deleted_at IS NULL
		ORDER BY s.sort_order, s.id
	`, userID)
	if err != nil {
		return nil, fmt.Errorf("permission keys for user: %w", err)
	}
	defer rows.Close()

	keys := []string{}
	for rows.Next() {
		var key string
		if err := rows.Scan(&key); err != nil {
			return nil, fmt.Errorf("scan permission key: %w", err)
		}
		keys = append(keys, key)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate permission keys: %w", err)
	}
	return keys, nil
}

// IsUserRoleSystem reports whether the user's role is a system role.
func (r *UserRepo) IsUserRoleSystem(ctx context.Context, userID string) (bool, error) {
	var isSystem bool
	err := r.pool.QueryRow(ctx, `
		SELECT COALESCE(r.is_system, false)
		FROM users u LEFT JOIN roles r ON r.id = u.role_id
		WHERE u.id = $1 AND u.deleted_at IS NULL
	`, userID).Scan(&isSystem)
	if err != nil {
		return false, fmt.Errorf("check user role system flag: %w", err)
	}
	return isSystem, nil
}

// CountUsersWithRole counts active users attached to the given role name.
func (r *UserRepo) CountUsersWithRole(ctx context.Context, roleName string) (int, error) {
	var count int
	err := r.pool.QueryRow(ctx, `
		SELECT COUNT(*) FROM users u
		JOIN roles r ON r.id = u.role_id
		WHERE r.name = $1 AND u.deleted_at IS NULL
	`, roleName).Scan(&count)
	if err != nil {
		return 0, fmt.Errorf("count users with role %s: %w", roleName, err)
	}
	return count, nil
}

// ────────────────────────────────
// Writes
// ────────────────────────────────

// CreateRole inserts a role and returns it.
func (r *RBACRepo) CreateRole(ctx context.Context, name, description string) (*models.Role, error) {
	var role models.Role
	err := r.pool.QueryRow(ctx, `
		INSERT INTO roles (name, description, is_system)
		VALUES ($1, $2, false)
		RETURNING id, name, description, is_system, created_at, updated_at
	`, name, description).Scan(&role.ID, &role.Name, &role.Description, &role.IsSystem,
		&role.CreatedAt, &role.UpdatedAt)
	if err != nil {
		if isDuplicateKeyErr(err) {
			return nil, fmt.Errorf("role name already exists")
		}
		return nil, fmt.Errorf("create role: %w", err)
	}
	role.SubmoduleIDs = []int{}
	role.Permissions = []string{}
	return &role, nil
}

// UpdateRole partially updates a role's metadata; system roles are rejected.
func (r *RBACRepo) UpdateRole(ctx context.Context, id int, name, description *string) (*models.Role, error) {
	var setClauses []string
	var args []interface{}
	argIdx := 1
	args = append(args, id)

	if name != nil {
		argIdx++
		setClauses = append(setClauses, fmt.Sprintf("name = $%d", argIdx))
		args = append(args, *name)
	}
	if description != nil {
		argIdx++
		setClauses = append(setClauses, fmt.Sprintf("description = $%d", argIdx))
		args = append(args, *description)
	}
	if len(setClauses) == 0 {
		return r.getRoleByID(ctx, id)
	}

	query := fmt.Sprintf(`
		UPDATE roles SET %s
		WHERE id = $1 AND deleted_at IS NULL AND NOT is_system
		RETURNING id, name, description, is_system, created_at, updated_at
	`, strings.Join(setClauses, ", "))

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		if isDuplicateKeyErr(err) {
			return nil, fmt.Errorf("role name already exists")
		}
		return nil, fmt.Errorf("update role: %w", err)
	}
	defer rows.Close()

	if !rows.Next() {
		return nil, nil
	}
	var role models.Role
	if err := rows.Scan(&role.ID, &role.Name, &role.Description, &role.IsSystem,
		&role.CreatedAt, &role.UpdatedAt); err != nil {
		return nil, fmt.Errorf("scan updated role: %w", err)
	}
	role.SubmoduleIDs = []int{}
	role.Permissions = []string{}
	return &role, nil
}

// getRoleByID fetches a single non-deleted role.
func (r *RBACRepo) getRoleByID(ctx context.Context, id int) (*models.Role, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, name, description, is_system, created_at, updated_at
		FROM roles WHERE id = $1 AND deleted_at IS NULL
	`, id)
	if err != nil {
		return nil, fmt.Errorf("get role: %w", err)
	}
	defer rows.Close()

	if !rows.Next() {
		return nil, nil
	}
	var role models.Role
	if err := rows.Scan(&role.ID, &role.Name, &role.Description, &role.IsSystem,
		&role.CreatedAt, &role.UpdatedAt); err != nil {
		return nil, fmt.Errorf("scan role: %w", err)
	}
	role.SubmoduleIDs = []int{}
	role.Permissions = []string{}
	return &role, nil
}

// GetRoleByID returns a single non-deleted role without its grants.
func (r *RBACRepo) GetRoleByID(ctx context.Context, id int) (*models.Role, error) {
	return r.getRoleByID(ctx, id)
}

// GetRoleByIDWithPerms returns a role with its granted submodule ids and keys.
func (r *RBACRepo) GetRoleByIDWithPerms(ctx context.Context, id int) (*models.Role, error) {
	role, err := r.getRoleByID(ctx, id)
	if err != nil || role == nil {
		return role, err
	}
	permRows, err := r.pool.Query(ctx, `
		SELECT rsp.submodule_id, s.key
		FROM role_submodule_permissions rsp
		JOIN submodules s ON s.id = rsp.submodule_id
		WHERE rsp.role_id = $1
		ORDER BY s.sort_order, s.id
	`, id)
	if err != nil {
		return nil, fmt.Errorf("list role permissions: %w", err)
	}
	defer permRows.Close()

	for permRows.Next() {
		var subID int
		var key string
		if err := permRows.Scan(&subID, &key); err != nil {
			return nil, fmt.Errorf("scan role permission: %w", err)
		}
		role.SubmoduleIDs = append(role.SubmoduleIDs, subID)
		role.Permissions = append(role.Permissions, key)
	}
	if err := permRows.Err(); err != nil {
		return nil, fmt.Errorf("iterate role permissions: %w", err)
	}
	return role, nil
}

// ReplacePermissions swaps the role's granted submodule set in ONE transaction.
func (r *RBACRepo) ReplacePermissions(ctx context.Context, roleID int, submoduleIDs []int) error {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	if _, err := tx.Exec(ctx,
		"DELETE FROM role_submodule_permissions WHERE role_id = $1", roleID); err != nil {
		return fmt.Errorf("clear permissions: %w", err)
	}

	for _, subID := range submoduleIDs {
		if _, err := tx.Exec(ctx, `
			INSERT INTO role_submodule_permissions (role_id, submodule_id)
			VALUES ($1, $2)
			ON CONFLICT (role_id, submodule_id) DO NOTHING
		`, roleID, subID); err != nil {
			return fmt.Errorf("grant submodule %d: %w", subID, err)
		}
	}

	if err := tx.Commit(ctx); err != nil {
		return fmt.Errorf("commit tx: %w", err)
	}
	return nil
}

// DeleteRole soft-deletes a role; system roles and roles still assigned to users are rejected.
func (r *RBACRepo) DeleteRole(ctx context.Context, id int) error {
	tag, err := r.pool.Exec(ctx, `
		UPDATE roles SET deleted_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL AND NOT is_system
		  AND NOT EXISTS (SELECT 1 FROM users u WHERE u.role_id = $1 AND u.deleted_at IS NULL)
	`, id)
	if err != nil {
		return fmt.Errorf("delete role: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("role not found, is a system role, or still has users")
	}
	return nil
}

// RoleHasUsers reports whether any active user is attached to the role.
func (r *RBACRepo) RoleHasUsers(ctx context.Context, roleID int) (bool, error) {
	var count int
	err := r.pool.QueryRow(ctx,
		"SELECT COUNT(*) FROM users WHERE role_id = $1 AND deleted_at IS NULL", roleID).Scan(&count)
	if err != nil {
		return false, fmt.Errorf("count role users: %w", err)
	}
	return count > 0, nil
}
