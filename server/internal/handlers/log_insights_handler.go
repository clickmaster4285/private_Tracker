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

// LogInsightsHandler serves GET /api/v1/logs/insights (JWTAuth).
type LogInsightsHandler struct {
	service *services.LogInsightsService
}

// NewLogInsightsHandler constructs a LogInsightsHandler.
func NewLogInsightsHandler(service *services.LogInsightsService) *LogInsightsHandler {
	return &LogInsightsHandler{service: service}
}

// GetInsights handles GET /api/v1/logs/insights.
// Query: from, to (required), optional departmentId, employeeId, topN.
func (h *LogInsightsHandler) GetInsights(c echo.Context) error {
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

	resp, err := h.service.GetInsights(c.Request().Context(), services.LogInsightsParams{
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
			Message: "Failed to load log insights",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}
