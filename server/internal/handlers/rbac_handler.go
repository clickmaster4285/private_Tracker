package handlers

import (
	"net/http"
	"strconv"

	"github.com/labstack/echo/v4"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/services"
)

// RBACHandler handles role and module-catalog endpoints.
type RBACHandler struct {
	rbacService *services.RBACService
}

// NewRBACHandler creates a new RBACHandler.
func NewRBACHandler(rbacService *services.RBACService) *RBACHandler {
	return &RBACHandler{rbacService: rbacService}
}

// ListModules handles GET /api/v1/modules — the full module/submodule catalog.
func (h *RBACHandler) ListModules(c echo.Context) error {
	tree, err := h.rbacService.ModuleTree(c.Request().Context())
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list modules",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, tree)
}

// ListRoles handles GET /api/v1/roles
func (h *RBACHandler) ListRoles(c echo.Context) error {
	result, err := h.rbacService.List(c.Request().Context())
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list roles",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, result)
}

// CreateRole handles POST /api/v1/roles
func (h *RBACHandler) CreateRole(c echo.Context) error {
	var req dto.CreateRoleRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}
	if req.Name == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Role name is required",
		})
	}

	role, err := h.rbacService.Create(c.Request().Context(), &req)
	if err != nil {
		code := http.StatusInternalServerError
		if err.Error() == "role name already exists" {
			code = http.StatusConflict
		}
		return c.JSON(code, dto.APIError{
			Code:    code,
			Message: err.Error(),
		})
	}

	return c.JSON(http.StatusCreated, role)
}

// UpdateRole handles PUT /api/v1/roles/:id
func (h *RBACHandler) UpdateRole(c echo.Context) error {
	id, err := strconv.Atoi(c.Param("id"))
	if err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid role id",
		})
	}

	var req dto.UpdateRoleRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	role, err := h.rbacService.Update(c.Request().Context(), id, &req)
	if err != nil {
		code := http.StatusInternalServerError
		switch err.Error() {
		case "role not found":
			code = http.StatusNotFound
		case "system role cannot be modified":
			code = http.StatusForbidden
		case "role name already exists":
			code = http.StatusConflict
		}
		return c.JSON(code, dto.APIError{
			Code:    code,
			Message: err.Error(),
		})
	}

	return c.JSON(http.StatusOK, role)
}

// DeleteRole handles DELETE /api/v1/roles/:id
func (h *RBACHandler) DeleteRole(c echo.Context) error {
	id, err := strconv.Atoi(c.Param("id"))
	if err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid role id",
		})
	}

	if err := h.rbacService.Delete(c.Request().Context(), id); err != nil {
		code := http.StatusInternalServerError
		switch err.Error() {
		case "role not found":
			code = http.StatusNotFound
		case "system role cannot be deleted", "role is still assigned to users":
			code = http.StatusConflict
		}
		return c.JSON(code, dto.APIError{
			Code:    code,
			Message: err.Error(),
		})
	}

	return c.JSON(http.StatusOK, map[string]interface{}{
		"message": "role deleted successfully",
	})
}
