package handlers

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/labstack/echo/v4"
	"github.com/alpha-ai-tracker/server/internal/services"
)

// ShiftHandler exposes the shift catalog CRUD.
type ShiftHandler struct {
	shiftService *services.ShiftService
}

// NewShiftHandler creates a new ShiftHandler.
func NewShiftHandler(shiftService *services.ShiftService) *ShiftHandler {
	return &ShiftHandler{shiftService: shiftService}
}

// ListShifts handles GET /api/v1/shifts
func (h *ShiftHandler) ListShifts(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	search := c.QueryParam("search")

	result, err := h.shiftService.List(c.Request().Context(), services.ShiftListQuery{
		Search:  search,
		Page:    page,
		PerPage: perPage,
	})
	if err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]interface{}{
			"code":    http.StatusInternalServerError,
			"message": "Failed to list shifts",
			"detail":  err.Error(),
		})
	}
	// Project to the {data, total, page, perPage, totalPages} shape the web
	// list pages (and the Infinite-Scroll Rule helper) expect. Mirrors how
	// monitoring's ListApps returns the same shape.
	return c.JSON(http.StatusOK, map[string]interface{}{
		"data":       result.Shifts,
		"total":      result.Total,
		"page":       result.Page,
		"perPage":    result.PerPage,
		"totalPages": result.TotalPages,
	})
}

// ListAllShifts handles GET /api/v1/shifts/all — every non-deleted shift
// without pagination, for populating dropdowns (employee form, profile).
func (h *ShiftHandler) ListAllShifts(c echo.Context) error {
	shifts, err := h.shiftService.ListAll(c.Request().Context())
	if err != nil {
		return c.JSON(http.StatusInternalServerError, map[string]interface{}{
			"code":    http.StatusInternalServerError,
			"message": "Failed to list shifts",
			"detail":  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, map[string]interface{}{
		"shifts": shifts,
		"total":  len(shifts),
	})
}

// CreateShift handles POST /api/v1/shifts
func (h *ShiftHandler) CreateShift(c echo.Context) error {
	var req services.ShiftInput
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, map[string]interface{}{
			"code":    http.StatusBadRequest,
			"message": "Invalid request body",
		})
	}
	created, err := h.shiftService.Create(c.Request().Context(), &req)
	if err != nil {
		status := http.StatusBadRequest
		if strings.Contains(err.Error(), "duplicate") || strings.Contains(err.Error(), "unique") {
			status = http.StatusConflict
		}
		return c.JSON(status, map[string]interface{}{
			"code":    status,
			"message": "Failed to create shift",
			"detail":  err.Error(),
		})
	}
	return c.JSON(http.StatusCreated, created)
}

// UpdateShift handles PUT /api/v1/shifts/:id
func (h *ShiftHandler) UpdateShift(c echo.Context) error {
	id, err := strconv.Atoi(c.Param("id"))
	if err != nil {
		return c.JSON(http.StatusBadRequest, map[string]interface{}{
			"code":    http.StatusBadRequest,
			"message": "Invalid shift ID",
		})
	}
	var req services.ShiftInput
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, map[string]interface{}{
			"code":    http.StatusBadRequest,
			"message": "Invalid request body",
		})
	}
	updated, err := h.shiftService.Update(c.Request().Context(), id, &req)
	if err != nil {
		return c.JSON(http.StatusBadRequest, map[string]interface{}{
			"code":    http.StatusBadRequest,
			"message": "Failed to update shift",
			"detail":  err.Error(),
		})
	}
	if updated == nil {
		return c.JSON(http.StatusNotFound, map[string]interface{}{
			"code":    http.StatusNotFound,
			"message": "Shift not found",
		})
	}
	return c.JSON(http.StatusOK, updated)
}

// DeleteShift handles DELETE /api/v1/shifts/:id
func (h *ShiftHandler) DeleteShift(c echo.Context) error {
	id, err := strconv.Atoi(c.Param("id"))
	if err != nil {
		return c.JSON(http.StatusBadRequest, map[string]interface{}{
			"code":    http.StatusBadRequest,
			"message": "Invalid shift ID",
		})
	}
	if err := h.shiftService.Delete(c.Request().Context(), id); err != nil {
		status := http.StatusInternalServerError
		msg := "Failed to delete shift"
		switch {
		case err.Error() == "shift not found":
			status = http.StatusNotFound
			msg = "Shift not found"
		case strings.HasPrefix(err.Error(), "shift is assigned"):
			status = http.StatusConflict
			msg = err.Error()
		}
		return c.JSON(status, map[string]interface{}{
			"code":    status,
			"message": msg,
			"detail":  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, map[string]interface{}{
		"message": "shift deleted successfully",
	})
}
