package handlers

import (
	"net/http"
	"strconv"

	"github.com/labstack/echo/v4"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/services"
)

// DepartmentHandler handles department CRUD endpoints.
type DepartmentHandler struct {
	departmentService *services.DepartmentService
}

// NewDepartmentHandler creates a new DepartmentHandler.
func NewDepartmentHandler(departmentService *services.DepartmentService) *DepartmentHandler {
	return &DepartmentHandler{departmentService: departmentService}
}

// ListDepartments handles GET /api/v1/departments
func (h *DepartmentHandler) ListDepartments(c echo.Context) error {
	result, err := h.departmentService.List(c.Request().Context())
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list departments",
			Detail:  err.Error(),
		})
	}

	return c.JSON(http.StatusOK, result)
}

// CreateDepartment handles POST /api/v1/departments
func (h *DepartmentHandler) CreateDepartment(c echo.Context) error {
	var req struct {
		Name string `json:"name"`
	}
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	if req.Name == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Department name is required",
		})
	}

	dept, err := h.departmentService.Create(c.Request().Context(), req.Name)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to create department",
			Detail:  err.Error(),
		})
	}

	return c.JSON(http.StatusCreated, dept)
}

// UpdateDepartment handles PUT /api/v1/departments/:id
func (h *DepartmentHandler) UpdateDepartment(c echo.Context) error {
	id, err := strconv.Atoi(c.Param("id"))
	if err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid department ID",
		})
	}

	var req struct {
		Name string `json:"name"`
	}
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	if req.Name == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Department name is required",
		})
	}

	dept, err := h.departmentService.Update(c.Request().Context(), id, req.Name)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to update department",
			Detail:  err.Error(),
		})
	}

	return c.JSON(http.StatusOK, dept)
}

// DeleteDepartment handles DELETE /api/v1/departments/:id
func (h *DepartmentHandler) DeleteDepartment(c echo.Context) error {
	id, err := strconv.Atoi(c.Param("id"))
	if err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid department ID",
		})
	}

	if err := h.departmentService.Delete(c.Request().Context(), id); err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to delete department",
			Detail:  err.Error(),
		})
	}

	return c.JSON(http.StatusOK, map[string]interface{}{
		"message": "department deleted successfully",
	})
}
