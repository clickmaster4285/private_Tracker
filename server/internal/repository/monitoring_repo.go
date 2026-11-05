package repository

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

// MonitoringRepo handles database operations for the monitoring configuration
// domain: types, categories, and classification of the detected app catalog and
// observed website domains.
type MonitoringRepo struct {
	pool *pgxpool.Pool
}

// NewMonitoringRepo creates a new MonitoringRepo.
func NewMonitoringRepo(pool *pgxpool.Pool) *MonitoringRepo {
	return &MonitoringRepo{pool: pool}
}

// ────────────────────────────────
// TYPES
// ────────────────────────────────

// MonitoringType is a classification type (e.g. Productive / Unproductive / Neutral).
type MonitoringType struct {
	ID          int    `json:"id"`
	Name        string `json:"name"`
	Color       string `json:"color"`
	Description string `json:"description"`
}

// ListTypes returns all non-deleted types ordered by name.
func (r *MonitoringRepo) ListTypes(ctx context.Context) ([]MonitoringType, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, name, COALESCE(color, '') AS color, COALESCE(description, '') AS description
		FROM monitoring_types
		WHERE deleted_at IS NULL
		ORDER BY name ASC
	`)
	if err != nil {
		return nil, fmt.Errorf("list monitoring types: %w", err)
	}
	defer rows.Close()

	var types []MonitoringType
	for rows.Next() {
		var t MonitoringType
		if err := rows.Scan(&t.ID, &t.Name, &t.Color, &t.Description); err != nil {
			return nil, fmt.Errorf("scan monitoring type: %w", err)
		}
		types = append(types, t)
	}
	return types, rows.Err()
}

// GetTypeByID returns a single non-deleted type.
func (r *MonitoringRepo) GetTypeByID(ctx context.Context, id int) (*MonitoringType, error) {
	var t MonitoringType
	err := r.pool.QueryRow(ctx, `
		SELECT id, name, COALESCE(color, '') AS color, COALESCE(description, '') AS description
		FROM monitoring_types
		WHERE id = $1 AND deleted_at IS NULL
	`, id).Scan(&t.ID, &t.Name, &t.Color, &t.Description)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, fmt.Errorf("monitoring type not found")
		}
		return nil, fmt.Errorf("get monitoring type: %w", err)
	}
	return &t, nil
}

// CreateType creates a new type.
func (r *MonitoringRepo) CreateType(ctx context.Context, t MonitoringType) (*MonitoringType, error) {
	var created MonitoringType
	err := r.pool.QueryRow(ctx, `
		INSERT INTO monitoring_types (name, color, description)
		VALUES ($1, $2, $3)
		RETURNING id, name, COALESCE(color, '') AS color, COALESCE(description, '') AS description
	`, t.Name, t.Color, t.Description).Scan(&created.ID, &created.Name, &created.Color, &created.Description)
	if err != nil {
		return nil, fmt.Errorf("create monitoring type: %w", err)
	}
	return &created, nil
}

// UpdateType updates an existing type.
func (r *MonitoringRepo) UpdateType(ctx context.Context, id int, t MonitoringType) (*MonitoringType, error) {
	var updated MonitoringType
	err := r.pool.QueryRow(ctx, `
		UPDATE monitoring_types
		SET name = $1, color = $2, description = $3
		WHERE id = $4 AND deleted_at IS NULL
		RETURNING id, name, COALESCE(color, '') AS color, COALESCE(description, '') AS description
	`, t.Name, t.Color, t.Description, id).Scan(&updated.ID, &updated.Name, &updated.Color, &updated.Description)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, fmt.Errorf("monitoring type not found")
		}
		return nil, fmt.Errorf("update monitoring type: %w", err)
	}
	return &updated, nil
}

// CountTypeUsage returns how many non-deleted apps/sites reference a type.
func (r *MonitoringRepo) CountTypeUsage(ctx context.Context, id int) (int, error) {
	var count int
	err := r.pool.QueryRow(ctx, `
		SELECT
			(SELECT COUNT(*) FROM installed_applications WHERE type_id = $1 AND deleted_at IS NULL) +
			(SELECT COUNT(*) FROM monitoring_sites WHERE type_id = $1 AND deleted_at IS NULL)
	`, id).Scan(&count)
	if err != nil {
		return 0, fmt.Errorf("count monitoring type usage: %w", err)
	}
	return count, nil
}

// DeleteType soft-deletes a type.
func (r *MonitoringRepo) DeleteType(ctx context.Context, id int) error {
	tag, err := r.pool.Exec(ctx, `
		UPDATE monitoring_types SET deleted_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL
	`, id)
	if err != nil {
		return fmt.Errorf("delete monitoring type: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("monitoring type not found")
	}
	return nil
}

// ────────────────────────────────
// CATEGORIES
// ────────────────────────────────

// MonitoringCategory is a classification category scoped by kind
// (application | website | both).
type MonitoringCategory struct {
	ID   int    `json:"id"`
	Name string `json:"name"`
	Kind string `json:"kind"`
}

// ListCategories returns non-deleted categories, optionally filtered by kind.
func (r *MonitoringRepo) ListCategories(ctx context.Context, kind string) ([]MonitoringCategory, error) {
	query := `
		SELECT id, name, kind
		FROM monitoring_categories
		WHERE deleted_at IS NULL
	`
	var args []interface{}
	if kind != "" {
		query += " AND kind = $1"
		args = append(args, kind)
	}
	query += " ORDER BY name ASC"

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list monitoring categories: %w", err)
	}
	defer rows.Close()

	var cats []MonitoringCategory
	for rows.Next() {
		var c MonitoringCategory
		if err := rows.Scan(&c.ID, &c.Name, &c.Kind); err != nil {
			return nil, fmt.Errorf("scan monitoring category: %w", err)
		}
		cats = append(cats, c)
	}
	return cats, rows.Err()
}

// GetCategoryByID returns a single non-deleted category.
func (r *MonitoringRepo) GetCategoryByID(ctx context.Context, id int) (*MonitoringCategory, error) {
	var c MonitoringCategory
	err := r.pool.QueryRow(ctx, `
		SELECT id, name, kind
		FROM monitoring_categories
		WHERE id = $1 AND deleted_at IS NULL
	`, id).Scan(&c.ID, &c.Name, &c.Kind)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, fmt.Errorf("monitoring category not found")
		}
		return nil, fmt.Errorf("get monitoring category: %w", err)
	}
	return &c, nil
}

// CreateCategory creates a new category.
func (r *MonitoringRepo) CreateCategory(ctx context.Context, c MonitoringCategory) (*MonitoringCategory, error) {
	var created MonitoringCategory
	err := r.pool.QueryRow(ctx, `
		INSERT INTO monitoring_categories (name, kind)
		VALUES ($1, $2)
		RETURNING id, name, kind
	`, c.Name, c.Kind).Scan(&created.ID, &created.Name, &created.Kind)
	if err != nil {
		return nil, fmt.Errorf("create monitoring category: %w", err)
	}
	return &created, nil
}

// UpdateCategory updates an existing category.
func (r *MonitoringRepo) UpdateCategory(ctx context.Context, id int, c MonitoringCategory) (*MonitoringCategory, error) {
	var updated MonitoringCategory
	err := r.pool.QueryRow(ctx, `
		UPDATE monitoring_categories
		SET name = $1, kind = $2
		WHERE id = $3 AND deleted_at IS NULL
		RETURNING id, name, kind
	`, c.Name, c.Kind, id).Scan(&updated.ID, &updated.Name, &updated.Kind)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, fmt.Errorf("monitoring category not found")
		}
		return nil, fmt.Errorf("update monitoring category: %w", err)
	}
	return &updated, nil
}

// DeleteCategory soft-deletes a category (existing classifications are detached).
func (r *MonitoringRepo) DeleteCategory(ctx context.Context, id int) error {
	tag, err := r.pool.Exec(ctx, `
		UPDATE monitoring_categories SET deleted_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL
	`, id)
	if err != nil {
		return fmt.Errorf("delete monitoring category: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("monitoring category not found")
	}
	return nil
}

// ────────────────────────────────
// APPLICATIONS (detected catalog classification)
// ────────────────────────────────

// MonitoredApp is a detected catalog application with its classification.
type MonitoredApp struct {
	ID           string `json:"id"`
	AppName      string `json:"appName"`
	BinaryName   string `json:"binaryName"`
	Categories   string `json:"categories"`
	IsBrowser    bool   `json:"isBrowser"`
	TypeID       *int   `json:"typeId,omitempty"`
	TypeName     string `json:"typeName"`
	TypeColor    string `json:"typeColor"`
	CategoryID   *int   `json:"categoryId,omitempty"`
	CategoryName string `json:"categoryName"`
}

// MonitoredAppListParams paginates/filters the app catalog.
type MonitoredAppListParams struct {
	Search       string
	TypeID       int
	CategoryID   int
	Unclassified bool
	Page         int
	PerPage      int
}

// MonitoredAppListResult is the paginated app catalog response.
type MonitoredAppListResult struct {
	Apps       []MonitoredApp
	Total      int
	Page       int
	PerPage    int
	TotalPages int
}

func (r *MonitoringRepo) ListApps(ctx context.Context, params MonitoredAppListParams) (*MonitoredAppListResult, error) {
	if params.Page < 1 {
		params.Page = 1
	}
	if params.PerPage < 1 || params.PerPage > 100 {
		params.PerPage = 20
	}

	var conditions []string
	var args []interface{}
	argIdx := 1

	conditions = append(conditions, "ia.deleted_at IS NULL AND COALESCE(ia.app_name, '') <> ''")

	if params.Search != "" {
		conditions = append(conditions, fmt.Sprintf(
			"(LOWER(ia.app_name) LIKE LOWER($%d) OR LOWER(COALESCE(ia.binary_name, '')) LIKE LOWER($%d))",
			argIdx, argIdx))
		args = append(args, "%"+params.Search+"%")
		argIdx++
	}
	if params.TypeID > 0 {
		conditions = append(conditions, fmt.Sprintf("ia.type_id = $%d", argIdx))
		args = append(args, params.TypeID)
		argIdx++
	}
	if params.CategoryID > 0 {
		conditions = append(conditions, fmt.Sprintf("ia.category_id = $%d", argIdx))
		args = append(args, params.CategoryID)
		argIdx++
	}
	if params.Unclassified {
		conditions = append(conditions, "(ia.type_id IS NULL OR ia.category_id IS NULL)")
	}

	whereClause := "WHERE " + strings.Join(conditions, " AND ")

	var total int
	if err := r.pool.QueryRow(ctx,
		fmt.Sprintf("SELECT COUNT(*) FROM installed_applications ia %s", whereClause),
		args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count monitored apps: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	query := fmt.Sprintf(`
		SELECT ia.id, ia.app_name, COALESCE(ia.binary_name, '') AS binary_name,
		       COALESCE(ia.categories, '') AS categories, ia.is_browser,
		       t.id AS type_id, COALESCE(t.name, '') AS type_name, COALESCE(t.color, '') AS type_color,
		       c.id AS category_id, COALESCE(c.name, '') AS category_name
		FROM installed_applications ia
		LEFT JOIN monitoring_types t ON t.id = ia.type_id AND t.deleted_at IS NULL
		LEFT JOIN monitoring_categories c ON c.id = ia.category_id AND c.deleted_at IS NULL
		%s
		ORDER BY LOWER(ia.app_name) ASC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list monitored apps: %w", err)
	}
	defer rows.Close()

	var apps []MonitoredApp
	for rows.Next() {
		var a MonitoredApp
		if err := rows.Scan(
			&a.ID, &a.AppName, &a.BinaryName, &a.Categories, &a.IsBrowser,
			&a.TypeID, &a.TypeName, &a.TypeColor,
			&a.CategoryID, &a.CategoryName,
		); err != nil {
			return nil, fmt.Errorf("scan monitored app row: %w", err)
		}
		apps = append(apps, a)
	}

	return &MonitoredAppListResult{
		Apps:       apps,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}

