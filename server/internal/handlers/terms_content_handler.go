package handlers

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/labstack/echo/v4"
)

// compareVersions returns -1, 0, or 1 comparing a to b.
// Versions are split on "." and compared numerically left-to-right.
// Non-numeric parts are compared lexicographically.
func compareVersions(a, b string) int {
	partsA := strings.Split(a, ".")
	partsB := strings.Split(b, ".")
	maxLen := len(partsA)
	if len(partsB) > maxLen {
		maxLen = len(partsB)
	}
	for i := 0; i < maxLen; i++ {
		var va, vb int
		if i < len(partsA) {
			va, _ = strconv.Atoi(partsA[i])
		}
		if i < len(partsB) {
			vb, _ = strconv.Atoi(partsB[i])
		}
		if va < vb {
			return -1
		}
		if va > vb {
			return 1
		}
	}
	return 0
}

type TermsContentHandler struct {
	repo *repository.TermsContentRepo
}

func NewTermsContentHandler(repo *repository.TermsContentRepo) *TermsContentHandler {
	return &TermsContentHandler{repo: repo}
}

type TermsContentResponse struct {
	ID           string `json:"id"`
	Heading      string `json:"heading"`
	Body         string `json:"body"`
	TermsVersion string `json:"termsVersion"`
	IsSystem     bool   `json:"isSystem"`
	FeatureID    string `json:"featureId,omitempty"`
	TermType     string `json:"termType"`
	IsActive     int    `json:"isActive"`
	SortOrder    int    `json:"sortOrder"`
	UpdatedAt    string `json:"updatedAt"`
	CreatedAt    string `json:"createdAt"`
}

type TermsContentListResponse struct {
	Items []TermsContentResponse `json:"items"`
}

type UpdateTermsContentRequest struct {
	Heading      string `json:"heading"`
	Body         string `json:"body"`
	TermsVersion string `json:"termsVersion"`
}

type CreateTermsContentRequest struct {
	Heading      string `json:"heading"`
	Body         string `json:"body"`
	TermsVersion string `json:"termsVersion"`
}

type UpdateActiveRequest struct {
	IsActive int `json:"isActive"`
}

func toResponse(item *repository.TermsContent) TermsContentResponse {
	return TermsContentResponse{
		ID:           item.ID,
		Heading:      item.Heading,
		Body:         item.Body,
		TermsVersion: item.TermsVersion,
		IsSystem:     item.IsSystem,
		FeatureID:    item.FeatureID,
		TermType:     item.TermType,
		IsActive:     item.IsActive,
		SortOrder:    item.SortOrder,
		UpdatedAt:    item.UpdatedAt.Format("2006-01-02T15:04:05Z"),
		CreatedAt:    item.CreatedAt.Format("2006-01-02T15:04:05Z"),
	}
}

// ListActiveTermsContent handles GET /api/v1/terms-content/active — the
// CLIENT-facing variant of the admin list. It lives under DeviceAuth (the
// employee-device auth surface, NOT JWTAuth/web-admin) and returns only active
// terms ordered by sort_order ASC, created_at ASC. See the Client-vs-Web API
// Auth Separation Rule in AGENTS.md §6.
func (h *TermsContentHandler) ListActiveTermsContent(c echo.Context) error {
	ctx := c.Request().Context()
	items, err := h.repo.ListActive(ctx)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	resp := make([]TermsContentResponse, len(items))
	for i, item := range items {
		resp[i] = toResponse(&item)
	}

	return c.JSON(http.StatusOK, TermsContentListResponse{Items: resp})
}

func (h *TermsContentHandler) ListTermsContent(c echo.Context) error {
	ctx := c.Request().Context()
	items, err := h.repo.ListAll(ctx)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	resp := make([]TermsContentResponse, len(items))
	for i, item := range items {
		resp[i] = toResponse(&item)
	}

	return c.JSON(http.StatusOK, TermsContentListResponse{Items: resp})
}

func (h *TermsContentHandler) GetTermsContent(c echo.Context) error {
	id := c.Param("id")
	ctx := c.Request().Context()

	item, err := h.repo.GetByID(ctx, id)
	if err != nil {
		return c.JSON(http.StatusNotFound, map[string]string{"error": "terms content not found"})
	}

	return c.JSON(http.StatusOK, toResponse(item))
}

func (h *TermsContentHandler) UpdateTermsContent(c echo.Context) error {
	id := c.Param("id")
	var req UpdateTermsContentRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "invalid request body"})
	}

	if strings.TrimSpace(req.Heading) == "" {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "heading is required"})
	}

	ctx := c.Request().Context()

	// Version upgrade validation: new version must be strictly greater than current.
	existing, err := h.repo.GetByID(ctx, id)
	if err != nil {
		return c.JSON(http.StatusNotFound, map[string]string{"error": "terms content not found"})
	}
	if req.TermsVersion != "" && existing.TermsVersion != "" {
		if compareVersions(req.TermsVersion, existing.TermsVersion) <= 0 {
			return c.JSON(http.StatusBadRequest, map[string]string{
				"message": "new version must be greater than current version (" + existing.TermsVersion + ")",
			})
		}
	}

	item, err := h.repo.Update(ctx, id, req.Heading, req.Body, req.TermsVersion)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	return c.JSON(http.StatusOK, toResponse(item))
}

func (h *TermsContentHandler) UpdateActive(c echo.Context) error {
	id := c.Param("id")
	var req UpdateActiveRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "invalid request body"})
	}

	if req.IsActive != 0 && req.IsActive != 1 {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "isActive must be 0 or 1"})
	}

	ctx := c.Request().Context()
	if err := h.repo.UpdateActive(ctx, id, req.IsActive); err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	return c.JSON(http.StatusOK, map[string]string{
		"message":  "updated",
		"isActive": strconv.Itoa(req.IsActive),
	})
}

func (h *TermsContentHandler) CreateTermsContent(c echo.Context) error {
	var req CreateTermsContentRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "invalid request body"})
	}

	if strings.TrimSpace(req.Heading) == "" {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "heading is required"})
	}

	if req.TermsVersion == "" {
		req.TermsVersion = "1.0"
	}

	ctx := c.Request().Context()
	maxOrder := h.repo.GetMaxSortOrder(ctx)

	item, err := h.repo.Create(ctx, req.Heading, req.Body, req.TermsVersion, maxOrder+1)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	return c.JSON(http.StatusCreated, toResponse(item))
}

func (h *TermsContentHandler) DeleteTermsContent(c echo.Context) error {
	id := c.Param("id")
	ctx := c.Request().Context()

	// Check if it's a system term — cannot delete
	item, err := h.repo.GetByID(ctx, id)
	if err != nil {
		return c.JSON(http.StatusNotFound, map[string]string{"error": "terms content not found"})
	}
	if item.IsSystem {
		return c.JSON(http.StatusForbidden, map[string]string{"error": "system terms cannot be deleted"})
	}

	if err := h.repo.Delete(ctx, id); err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": err.Error()})
	}

	return c.JSON(http.StatusOK, map[string]string{"message": "deleted"})
}
