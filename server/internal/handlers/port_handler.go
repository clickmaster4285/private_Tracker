package handlers

import (
	"errors"
	"log"
	"net/http"
	"strconv"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/jackc/pgx/v5"
	"github.com/labstack/echo/v4"
)

// PortHandler serves hardware/port inventory (DeviceAuth sync + JWT read) and
// device-control rule CRUD (JWT).
type PortHandler struct {
	svc *services.PortService
}

func NewPortHandler(svc *services.PortService) *PortHandler {
	return &PortHandler{svc: svc}
}

// SyncPorts handles POST /api/v1/hardware-ports/sync (DeviceAuth).
func (h *PortHandler) SyncPorts(c echo.Context) error {
	var req dto.SyncPortsRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.svc.SyncPorts(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[ports] SyncPorts error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ListPorts handles GET /api/v1/hardware-ports?employeeId=&machineId= (JWT).
func (h *PortHandler) ListPorts(c echo.Context) error {
	employeeID := strings.TrimSpace(c.QueryParam("employeeId"))
	if employeeID == "" {
		return errorResponse(c, http.StatusBadRequest, "employeeId is required", nil)
	}
	machineID := strings.TrimSpace(c.QueryParam("machineId"))
	resp, err := h.svc.ListPorts(c.Request().Context(), employeeID, machineID)
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list ports", err)
	}
	return c.JSON(http.StatusOK, resp)
}

func (h *PortHandler) userID(c echo.Context) string {
	uid, _ := c.Get("user_id").(string)
	return uid
}

// ListRules handles GET /api/v1/device-control/rules (JWT).
func (h *PortHandler) ListRules(c echo.Context) error {
	resp, err := h.svc.ListRules(c.Request().Context())
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list rules", err)
	}
	return c.JSON(http.StatusOK, resp)
}

// CreateRule handles POST /api/v1/device-control/rules (JWT).
func (h *PortHandler) CreateRule(c echo.Context) error {
	var req dto.CreateDeviceControlRuleRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	row, err := h.svc.CreateRule(c.Request().Context(), req, h.userID(c))
	if err != nil {
		msg := err.Error()
		if strings.Contains(msg, "required") || strings.Contains(msg, "must be") {
			return errorResponse(c, http.StatusBadRequest, msg, err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to add rule", err)
	}
	return c.JSON(http.StatusCreated, row)
}

// UpdateRule handles PATCH /api/v1/device-control/rules/:id (JWT).
func (h *PortHandler) UpdateRule(c echo.Context) error {
	id, err := strconv.ParseInt(c.Param("id"), 10, 64)
	if err != nil || id < 1 {
		return errorResponse(c, http.StatusBadRequest, "Invalid id", err)
	}
	var req dto.UpdateDeviceControlRuleRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	row, err := h.svc.UpdateRule(c.Request().Context(), id, req, h.userID(c))
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return errorResponse(c, http.StatusNotFound, "Rule not found", err)
		}
		if strings.Contains(err.Error(), "must be") {
			return errorResponse(c, http.StatusBadRequest, err.Error(), err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to update rule", err)
	}
	return c.JSON(http.StatusOK, row)
}

// DeleteRule handles DELETE /api/v1/device-control/rules/:id (JWT).
func (h *PortHandler) DeleteRule(c echo.Context) error {
	id, err := strconv.ParseInt(c.Param("id"), 10, 64)
	if err != nil || id < 1 {
		return errorResponse(c, http.StatusBadRequest, "Invalid id", err)
	}
	if err := h.svc.DeleteRule(c.Request().Context(), id, h.userID(c)); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return errorResponse(c, http.StatusNotFound, "Rule not found", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to delete rule", err)
	}
	return c.NoContent(http.StatusNoContent)
}
