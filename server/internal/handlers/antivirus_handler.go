package handlers

import (
	"log"
	"net/http"
	"strconv"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

// AntivirusHandler serves the 24/7 antivirus guard: DeviceAuth status sync from the
// Windows service, JWT fleet view for the web /antivirus page.
type AntivirusHandler struct {
	svc *services.AntivirusService
}

func NewAntivirusHandler(svc *services.AntivirusService) *AntivirusHandler {
	return &AntivirusHandler{svc: svc}
}

// SyncStatus handles POST /api/v1/antivirus/status/sync (DeviceAuth).
func (h *AntivirusHandler) SyncStatus(c echo.Context) error {
	var req dto.SyncAntivirusStatusRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	if err := h.svc.SyncStatus(c.Request().Context(), &req); err != nil {
		log.Printf("[antivirus] SyncStatus error: %v", err)
		return errorResponse(c, http.StatusInternalServerError, "Failed to sync antivirus status", err)
	}
	resp := dto.SyncAntivirusStatusResponse{Accepted: true}
	if removed, _ := c.Get("device_removed").(bool); removed {
		resp.Action = "wipe_local"
	}
	return c.JSON(http.StatusOK, resp)
}

// ListStatus handles GET /api/v1/antivirus/status?search=&health=&page=&perPage= (JWT).
func (h *AntivirusHandler) ListStatus(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	resp, err := h.svc.ListStatus(c.Request().Context(), repository.AntivirusListFilter{
		Search:  c.QueryParam("search"),
		Health:  c.QueryParam("health"),
		Page:    page,
		PerPage: perPage,
	})
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list antivirus status", err)
	}
	return c.JSON(http.StatusOK, resp)
}

// ── Remote commands (admin-side fix) ────────────────────────────────────────

// QueueCommand handles POST /api/v1/antivirus/commands (JWT) — admin queues a remote action.
func (h *AntivirusHandler) QueueCommand(c echo.Context) error {
	var req dto.CreateAntivirusCommandRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	uid, _ := c.Get("user_id").(string)
	id, err := h.svc.QueueCommand(c.Request().Context(), &req, uid)
	if err != nil {
		if err == services.ErrInvalidCommand {
			return errorResponse(c, http.StatusBadRequest, "Invalid antivirus command", err)
		}
		log.Printf("[antivirus] QueueCommand error: %v", err)
		return errorResponse(c, http.StatusInternalServerError, "Failed to queue command", err)
	}
	return c.JSON(http.StatusOK, map[string]any{"id": id, "queued": true})
}

// ListCommands handles GET /api/v1/antivirus/commands?employeeId=&machineId=&limit= (JWT).
func (h *AntivirusHandler) ListCommands(c echo.Context) error {
	empID := c.QueryParam("employeeId")
	if empID == "" {
		return errorResponse(c, http.StatusBadRequest, "employeeId is required", nil)
	}
	limit, _ := strconv.Atoi(c.QueryParam("limit"))
	resp, err := h.svc.ListCommands(c.Request().Context(), empID, c.QueryParam("machineId"), limit)
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list commands", err)
	}
	return c.JSON(http.StatusOK, resp)
}

// PullCommands handles GET /api/v1/antivirus/commands/pending (DeviceAuth) — the Windows
// service claims its machine's pending commands (pending -> running).
func (h *AntivirusHandler) PullCommands(c echo.Context) error {
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	machineID := getAuthenticatedMachineID(c)
	cmds, err := h.svc.PullCommands(c.Request().Context(), empID, machineID)
	if err != nil {
		log.Printf("[antivirus] PullCommands error: %v", err)
		return errorResponse(c, http.StatusInternalServerError, "Failed to pull commands", err)
	}
	resp := dto.AntivirusCommandPullResponse{Commands: cmds}
	if removed, _ := c.Get("device_removed").(bool); removed {
		resp.Action = "wipe_local"
	}
	return c.JSON(http.StatusOK, resp)
}

// ReportResult handles POST /api/v1/antivirus/commands/result (DeviceAuth).
func (h *AntivirusHandler) ReportResult(c echo.Context) error {
	var req dto.AntivirusCommandResultRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	if err := h.svc.ReportResult(c.Request().Context(), empID, getAuthenticatedMachineID(c), &req); err != nil {
		if err == services.ErrInvalidCommand {
			return errorResponse(c, http.StatusBadRequest, "Invalid command result", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to record command result", err)
	}
	return c.JSON(http.StatusOK, map[string]any{"accepted": true})
}
