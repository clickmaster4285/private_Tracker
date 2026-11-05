package handlers

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

// MonitoringHandler handles the monitoring configuration domain:
// types, categories, and app/site classification endpoints.
type MonitoringHandler struct {
	monitoringService *services.MonitoringService
}

// NewMonitoringHandler creates a new MonitoringHandler.
func NewMonitoringHandler(monitoringService *services.MonitoringService) *MonitoringHandler {
	return &MonitoringHandler{monitoringService: monitoringService}
}

func errorResponse(c echo.Context, status int, message string, err error) error {
	detail := ""
	if err != nil {
		detail = err.Error()
	}
	return c.JSON(status, dto.APIError{
		Code:    status,
		Message: message,
		Detail:  detail,
	})
}

func parseID(c echo.Context) (int, error) {
	return strconv.Atoi(c.Param("id"))
}

// ────────────────────────────────
// TYPES
// ────────────────────────────────

// ListTypes handles GET /api/v1/monitoring/types
func (h *MonitoringHandler) ListTypes(c echo.Context) error {
	types, err := h.monitoringService.ListTypes(c.Request().Context())
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list types", err)
	}
	return c.JSON(http.StatusOK, map[string]interface{}{"types": types, "total": len(types)})
}

// CreateType handles POST /api/v1/monitoring/types
func (h *MonitoringHandler) CreateType(c echo.Context) error {
	var req repository.MonitoringType
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	created, err := h.monitoringService.CreateType(c.Request().Context(), req)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to create type", err)
	}
	return c.JSON(http.StatusCreated, created)
}

// UpdateType handles PUT /api/v1/monitoring/types/:id
func (h *MonitoringHandler) UpdateType(c echo.Context) error {
	id, err := parseID(c)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid type ID", err)
	}
	var req repository.MonitoringType
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	updated, err := h.monitoringService.UpdateType(c.Request().Context(), id, req)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to update type", err)
	}
	return c.JSON(http.StatusOK, updated)
}

// DeleteType handles DELETE /api/v1/monitoring/types/:id
func (h *MonitoringHandler) DeleteType(c echo.Context) error {
	id, err := parseID(c)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid type ID", err)
	}
	if err := h.monitoringService.DeleteType(c.Request().Context(), id); err != nil {
		if strings.HasPrefix(err.Error(), "type is assigned") {
			return errorResponse(c, http.StatusConflict, "Failed to delete type", err)
		}
		if err.Error() == "monitoring type not found" {
			return errorResponse(c, http.StatusNotFound, "Failed to delete type", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to delete type", err)
	}
	return c.JSON(http.StatusOK, map[string]interface{}{"message": "type deleted successfully"})
}

// ────────────────────────────────
// CATEGORIES
// ────────────────────────────────

// ListCategories handles GET /api/v1/monitoring/categories?kind=
func (h *MonitoringHandler) ListCategories(c echo.Context) error {
	kind := c.QueryParam("kind")
	categories, err := h.monitoringService.ListCategories(c.Request().Context(), kind)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to list categories", err)
	}
	return c.JSON(http.StatusOK, map[string]interface{}{"categories": categories, "total": len(categories)})
}

// CreateCategory handles POST /api/v1/monitoring/categories
func (h *MonitoringHandler) CreateCategory(c echo.Context) error {
	var req repository.MonitoringCategory
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	created, err := h.monitoringService.CreateCategory(c.Request().Context(), req)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to create category", err)
	}
	return c.JSON(http.StatusCreated, created)
}

// UpdateCategory handles PUT /api/v1/monitoring/categories/:id
func (h *MonitoringHandler) UpdateCategory(c echo.Context) error {
	id, err := parseID(c)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid category ID", err)
	}
	var req repository.MonitoringCategory
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	updated, err := h.monitoringService.UpdateCategory(c.Request().Context(), id, req)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to update category", err)
	}
	return c.JSON(http.StatusOK, updated)
}

// DeleteCategory handles DELETE /api/v1/monitoring/categories/:id
func (h *MonitoringHandler) DeleteCategory(c echo.Context) error {
	id, err := parseID(c)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid category ID", err)
	}
	if err := h.monitoringService.DeleteCategory(c.Request().Context(), id); err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to delete category", err)
	}
	return c.JSON(http.StatusOK, map[string]interface{}{"message": "category deleted successfully"})
}

// ────────────────────────────────
// APPLICATIONS
// ────────────────────────────────

// ListApps handles GET /api/v1/monitoring/apps
func (h *MonitoringHandler) ListApps(c echo.Context) error {
	params, err := parseListParams(c)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid list parameters", err)
	}
	result, err := h.monitoringService.ListApps(c.Request().Context(), params)
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list applications", err)
	}
	return c.JSON(http.StatusOK, result)
}

// UpdateAppClassification handles PATCH /api/v1/monitoring/apps/:id
func (h *MonitoringHandler) UpdateAppClassification(c echo.Context) error {
	id := c.Param("id")
	if id == "" {
		return errorResponse(c, http.StatusBadRequest, "Invalid application ID", nil)
	}
	typeID, categoryID, err := parseClassificationBody(c)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	if err := h.monitoringService.UpdateAppClassification(c.Request().Context(), id, typeID, categoryID); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to update application classification", err)
	}
	return c.JSON(http.StatusOK, map[string]interface{}{"message": "application classification updated"})
}