// UpdateAppClassification sets (or clears) an app's type/category assignment.
func (r *MonitoringRepo) UpdateAppClassification(ctx context.Context, id string, typeID, categoryID *int) error {
	tag, err := r.pool.Exec(ctx, `
		UPDATE installed_applications
		SET type_id = $1, category_id = $2
		WHERE id = $3 AND deleted_at IS NULL
	`, nullableInt(typeID), nullableInt(categoryID), id)
	if err != nil {
		return fmt.Errorf("update app classification: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("application not found")
	}
	return nil
}

// ────────────────────────────────
// WEBSITES (observed domain registry)
// ────────────────────────────────

// MonitoredSite is an observed website domain with its classification.
type MonitoredSite struct {
	ID           int64  `json:"id"`
	Domain       string `json:"domain"`
	TypeID       *int   `json:"typeId,omitempty"`
	TypeName     string `json:"typeName"`
	TypeColor    string `json:"typeColor"`
	CategoryID   *int   `json:"categoryId,omitempty"`
	CategoryName string `json:"categoryName"`
}

// MonitoredSiteListParams paginates/filters the site registry.
type MonitoredSiteListParams struct {
	Search       string
	TypeID       int
	CategoryID   int
	Unclassified bool
	Page         int
	PerPage      int
}

// MonitoredSiteListResult is the paginated site registry response.
type MonitoredSiteListResult struct {
	Sites      []MonitoredSite
	Total      int
	Page       int
	PerPage    int
	TotalPages int
}

// SyncWebsiteDomains ingests every distinct observed domain from app_items into the
// monitoring_sites registry (idempotent — existing rows get their last_seen refreshed).
func (r *MonitoringRepo) SyncWebsiteDomains(ctx context.Context) (int, error) {
	tag, err := r.pool.Exec(ctx, `
		INSERT INTO monitoring_sites (domain)
		SELECT DISTINCT domain
		FROM app_items
		WHERE COALESCE(domain, '') <> '' AND deleted_at IS NULL
		ON CONFLICT (domain) WHERE deleted_at IS NULL
		DO UPDATE SET last_seen_at = NOW()
	`)
	if err != nil {
		return 0, fmt.Errorf("sync website domains: %w", err)
	}
	return int(tag.RowsAffected()), nil
}

// CreateWebsite inserts a new monitoring_sites row and returns the created site.
func (r *MonitoringRepo) CreateWebsite(ctx context.Context, domain string, typeID, categoryID *int) (*MonitoredSite, error) {
	now := time.Now()
	var site MonitoredSite
	err := r.pool.QueryRow(ctx, `
		INSERT INTO monitoring_sites (domain, type_id, category_id, first_seen_at, last_seen_at, created_at, updated_at)
		VALUES ($1, $2, $3, $4, $5, $6, $7)
		ON CONFLICT (domain) WHERE deleted_at IS NULL
		DO UPDATE SET updated_at = $7
		RETURNING id, domain, type_id, category_id
	`, domain, nullableInt(typeID), nullableInt(categoryID), now, now, now, now).Scan(
		&site.ID, &site.Domain, &site.TypeID, &site.CategoryID,
	)
	if err != nil {
		return nil, fmt.Errorf("create website: %w", err)
	}
	return &site, nil
}

// ListWebsites returns the paginated site registry joined with type/category names.
func (r *MonitoringRepo) ListWebsites(ctx context.Context, params MonitoredSiteListParams) (*MonitoredSiteListResult, error) {
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
		conditions = append(conditions, fmt.Sprintf("LOWER(s.domain) LIKE LOWER($%d)", argIdx))
		args = append(args, "%"+params.Search+"%")
		argIdx++
	}
	if params.TypeID > 0 {
		conditions = append(conditions, fmt.Sprintf("s.type_id = $%d", argIdx))
		args = append(args, params.TypeID)
		argIdx++
	}
	if params.CategoryID > 0 {
		conditions = append(conditions, fmt.Sprintf("s.category_id = $%d", argIdx))
		args = append(args, params.CategoryID)
		argIdx++
	}
	if params.Unclassified {
		conditions = append(conditions, "(s.type_id IS NULL OR s.category_id IS NULL)")
	}

	whereClause := "WHERE " + strings.Join(conditions, " AND ")

	var total int
	if err := r.pool.QueryRow(ctx,
		fmt.Sprintf("SELECT COUNT(*) FROM monitoring_sites s %s", whereClause),
		args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count monitored sites: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	query := fmt.Sprintf(`
		SELECT s.id, s.domain,
		       t.id AS type_id, COALESCE(t.name, '') AS type_name, COALESCE(t.color, '') AS type_color,
		       c.id AS category_id, COALESCE(c.name, '') AS category_name
		FROM monitoring_sites s
		LEFT JOIN monitoring_types t ON t.id = s.type_id AND t.deleted_at IS NULL
		LEFT JOIN monitoring_categories c ON c.id = s.category_id AND c.deleted_at IS NULL
		%s
		ORDER BY LOWER(s.domain) ASC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list monitored sites: %w", err)
	}
	defer rows.Close()

	var sites []MonitoredSite
	for rows.Next() {
		var s MonitoredSite
		if err := rows.Scan(
			&s.ID, &s.Domain,
			&s.TypeID, &s.TypeName, &s.TypeColor,
			&s.CategoryID, &s.CategoryName,
		); err != nil {
			return nil, fmt.Errorf("scan monitored site row: %w", err)
		}
		sites = append(sites, s)
	}

	return &MonitoredSiteListResult{
		Sites:      sites,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}

// UpdateSiteClassification sets (or clears) a site's type/category assignment.
func (r *MonitoringRepo) UpdateSiteClassification(ctx context.Context, id int64, typeID, categoryID *int) error {
	tag, err := r.pool.Exec(ctx, `
		UPDATE monitoring_sites
		SET type_id = $1, category_id = $2
		WHERE id = $3 AND deleted_at IS NULL
	`, nullableInt(typeID), nullableInt(categoryID), id)
	if err != nil {
		return fmt.Errorf("update site classification: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("website not found")
	}
	return nil
}

// nullableInt returns nil for a nil pointer so pgx stores SQL NULL, otherwise the value.
func nullableInt(v *int) interface{} {
	if v == nil {
		return nil
	}
	return *v
}