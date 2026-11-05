package handlers

import (
	"net/http"
	"strconv"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

// DashboardHandler serves GET /api/v1/dashboard/summary (JWTAuth).
type DashboardHandler struct {
	service *services.DashboardService
}

// NewDashboardHandler constructs a DashboardHandler.
func NewDashboardHandler(service *services.DashboardService) *DashboardHandler {
	return &DashboardHandler{service: service}
}

// GetSummary handles GET /api/v1/dashboard/summary.
// Query: from, to (required; exclusive end), optional departmentId, topN, recentLimit.
func (h *DashboardHandler) GetSummary(c echo.Context) error {
	from := parseDashboardTime(c.QueryParam("from"))
	to := parseDashboardTime(c.QueryParam("to"))
	if from.IsZero() || to.IsZero() {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "from and to are required (RFC3339 or YYYY-MM-DD)",
		})
	}
	// Date-only `to` means end of that calendar day exclusive → next midnight.
	// If the client already sent an exclusive instant (RFC3339 with time), use as-is.
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
	recentLimit, _ := strconv.Atoi(c.QueryParam("recentLimit"))

	resp, err := h.service.GetSummary(c.Request().Context(), services.SummaryParams{
		From:         from,
		To:           to,
		DepartmentID: departmentID,
		TopN:         topN,
		RecentLimit:  recentLimit,
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
			Message: "Failed to load dashboard summary",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}

func parseDashboardTime(v string) time.Time {
	if v == "" {
		return time.Time{}
	}
	for _, layout := range []string{time.RFC3339Nano, time.RFC3339, "2006-01-02"} {
		if t, err := time.Parse(layout, v); err == nil {
			return t
		}
	}
	return time.Time{}
}

func isDateOnly(v string) bool {
	if len(v) != 10 {
		return false
	}
	_, err := time.Parse("2006-01-02", v)
	return err == nil
}
