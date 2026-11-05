package services

import (
	"context"
	"fmt"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/repository"
)

// MonitoringService handles business logic for the monitoring configuration
// domain (types, categories, app/site classification).
type MonitoringService struct {
	repo *repository.MonitoringRepo
}

// NewMonitoringService creates a new MonitoringService.
func NewMonitoringService(repo *repository.MonitoringRepo) *MonitoringService {
	return &MonitoringService{repo: repo}
}

// ────────────────────────────────
// TYPES
// ────────────────────────────────

// MonitoringTypeInfo is the public type payload.
type MonitoringTypeInfo struct {
	ID          int    `json:"id"`
	Name        string `json:"name"`
	Color       string `json:"color"`
	Description string `json:"description"`
}

// ListTypes returns all types.
func (s *MonitoringService) ListTypes(ctx context.Context) ([]MonitoringTypeInfo, error) {
	types, err := s.repo.ListTypes(ctx)
	if err != nil {
		return nil, err
	}
	info := make([]MonitoringTypeInfo, len(types))
	for i, t := range types {
		info[i] = toTypeInfo(t)
	}
	return info, nil
}

// CreateType creates a new type.
func (s *MonitoringService) CreateType(ctx context.Context, t repository.MonitoringType) (*MonitoringTypeInfo, error) {
	if strings.TrimSpace(t.Name) == "" {
		return nil, fmt.Errorf("type name is required")
	}
	created, err := s.repo.CreateType(ctx, t)
	if err != nil {
		return nil, err
	}
	return toTypeInfoPtr(created), nil
}

// UpdateType updates an existing type.
func (s *MonitoringService) UpdateType(ctx context.Context, id int, t repository.MonitoringType) (*MonitoringTypeInfo, error) {
	if strings.TrimSpace(t.Name) == "" {
		return nil, fmt.Errorf("type name is required")
	}
	updated, err := s.repo.UpdateType(ctx, id, t)
	if err != nil {
		return nil, err
	}
	return toTypeInfoPtr(updated), nil
}

// DeleteType soft-deletes a type, refusing when any app/site still uses it.
func (s *MonitoringService) DeleteType(ctx context.Context, id int) error {
	usage, err := s.repo.CountTypeUsage(ctx, id)
	if err != nil {
		return err
	}
	if usage > 0 {
		return fmt.Errorf("type is assigned to %d app(s)/site(s) and cannot be deleted", usage)
	}
	return s.repo.DeleteType(ctx, id)
}

func toTypeInfo(t repository.MonitoringType) MonitoringTypeInfo {
	return MonitoringTypeInfo{
		ID:          t.ID,
		Name:        t.Name,
		Color:       t.Color,
		Description: t.Description,
	}
}

func toTypeInfoPtr(t *repository.MonitoringType) *MonitoringTypeInfo {
	if t == nil {
		return nil
	}
	info := toTypeInfo(*t)
	return &info
}

// ────────────────────────────────
// CATEGORIES
// ────────────────────────────────

// MonitoringCategoryInfo is the public category payload.
type MonitoringCategoryInfo struct {
	ID   int    `json:"id"`
	Name string `json:"name"`
	Kind string `json:"kind"`
}

// ListCategories returns categories, optionally filtered by kind.
func (s *MonitoringService) ListCategories(ctx context.Context, kind string) ([]MonitoringCategoryInfo, error) {
	if kind != "" && !validKind(kind) {
		return nil, fmt.Errorf("invalid category kind")
	}
	cats, err := s.repo.ListCategories(ctx, kind)
	if err != nil {
		return nil, err
	}
	info := make([]MonitoringCategoryInfo, len(cats))
	for i, c := range cats {
		info[i] = toCategoryInfo(c)
	}
	return info, nil
}

// CreateCategory creates a new category.
func (s *MonitoringService) CreateCategory(ctx context.Context, c repository.MonitoringCategory) (*MonitoringCategoryInfo, error) {
	if strings.TrimSpace(c.Name) == "" {
		return nil, fmt.Errorf("category name is required")
	}
	if !validKind(c.Kind) {
		return nil, fmt.Errorf("invalid category kind")
	}
	created, err := s.repo.CreateCategory(ctx, c)
	if err != nil {
		return nil, err
	}
	info := toCategoryInfo(*created)
	return &info, nil
}

// UpdateCategory updates an existing category.
func (s *MonitoringService) UpdateCategory(ctx context.Context, id int, c repository.MonitoringCategory) (*MonitoringCategoryInfo, error) {
	if strings.TrimSpace(c.Name) == "" {
		return nil, fmt.Errorf("category name is required")
	}
	if !validKind(c.Kind) {
		return nil, fmt.Errorf("invalid category kind")
	}
	updated, err := s.repo.UpdateCategory(ctx, id, c)
	if err != nil {
		return nil, err
	}
	info := toCategoryInfo(*updated)
	return &info, nil
}

// DeleteCategory soft-deletes a category.
func (s *MonitoringService) DeleteCategory(ctx context.Context, id int) error {
	return s.repo.DeleteCategory(ctx, id)
}

func toCategoryInfo(c repository.MonitoringCategory) MonitoringCategoryInfo {
	return MonitoringCategoryInfo{
		ID:   c.ID,
		Name: c.Name,
		Kind: c.Kind,
	}
}

func validKind(kind string) bool {
	return kind == "application" || kind == "website" || kind == "both"
}

// ────────────────────────────────
// APPLICATIONS
// ────────────────────────────────

