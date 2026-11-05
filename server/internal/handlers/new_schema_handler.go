package handlers

import (
	"log"
	"net/http"
	"strconv"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

// NewSchemaHandler handles ingestion and listing endpoints for Phase 1 & 2 tables.
type NewSchemaHandler struct {
	service     *services.NewSchemaService
	authService *services.AuthService
}

func NewNewSchemaHandler(service *services.NewSchemaService, authService *services.AuthService) *NewSchemaHandler {
	return &NewSchemaHandler{service: service, authService: authService}
}

// Helper to extract server-authenticated employee ID from Echo context
func getAuthenticatedEmployeeID(c echo.Context) (string, error) {
	employeeID, ok := c.Get("employee_id").(string)
	if !ok || employeeID == "" {
		return "", c.JSON(http.StatusUnauthorized, dto.APIError{Code: http.StatusUnauthorized, Message: "Unauthorized employee context"})
	}
	return employeeID, nil
}

// getAuthenticatedMachineID returns the reporting device's machine id (set by
// DeviceAuth). Empty for the legacy Bearer fallback, which keeps those rows
// unattributed rather than guessing a machine.
func getAuthenticatedMachineID(c echo.Context) string {
	machineID, _ := c.Get("device_machine_id").(string)
	return machineID
}

// deviceRemoved reports whether DeviceAuth accepted a REVOKED device token — i.e. this
// machine was removed via the dashboard ("Remove current device"). The sync handler must
// persist the incoming batch FIRST, then let applyDeviceAction answer with the wipe
// instruction (R6). Never set on the Bearer fallback.
func deviceRemoved(c echo.Context) bool {
	v, _ := c.Get("device_removed").(bool)
	return v
}

// applyDeviceAction decorates a sync response with wipe_local when the device was removed
// (after the batch was persisted — R6).
func (h *NewSchemaHandler) applyDeviceAction(c echo.Context, resp *dto.SyncBatchResponse) *dto.SyncBatchResponse {
	if resp == nil {
		return resp
	}
	if deviceRemoved(c) {
		resp.Action = "wipe_local"
	}
	return resp
}

// ────────────────────────────────
// Phase 1: Device Hardware Info
// ────────────────────────────────

func (h *NewSchemaHandler) SyncDeviceHardware(c echo.Context) error {
	var req dto.SyncDeviceHardwareRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.service.SyncDeviceHardware(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncDeviceHardware error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

// ────────────────────────────────
// Phase 1: Installed Applications
// ────────────────────────────────

func (h *NewSchemaHandler) SyncInstalledApps(c echo.Context) error {
	var req dto.SyncInstalledAppsRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.service.SyncInstalledApps(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncInstalledApps error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

// ────────────────────────────────
// Phase 1: Installed Packages
// ────────────────────────────────

func (h *NewSchemaHandler) SyncInstalledPackages(c echo.Context) error {
	var req dto.SyncInstalledPackagesRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.service.SyncInstalledPackages(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncInstalledPackages error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

// ────────────────────────────────
// Phase 1: Network Info
// ────────────────────────────────

func (h *NewSchemaHandler) SyncNetworkInfo(c echo.Context) error {
	var req dto.SyncNetworkInfoRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.service.SyncNetworkInfo(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncNetworkInfo error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

// ────────────────────────────────
// Phase 1: Session Events
// ────────────────────────────────

func (h *NewSchemaHandler) SyncSessionEvents(c echo.Context) error {
	var req dto.SyncSessionEventsRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.service.SyncSessionEvents(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncSessionEvents error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

// ────────────────────────────────
// Phase 2: App Sessions
// ────────────────────────────────

func (h *NewSchemaHandler) SyncAppSessions(c echo.Context) error {
	var req dto.SyncAppSessionsRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncAppSessions(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncAppSessions error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

// ────────────────────────────────
// App Items (replaces old separate child tables)
// ────────────────────────────────

func (h *NewSchemaHandler) SyncAppItems(c echo.Context) error {
	var req dto.SyncAppItemsRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.service.SyncAppItems(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncAppItems error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

// ────────────────────────────────
// List App Sessions (web dashboard)
// ────────────────────────────────

func (h *NewSchemaHandler) ListAppSessions(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))

	params := repository.AppSessionListParams{
		EmployeeID: c.QueryParam("employeeId"),
		Search:     c.QueryParam("search"),
		Platform:   c.QueryParam("platform"),
		DateFrom:   parseTimeParam(c.QueryParam("dateFrom")),
		DateTo:     parseTimeParam(c.QueryParam("dateTo")),
		Page:       page,
		PerPage:    perPage,
	}

	result, err := h.service.ListAppSessions(c.Request().Context(), params)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list app sessions",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, result)
}

// ────────────────────────────────
// List App Sessions Usage (per-app aggregate for web dashboard)
// ────────────────────────────────
//
// Returns one row per (appDisplayName, processName) with session count,
// first opened, last closed, and total duration. The "Duration" cell
// the page renders is (lastClosedAt - firstOpenedAt), NOT the sum of
// per-session durations — so multi-tab windows never inflate the total.

func (h *NewSchemaHandler) ListAppSessionsUsage(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))

	params := repository.AppSessionUsageListParams{
		EmployeeID: c.QueryParam("employeeId"),
		Search:     c.QueryParam("search"),
		Platform:   c.QueryParam("platform"),
		DateFrom:   parseTimeParam(c.QueryParam("dateFrom")),
		DateTo:     parseTimeParam(c.QueryParam("dateTo")),
		Page:       page,
		PerPage:    perPage,
	}

	result, err := h.service.ListAppSessionsUsage(c.Request().Context(), params)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list app sessions usage",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, result)
}

// ────────────────────────────────
// List App Sessions For A Specific App (per-app chevron expand)
// ────────────────────────────────
//
// Used by the /employee-journey/apps page when the user expands a row
// to see the individual sessions that make up the per-app aggregate.
// The (appDisplayName, processName) pair is the same GROUP BY key the
// aggregate uses, so the result is consistent with the parent row's
// sessionCount. Paginated server-side so a heavy user (e.g. 200 chrome
// opens in a week) doesn't ship every row up front.
//
// Both appDisplayName AND processName MUST be provided — an empty
// processName matches rows where process_name IS NULL (the aggregate
// rows can have an empty processName when the client didn't supply one).

func (h *NewSchemaHandler) ListAppSessionsForApp(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))

	appDisplayName := c.QueryParam("appDisplayName")
	processName := c.QueryParam("processName")

	// Either both or neither — a single key without its pair is ambiguous.
	if appDisplayName == "" && processName == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "appDisplayName and processName are required (both, or both empty for the IS NULL case)",
		})
	}

	params := repository.AppSessionForAppListParams{
		EmployeeID:     c.QueryParam("employeeId"),
		AppDisplayName: appDisplayName,
		ProcessName:    processName,
		DateFrom:       parseTimeParam(c.QueryParam("dateFrom")),
		DateTo:         parseTimeParam(c.QueryParam("dateTo")),
		Page:           page,
		PerPage:        perPage,
	}

	result, err := h.service.ListAppSessionsForApp(c.Request().Context(), params)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list app sessions for app",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, result)
}

// ────────────────────────────────
// List App Items Usage (per-domain aggregate for web dashboard)
// ────────────────────────────────
//
// Returns one row per domain with visitCount, totalDurationSeconds, and
// lastVisitedAt. The /employee-journey/web page uses this for Visits and
// Duration so infinite-scroll page loads never inflate those cells.

func (h *NewSchemaHandler) ListAppItemsUsage(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))

	itemType := c.QueryParam("itemType")
	if itemType == "" {
		itemType = "browser_tab"
	}

	params := repository.AppItemUsageListParams{
		EmployeeID: c.QueryParam("employeeId"),
		ItemType:   itemType,
		Search:     c.QueryParam("search"),
		DateFrom:   parseTimeParam(c.QueryParam("dateFrom")),
		DateTo:     parseTimeParam(c.QueryParam("dateTo")),
		Page:       page,
		PerPage:    perPage,
	}

	result, err := h.service.ListAppItemsUsage(c.Request().Context(), params)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list app items usage",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, result)
}

// ────────────────────────────────
// List App Items (browser URLs, file paths, etc.)
// ────────────────────────────────

func (h *NewSchemaHandler) ListAppItems(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))

	params := repository.AppItemListParams{
		EmployeeID:   c.QueryParam("employeeId"),
		AppSessionID: c.QueryParam("appSessionId"),
		ItemType:     c.QueryParam("itemType"),
		Domain:       c.QueryParam("domain"),
		Search:       c.QueryParam("search"),
		DateFrom:     parseTimeParam(c.QueryParam("dateFrom")),
		DateTo:       parseTimeParam(c.QueryParam("dateTo")),
		Page:         page,
		PerPage:      perPage,
	}

	result, err := h.service.ListAppItems(c.Request().Context(), params)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list app items",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, result)
}

// parseTimeParam parses an optional RFC3339 (or date-only) query param into a
// time.Time. Returns the zero value when empty/invalid (meaning "no bound").
func parseTimeParam(v string) time.Time {
	if v == "" {
		return time.Time{}
	}
	for _, layout := range []string{time.RFC3339, "2006-01-02"} {
		if t, err := time.Parse(layout, v); err == nil {
			return t
		}
	}
	return time.Time{}
}

// ────────────────────────────────
// Phase 3: App Status
// ────────────────────────────────

func (h *NewSchemaHandler) SyncAppStatus(c echo.Context) error {
	var req dto.SyncAppStatusRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.service.SyncAppStatus(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncAppStatus error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

// ────────────────────────────────
// Phase 3: Hardware Devices
// ────────────────────────────────

func (h *NewSchemaHandler) SyncHardwareDevices(c echo.Context) error {
	var req dto.SyncHardwareDevicesRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.service.SyncHardwareDevices(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncHardwareDevices error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

// ────────────────────────────────
// Phase 3: Permission Status
// ────────────────────────────────

func (h *NewSchemaHandler) SyncPermissionStatus(c echo.Context) error {
	var req dto.SyncPermissionStatusRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.service.SyncPermissionStatus(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncPermissionStatus error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

// ────────────────────────────────
// Phase 3: Storage Devices
// ────────────────────────────────

func (h *NewSchemaHandler) SyncStorageDevices(c echo.Context) error {
	var req dto.SyncStorageDevicesRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.service.SyncStorageDevices(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncStorageDevices error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

// ────────────────────────────────
// Phase 3: Location Samples (GPS)
// ────────────────────────────────

func (h *NewSchemaHandler) SyncLocationSamples(c echo.Context) error {
	var req dto.SyncLocationSamplesRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID
	req.MachineID = getAuthenticatedMachineID(c)

	resp, err := h.service.SyncLocationSamples(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncLocationSamples error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, h.applyDeviceAction(c, resp))
}

func (h *NewSchemaHandler) ListLocationSamples(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))

	params := repository.LocationSampleListParams{
		EmployeeID: c.QueryParam("employeeId"),
		DateFrom:   parseTimeParam(c.QueryParam("dateFrom")),
		DateTo:     parseTimeParam(c.QueryParam("dateTo")),
		Page:       page,
		PerPage:    perPage,
	}

	result, err := h.service.ListLocationSamples(c.Request().Context(), params)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list location samples",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, result)
}

// ────────────────────────────────
// Employee Detail (web dashboard — GET /employees/:id/detail)
// ────────────────────────────────

func (h *NewSchemaHandler) GetEmployeeDetail(c echo.Context) error {
	// ?deviceId=<machine_id> scopes the device-level surfaces to one machine; empty keeps
	// the employee-wide aggregate (see the Device Specs device dropdown).
	detail, err := h.service.GetEmployeeDetail(c.Request().Context(), c.Param("id"), c.QueryParam("deviceId"))
	if err != nil {
		if err.Error() == "employee not found" {
			return c.JSON(http.StatusNotFound, dto.APIError{Code: http.StatusNotFound, Message: "Employee not found"})
		}
		log.Printf("[new_schema] GetEmployeeDetail error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to load employee detail",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, detail)
}

// RemoveEmployeeDevice handles POST /api/v1/employees/:id/devices/remove (web admin).
// Revokes every active device, closes open sessions/items and flips the employee to
// untracked — history is kept, and the next login on a new machine starts fresh.
func (h *NewSchemaHandler) RemoveEmployeeDevice(c echo.Context) error {
	resp, err := h.service.RemoveEmployeeDevices(c.Request().Context(), c.Param("id"))
	if err != nil {
		if err.Error() == "employee not found" {
			return c.JSON(http.StatusNotFound, dto.APIError{Code: http.StatusNotFound, Message: "Employee not found"})
		}
		log.Printf("[new_schema] RemoveEmployeeDevice error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to remove the employee device",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}

// SelfRemoveAccount handles POST /api/v1/devices/self-remove (DeviceAuth). The client
// calls it AFTER draining its unsynced rows, when the employee runs
// `client.exe --remove-account`: the CALLING machine is marked 'old' with last_track_at
// advanced, its device registrations revoked, open activity closed and the employee
// flipped to untracked. Nothing is deleted (R4/R5). The client then wipes its local DB +
// machine id, and the next login generates a brand-new machine id (R7).
func (h *NewSchemaHandler) SelfRemoveAccount(c echo.Context) error {
	employeeID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	machineID := getAuthenticatedMachineID(c)

	resp, err := h.service.RemoveAccount(c.Request().Context(), employeeID, machineID)
	if err != nil {
		if err.Error() == "employee not found" {
			return c.JSON(http.StatusNotFound, dto.APIError{Code: http.StatusNotFound, Message: "Employee not found"})
		}
		log.Printf("[new_schema] SelfRemoveAccount error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to remove the account",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// Hours Insights (web dashboard — GET /hours-insights)
// ────────────────────────────────

func (h *NewSchemaHandler) GetHoursInsights(c echo.Context) error {
	employeeID := c.QueryParam("employeeId")
	if employeeID == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "employeeId is required"})
	}

	dateFrom := parseTimeParam(c.QueryParam("dateFrom"))
	dateTo := parseTimeParam(c.QueryParam("dateTo"))

	result, err := h.service.GetHoursInsights(c.Request().Context(), repository.HoursInsightsParams{
		EmployeeID: employeeID,
		DateFrom:   dateFrom,
		DateTo:     dateTo,
		Preset:     c.QueryParam("preset"),
	})
	if err != nil {
		log.Printf("[new_schema] GetHoursInsights error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to load hours insights",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, result)
}
