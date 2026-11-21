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

type TimeAttendanceHandler struct {
	service *services.TimeAttendanceService
}

func NewTimeAttendanceHandler(service *services.TimeAttendanceService) *TimeAttendanceHandler {
	return &TimeAttendanceHandler{service: service}
}

func (h *TimeAttendanceHandler) GetMySchedule(c echo.Context) error {
	employeeID, ok := c.Get("employee_id").(string)
	if !ok || strings.TrimSpace(employeeID) == "" {
		return apiError(c, http.StatusUnauthorized, "Authenticated employee identity missing", "")
	}
	response, err := h.service.GetSchedule(c.Request().Context(), employeeID)
	if err != nil {
		return apiError(c, http.StatusInternalServerError, "Failed to load schedule", err.Error())
	}
	if response == nil {
		return apiError(c, http.StatusNotFound, "No active schedule assigned", "")
	}
	return c.JSON(http.StatusOK, response)
}

func (h *TimeAttendanceHandler) ServerTime(c echo.Context) error {
	now := time.Now().UTC()
	c.Response().Header().Set("Date", now.Format(http.TimeFormat))
	return c.JSON(http.StatusOK, map[string]string{"nowUtc": now.Format(time.RFC3339Nano)})
}

func (h *TimeAttendanceHandler) ListHolidays(c echo.Context) error {
	rows, err := h.service.ListHolidays(c.Request().Context())
	if err != nil {
		return apiError(c, http.StatusInternalServerError, "Failed to list holidays", err.Error())
	}
	return c.JSON(http.StatusOK, map[string]interface{}{"data": rows, "total": len(rows)})
}

func (h *TimeAttendanceHandler) CreateHoliday(c echo.Context) error {
	var input dto.HolidayInput
	if err := c.Bind(&input); err != nil {
		return apiError(c, http.StatusBadRequest, "Invalid request body", "")
	}
	row, err := h.service.CreateHoliday(c.Request().Context(), input)
	if err != nil {
		status := http.StatusBadRequest
		if strings.Contains(strings.ToLower(err.Error()), "unique") {
			status = http.StatusConflict
		}
		return apiError(c, status, "Failed to create holiday", err.Error())
	}
	return c.JSON(http.StatusCreated, row)
}

func (h *TimeAttendanceHandler) UpdateHoliday(c echo.Context) error {
	id, err := strconv.Atoi(c.Param("id"))
	if err != nil {
		return apiError(c, http.StatusBadRequest, "Invalid holiday ID", "")
	}
	var input dto.HolidayInput
	if err := c.Bind(&input); err != nil {
		return apiError(c, http.StatusBadRequest, "Invalid request body", "")
	}
	row, err := h.service.UpdateHoliday(c.Request().Context(), id, input)
	if err != nil {
		return apiError(c, http.StatusBadRequest, "Failed to update holiday", err.Error())
	}
	if row == nil {
		return apiError(c, http.StatusNotFound, "Holiday not found", "")
	}
	return c.JSON(http.StatusOK, row)
}

func (h *TimeAttendanceHandler) DeleteHoliday(c echo.Context) error {
	id, err := strconv.Atoi(c.Param("id"))
	if err != nil {
		return apiError(c, http.StatusBadRequest, "Invalid holiday ID", "")
	}
	deleted, err := h.service.DeleteHoliday(c.Request().Context(), id)
	if err != nil {
		return apiError(c, http.StatusInternalServerError, "Failed to delete holiday", err.Error())
	}
	if !deleted {
		return apiError(c, http.StatusNotFound, "Holiday not found", "")
	}
	return c.JSON(http.StatusOK, map[string]string{"message": "holiday deleted successfully"})
}

func (h *TimeAttendanceHandler) GetToday(c echo.Context) error {
	employeeID := strings.TrimSpace(c.QueryParam("employeeId"))
	if employeeID == "" {
		return apiError(c, http.StatusBadRequest, "employeeId is required", "")
	}
	result, err := h.service.AttendanceToday(c.Request().Context(), employeeID)
	if err != nil {
		return apiError(c, http.StatusBadRequest, "Failed to calculate attendance", err.Error())
	}
	return c.JSON(http.StatusOK, result)
}

func (h *TimeAttendanceHandler) GetDayEvents(c echo.Context) error {
	employeeID := strings.TrimSpace(c.QueryParam("employeeId"))
	if employeeID == "" {
		return apiError(c, http.StatusBadRequest, "employeeId is required", "")
	}
	date := strings.TrimSpace(c.QueryParam("date"))
	if date == "" {
		return apiError(c, http.StatusBadRequest, "date is required", "")
	}
	result, err := h.service.DaySessionEvents(c.Request().Context(), employeeID, date)
	if err != nil {
		return apiError(c, http.StatusBadRequest, "Failed to load day events", err.Error())
	}
	return c.JSON(http.StatusOK, result)
}

func (h *TimeAttendanceHandler) GetRange(c echo.Context) error {
	employeeID := strings.TrimSpace(c.QueryParam("employeeId"))
	if employeeID == "" {
		return apiError(c, http.StatusBadRequest, "employeeId is required", "")
	}
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	result, err := h.service.AttendanceRange(
		c.Request().Context(), employeeID,
		c.QueryParam("from"), c.QueryParam("to"), page, perPage,
	)
	if err != nil {
		return apiError(c, http.StatusBadRequest, "Failed to calculate attendance", err.Error())
	}
	return c.JSON(http.StatusOK, result)
}

// ListShiftExtensions — GET /shift-extensions?employeeId=EMP-… (current + upcoming grants).
func (h *TimeAttendanceHandler) ListShiftExtensions(c echo.Context) error {
	out, err := h.service.ListShiftExtensions(c.Request().Context(), c.QueryParam("employeeId"))
	if err != nil {
		return apiError(c, http.StatusBadRequest, "Failed to load extra time", err.Error())
	}
	return c.JSON(http.StatusOK, out)
}

// CreateShiftExtension — POST /shift-extensions ("Give extra time").
func (h *TimeAttendanceHandler) CreateShiftExtension(c echo.Context) error {
	var input dto.ShiftExtensionInput
	if err := c.Bind(&input); err != nil {
		return apiError(c, http.StatusBadRequest, "Invalid request body", "")
	}
	createdBy, _ := c.Get("user_id").(string)
	out, err := h.service.CreateShiftExtension(c.Request().Context(), input, createdBy)
	if err != nil {
		status := http.StatusBadRequest
		if strings.Contains(err.Error(), "foreign key") || strings.Contains(err.Error(), "23503") {
			return apiError(c, status, "Employee not found", "")
		}
		return apiError(c, status, "Failed to give extra time", err.Error())
	}
	return c.JSON(http.StatusCreated, out)
}

// DeleteShiftExtension — DELETE /shift-extensions/:id.
func (h *TimeAttendanceHandler) DeleteShiftExtension(c echo.Context) error {
	id, err := strconv.ParseInt(c.Param("id"), 10, 64)
	if err != nil {
		return apiError(c, http.StatusBadRequest, "Invalid extra time ID", "")
	}
	ok, err := h.service.DeleteShiftExtension(c.Request().Context(), id)
	if err != nil {
		return apiError(c, http.StatusInternalServerError, "Failed to remove extra time", err.Error())
	}
	if !ok {
		return apiError(c, http.StatusNotFound, "Extra time not found", "")
	}
	return c.JSON(http.StatusOK, map[string]string{"message": "removed"})
}

func apiError(c echo.Context, status int, message, detail string) error {
	return c.JSON(status, dto.APIError{Code: status, Message: message, Detail: detail})
}
