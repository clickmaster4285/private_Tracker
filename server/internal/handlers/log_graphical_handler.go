package handlers

import (
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

// LogGraphicalHandler serves GET /api/v1/logs/graphical (JWTAuth).
type LogGraphicalHandler struct {
	service *services.LogGraphicalService
}

// NewLogGraphicalHandler constructs a LogGraphicalHandler.
func NewLogGraphicalHandler(service *services.LogGraphicalService) *LogGraphicalHandler {
	return &LogGraphicalHandler{service: service}
}

// GetGraphical handles GET /api/v1/logs/graphical.
func (h *LogGraphicalHandler) GetGraphical(c echo.Context) error {
	from := parseDashboardTime(c.QueryParam("from"))
	to := parseDashboardTime(c.QueryParam("to"))
	if from.IsZero() || to.IsZero() {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "from and to are required (RFC3339 or YYYY-MM-DD)",
		})
	}
	if isDateOnly(c.QueryParam("to")) {
		to = to.Add(24 * time.Hour)
	}

	var departmentID *int
	if raw := c.QueryParam("departmentId"); raw != "" {
		id, err := strconv.Atoi(raw)
		if err != nil || id < 1 {
			return c.JSON(http.StatusBadRequest, dto.APIError{
				Code:    http.StatusBadRequest,
				Message: "invalid departmentId",
			})
		}
		departmentID = &id
	}

	topN, _ := strconv.Atoi(c.QueryParam("topN"))
	employeeID := strings.TrimSpace(c.QueryParam("employeeId"))

	resp, err := h.service.GetGraphical(c.Request().Context(), services.LogGraphicalParams{
		From:         from,
		To:           to,
		DepartmentID: departmentID,
		EmployeeID:   employeeID,
		TopN:         topN,
	})
	if err != nil {
		msg := err.Error()
		if msg == "from and to are required" || msg == "to must be after from" {
			return c.JSON(http.StatusBadRequest, dto.APIError{
				Code: http.StatusBadRequest, Message: msg,
			})
		}
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to load graphical logs",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}
