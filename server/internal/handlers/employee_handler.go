package handlers

import (
	"log"
	"net/http"
	"strconv"
	"strings"

	"github.com/labstack/echo/v4"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
)

// EmployeeHandler handles employee CRUD endpoints.
type EmployeeHandler struct {
	employeeService *services.EmployeeService
}

// NewEmployeeHandler creates a new EmployeeHandler.
func NewEmployeeHandler(employeeService *services.EmployeeService) *EmployeeHandler {
	return &EmployeeHandler{employeeService: employeeService}
}

// logAndReturnError logs an error and returns a JSON error response.
func (h *EmployeeHandler) logAndReturnError(c echo.Context, code int, message string, err error) error {
	log.Printf("[employee] %s: %v", message, err)
	return c.JSON(code, dto.APIError{
		Code:    code,
		Message: message,
		Detail:  err.Error(),
	})
}

// ListEmployees handles GET /api/v1/employees
func (h *EmployeeHandler) ListEmployees(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))

	params := repository.EmployeeListParams{
		Search:     c.QueryParam("search"),
		Department: c.QueryParam("department"),
		Status:     c.QueryParam("status"),
		Page:       page,
		PerPage:    perPage,
	}

	result, err := h.employeeService.List(c.Request().Context(), params)
	if err != nil {
		return h.logAndReturnError(c, http.StatusInternalServerError, "Failed to list employees", err)
	}

	return c.JSON(http.StatusOK, result)
}

// GetEmployee handles GET /api/v1/employees/:id
func (h *EmployeeHandler) GetEmployee(c echo.Context) error {
	id := c.Param("id")

	emp, err := h.employeeService.GetByID(c.Request().Context(), id)
	if err != nil {
		return h.logAndReturnError(c, http.StatusInternalServerError, "Failed to get employee", err)
	}
	if emp == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code:    http.StatusNotFound,
			Message: "Employee not found",
		})
	}

	return c.JSON(http.StatusOK, emp)
}

// CreateEmployee handles POST /api/v1/employees
func (h *EmployeeHandler) CreateEmployee(c echo.Context) error {
	var req dto.CreateEmployeeRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	if req.Name == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Name is required",
		})
	}

	emp, err := h.employeeService.Create(c.Request().Context(), &req)
	if err != nil {
		code := http.StatusInternalServerError
		if isDuplicateError(err) {
			code = http.StatusConflict
		}
		return h.logAndReturnError(c, code, "Failed to create employee", err)
	}

	return c.JSON(http.StatusCreated, emp)
}

// ImportEmployees handles POST /api/v1/employees/import
func (h *EmployeeHandler) ImportEmployees(c echo.Context) error {
	var req dto.ImportEmployeesRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	if len(req.Employees) == 0 {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "No employee rows to import",
		})
	}

	resp, err := h.employeeService.ImportEmployees(c.Request().Context(), &req)
	if err != nil {
		return h.logAndReturnError(c, http.StatusInternalServerError, "Failed to import employees", err)
	}

	return c.JSON(http.StatusOK, resp)
}

// ExportEmployees handles GET /api/v1/employees/export
func (h *EmployeeHandler) ExportEmployees(c echo.Context) error {
	rows, err := h.employeeService.ExportEmployees(c.Request().Context())
	if err != nil {
		return h.logAndReturnError(c, http.StatusInternalServerError, "Failed to export employees", err)
	}
	return c.JSON(http.StatusOK, rows)
}

// UpdateEmployee handles PUT /api/v1/employees/:id
func (h *EmployeeHandler) UpdateEmployee(c echo.Context) error {
	id := c.Param("id")

	var req dto.UpdateEmployeeRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	emp, err := h.employeeService.Update(c.Request().Context(), id, &req)
	if err != nil {
		code := http.StatusInternalServerError
		if isDuplicateError(err) {
			code = http.StatusConflict
		}
		return h.logAndReturnError(c, code, "Failed to update employee", err)
	}
	if emp == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code:    http.StatusNotFound,
			Message: "Employee not found",
		})
	}

	return c.JSON(http.StatusOK, emp)
}

// DeleteEmployee handles DELETE /api/v1/employees/:id
func (h *EmployeeHandler) DeleteEmployee(c echo.Context) error {
	id := c.Param("id")

	if err := h.employeeService.Delete(c.Request().Context(), id); err != nil {
		code := http.StatusInternalServerError
		if err.Error() == "employee not found" {
			code = http.StatusNotFound
		}
		return h.logAndReturnError(c, code, "Failed to delete employee", err)
	}

	return c.JSON(http.StatusOK, map[string]interface{}{
		"message": "employee deleted successfully",
	})
}

// GenerateSecret handles POST /api/v1/employees/:id/generate-secret
func (h *EmployeeHandler) GenerateSecret(c echo.Context) error {
	id := c.Param("id")

	resp, err := h.employeeService.GenerateSecret(c.Request().Context(), id)
	if err != nil {
		code := http.StatusInternalServerError
		if err.Error() == "employee not found" {
			code = http.StatusNotFound
		}
		return h.logAndReturnError(c, code, "Failed to generate secret", err)
	}

	return c.JSON(http.StatusOK, resp)
}

// isDuplicateError checks if the error is a duplicate key violation.
func isDuplicateError(err error) bool {
	return err != nil && (strings.Contains(err.Error(), "duplicate") || strings.Contains(err.Error(), "already exists"))
}