// MonitoredAppInfo is the public app-catalog payload.
type MonitoredAppInfo struct {
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

// ListApps returns the paginated detected-app catalog with classification.
func (s *MonitoringService) ListApps(ctx context.Context, params repository.MonitoredAppListParams) (*MonitoredAppListResult, error) {
	result, err := s.repo.ListApps(ctx, params)
	if err != nil {
		return nil, err
	}
	return &MonitoredAppListResult{
		Data:       toAppInfos(result.Apps),
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}

// UpdateAppClassification assigns/clears an app's type and category.
func (s *MonitoringService) UpdateAppClassification(ctx context.Context, id string, typeID, categoryID *int) error {
	if err := s.validateClassificationRefs(ctx, typeID, categoryID); err != nil {
		return err
	}
	return s.repo.UpdateAppClassification(ctx, id, typeID, categoryID)
}

func toAppInfos(apps []repository.MonitoredApp) []MonitoredAppInfo {
	info := make([]MonitoredAppInfo, len(apps))
	for i, a := range apps {
		info[i] = MonitoredAppInfo{
			ID:           a.ID,
			AppName:      a.AppName,
			BinaryName:   a.BinaryName,
			Categories:   a.Categories,
			IsBrowser:    a.IsBrowser,
			TypeID:       a.TypeID,
			TypeName:     a.TypeName,
			TypeColor:    a.TypeColor,
			CategoryID:   a.CategoryID,
			CategoryName: a.CategoryName,
		}
	}
	return info
}

// ────────────────────────────────
// WEBSITES
// ────────────────────────────────

// MonitoredSiteInfo is the public site-registry payload.
type MonitoredSiteInfo struct {
	ID           int64  `json:"id"`
	Domain       string `json:"domain"`
	TypeID       *int   `json:"typeId,omitempty"`
	TypeName     string `json:"typeName"`
	TypeColor    string `json:"typeColor"`
	CategoryID   *int   `json:"categoryId,omitempty"`
	CategoryName string `json:"categoryName"`
}

// ListWebsites syncs newly-observed domains into the registry, then returns the
// paginated site list with classification.
func (s *MonitoringService) ListWebsites(ctx context.Context, params repository.MonitoredSiteListParams) (*MonitoredSiteListResult, error) {
	if _, err := s.repo.SyncWebsiteDomains(ctx); err != nil {
		return nil, err
	}
	result, err := s.repo.ListWebsites(ctx, params)
	if err != nil {
		return nil, err
	}
	return &MonitoredSiteListResult{
		Data:       toSiteInfos(result.Sites),
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}

// UpdateSiteClassification assigns/clears a site's type and category.
func (s *MonitoringService) UpdateSiteClassification(ctx context.Context, id int64, typeID, categoryID *int) error {
	if err := s.validateClassificationRefs(ctx, typeID, categoryID); err != nil {
		return err
	}
	return s.repo.UpdateSiteClassification(ctx, id, typeID, categoryID)
}

// CreateWebsite adds a new website to the monitoring registry with optional classification.
func (s *MonitoringService) CreateWebsite(ctx context.Context, domain string, typeID, categoryID *int) (*MonitoredSiteInfo, error) {
	domain = strings.TrimSpace(domain)
	if domain == "" {
		return nil, fmt.Errorf("domain is required")
	}
	if err := s.validateClassificationRefs(ctx, typeID, categoryID); err != nil {
		return nil, err
	}
	site, err := s.repo.CreateWebsite(ctx, domain, typeID, categoryID)
	if err != nil {
		return nil, err
	}
	return &MonitoredSiteInfo{
		ID:           site.ID,
		Domain:       site.Domain,
		TypeID:       site.TypeID,
		TypeName:     "",
		TypeColor:    "",
		CategoryID:   site.CategoryID,
		CategoryName: "",
	}, nil
}

func toSiteInfos(sites []repository.MonitoredSite) []MonitoredSiteInfo {
	info := make([]MonitoredSiteInfo, len(sites))
	for i, st := range sites {
		info[i] = MonitoredSiteInfo{
			ID:           st.ID,
			Domain:       st.Domain,
			TypeID:       st.TypeID,
			TypeName:     st.TypeName,
			TypeColor:    st.TypeColor,
			CategoryID:   st.CategoryID,
			CategoryName: st.CategoryName,
		}
	}
	return info
}

// validateClassificationRefs verifies that provided FK ids resolve to existing records.
func (s *MonitoringService) validateClassificationRefs(ctx context.Context, typeID, categoryID *int) error {
	if typeID != nil {
		if _, err := s.repo.GetTypeByID(ctx, *typeID); err != nil {
			return fmt.Errorf("invalid type: %w", err)
		}
	}
	if categoryID != nil {
		if _, err := s.repo.GetCategoryByID(ctx, *categoryID); err != nil {
			return fmt.Errorf("invalid category: %w", err)
		}
	}
	return nil
}

// ────────────────────────────────
// RESPONSES
// ────────────────────────────────

// MonitoredAppListResult is the paginated app-catalog response.
type MonitoredAppListResult struct {
	Data       []MonitoredAppInfo `json:"data"`
	Total      int                `json:"total"`
	Page       int                `json:"page"`
	PerPage    int                `json:"perPage"`
	TotalPages int                `json:"totalPages"`
}

// MonitoredSiteListResult is the paginated site-registry response.
type MonitoredSiteListResult struct {
	Data       []MonitoredSiteInfo `json:"data"`
	Total      int                 `json:"total"`
	Page       int                 `json:"page"`
	PerPage    int                 `json:"perPage"`
	TotalPages int                 `json:"totalPages"`
}