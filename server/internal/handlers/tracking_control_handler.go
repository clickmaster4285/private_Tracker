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

// TrackingControlHandler serves DeviceAuth control poll + tracking events + HR mode PATCH.
type TrackingControlHandler struct {
	svc          *services.TrackingControlService
	employeeRepo *repository.EmployeeRepo
}

func NewTrackingControlHandler(svc *services.TrackingControlService, employeeRepo *repository.EmployeeRepo) *TrackingControlHandler {
	return &TrackingControlHandler{svc: svc, employeeRepo: employeeRepo}
}

// GetControl handles GET /api/v1/devices/control?sinceEpoch=
func (h *TrackingControlHandler) GetControl(c echo.Context) error {
	employeeID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	machineID := getAuthenticatedMachineID(c)
	since := int64(0)
	if v := c.QueryParam("sinceEpoch"); v != "" {
		n, err := strconv.ParseInt(v, 10, 64)
		if err != nil {
			return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid sinceEpoch"})
		}
		since = n
	}
	resp, err := h.svc.GetControl(c.Request().Context(), employeeID, machineID, since)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to load device control", Detail: err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}

// SyncTrackingEvents handles POST /api/v1/devices/tracking-events
func (h *TrackingControlHandler) SyncTrackingEvents(c echo.Context) error {
	employeeID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	machineID := getAuthenticatedMachineID(c)
	var req dto.SyncTrackingEventsRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	if len(req.Events) == 0 {
		return c.JSON(http.StatusOK, dto.SyncTrackingEventsResponse{AcceptedIDs: []string{}})
	}
	resp, err := h.svc.IngestTrackingEvents(c.Request().Context(), employeeID, machineID, req)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to sync tracking events", Detail: err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}

// ListRoamingMachines handles GET /api/v1/device-configuration/roaming-machines (JWT).
func (h *TrackingControlHandler) ListRoamingMachines(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	resp, err := h.svc.ListRoamingMachines(c.Request().Context(), c.QueryParam("search"), page, perPage)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to list roaming machines", Detail: err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}

// ListTrackingEventLogs handles GET /api/v1/device-configuration/tracking-events (JWT).
func (h *TrackingControlHandler) ListTrackingEventLogs(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	resp, err := h.svc.ListTrackingEventLogs(
		c.Request().Context(),
		c.QueryParam("search"),
		c.QueryParam("employeeId"),
		c.QueryParam("machineId"),
		c.QueryParam("eventType"),
		page, perPage,
	)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to list tracking events", Detail: err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}

// GetTrackerModeTypes handles GET /api/v1/device-configuration/mode-types (JWT).
func (h *TrackingControlHandler) GetTrackerModeTypes(c echo.Context) error {
	resp, err := h.svc.GetTrackerModeTypes(c.Request().Context())
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to load tracker mode types", Detail: err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}

// SetMachineTrackingMode handles PATCH /api/v1/employees/:id/machines/:machineId/tracking-mode (JWT).
// :id is the employee UUID (same as other employee admin routes).
func (h *TrackingControlHandler) SetMachineTrackingMode(c echo.Context) error {
	emp, err := h.employeeRepo.GetByID(c.Request().Context(), c.Param("id"))
	if err != nil || emp == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{Code: http.StatusNotFound, Message: "Employee not found"})
	}
	machineID := strings.TrimSpace(c.Param("machineId"))
	if machineID == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "machineId required"})
	}

	var req dto.SetMachineTrackingModeRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}

	resp, err := h.svc.SetMachineTrackingMode(c.Request().Context(), emp.EmployeeID, machineID, req.Mode)
	if err != nil {
		if strings.Contains(err.Error(), "invalid mode") {
			return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "mode must be fixed, roaming, or null"})
		}
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to update tracking mode", Detail: err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}