// ────────────────────────────────
// WEBSITES
// ────────────────────────────────

// ListWebsites handles GET /api/v1/monitoring/websites
func (h *MonitoringHandler) ListWebsites(c echo.Context) error {
	params, err := parseListParams(c)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid list parameters", err)
	}
	siteParams := repository.MonitoredSiteListParams{
		Search:       params.Search,
		TypeID:       params.TypeID,
		CategoryID:   params.CategoryID,
		Unclassified: params.Unclassified,
		Page:         params.Page,
		PerPage:      params.PerPage,
	}
	result, err := h.monitoringService.ListWebsites(c.Request().Context(), siteParams)
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list websites", err)
	}
	return c.JSON(http.StatusOK, result)
}

// UpdateSiteClassification handles PATCH /api/v1/monitoring/websites/:id
func (h *MonitoringHandler) UpdateSiteClassification(c echo.Context) error {
	id, err := strconv.ParseInt(c.Param("id"), 10, 64)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid website ID", err)
	}
	typeID, categoryID, err := parseClassificationBody(c)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	if err := h.monitoringService.UpdateSiteClassification(c.Request().Context(), id, typeID, categoryID); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to update website classification", err)
	}
	return c.JSON(http.StatusOK, map[string]interface{}{"message": "website classification updated"})
}

// CreateWebsite handles POST /api/v1/monitoring/websites
func (h *MonitoringHandler) CreateWebsite(c echo.Context) error {
	var req struct {
		Domain     string `json:"domain"`
		TypeID     *int   `json:"typeId"`
		CategoryID *int   `json:"categoryId"`
	}
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	domain := strings.TrimSpace(req.Domain)
	if domain == "" {
		return errorResponse(c, http.StatusBadRequest, "Domain is required", nil)
	}
	// Normalize domain: strip protocol, path, query, fragment, and lowercase.
	domain = normalizeDomain(domain)
	if domain == "" {
		return errorResponse(c, http.StatusBadRequest, "Invalid domain", nil)
	}
	site, err := h.monitoringService.CreateWebsite(c.Request().Context(), domain, req.TypeID, req.CategoryID)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to create website", err)
	}
	return c.JSON(http.StatusOK, site)
}

// normalizeDomain strips protocol, path, query, and fragment from a URL/domain
// and lowercases the result. E.g. "HTTPS://WWW.Example.COM/path" → "example.com".
func normalizeDomain(raw string) string {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return ""
	}
	// Strip protocol if present.
	raw = strings.TrimPrefix(raw, "https://")
	raw = strings.TrimPrefix(raw, "http://")
	raw = strings.TrimPrefix(raw, "//")
	// Strip everything after the first slash (path/query/fragment).
	if idx := strings.IndexAny(raw, "/?#"); idx >= 0 {
		raw = raw[:idx]
	}
	// Remove www. prefix for consistency.
	raw = strings.TrimPrefix(raw, "www.")
	return strings.ToLower(strings.TrimSpace(raw))
}

// parseListParams reads the shared search/type/category/unclassified/page/perPage
// query parameters used by both app and website listings.
func parseListParams(c echo.Context) (repository.MonitoredAppListParams, error) {
	params := repository.MonitoredAppListParams{
		Search: c.QueryParam("search"),
	}
	if v := c.QueryParam("typeId"); v != "" {
		id, err := strconv.Atoi(v)
		if err != nil {
			return params, err
		}
		params.TypeID = id
	}
	if v := c.QueryParam("categoryId"); v != "" {
		id, err := strconv.Atoi(v)
		if err != nil {
			return params, err
		}
		params.CategoryID = id
	}
	params.Unclassified = c.QueryParam("unclassified") == "true"
	if v := c.QueryParam("page"); v != "" {
		page, err := strconv.Atoi(v)
		if err != nil {
			return params, err
		}
		params.Page = page
	}
	if v := c.QueryParam("perPage"); v != "" {
		perPage, err := strconv.Atoi(v)
		if err != nil {
			return params, err
		}
		params.PerPage = perPage
	}
	return params, nil
}

// parseClassificationBody reads {"typeId": n|null, "categoryId": n|null} preserving
// presence: a key present with a number sets the FK, a key present with null clears
// it, and an absent key leaves the current value untouched.
func parseClassificationBody(c echo.Context) (typeID, categoryID *int, err error) {
	var req map[string]*int
	if err := c.Bind(&req); err != nil {
		return nil, nil, err
	}
	if v, ok := req["typeId"]; ok {
		typeID = v
	}
	if v, ok := req["categoryId"]; ok {
		categoryID = v
	}
	if typeID == nil && categoryID == nil {
		// Distinguish "both fields absent" (no-op) from "both fields null".
		if _, okType := req["typeId"]; !okType {
			if _, okCat := req["categoryId"]; !okCat {
				return nil, nil, nil
			}
		}
	}
	return typeID, categoryID, nil
}