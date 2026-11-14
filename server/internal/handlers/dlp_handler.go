package handlers

import (
	"errors"
	"net/http"
	"strconv"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/jackc/pgx/v5"
	"github.com/labstack/echo/v4"
)

// DlpHandler serves Security & DLP admin + device endpoints.
type DlpHandler struct {
	svc          *services.DlpService
	employeeRepo *repository.EmployeeRepo
}

func NewDlpHandler(svc *services.DlpService, employeeRepo *repository.EmployeeRepo) *DlpHandler {
	return &DlpHandler{svc: svc, employeeRepo: employeeRepo}
}

func (h *DlpHandler) resolveEmployee(c echo.Context) (*models.Employee, error) {
	id := strings.TrimSpace(c.Param("id"))
	if id == "" {
		return nil, errorResponse(c, http.StatusBadRequest, "employee id is required", nil)
	}
	emp, err := h.employeeRepo.GetByID(c.Request().Context(), id)
	if err != nil {
		return nil, errorResponse(c, http.StatusInternalServerError, "Failed to load employee", err)
	}
	if emp == nil {
		return nil, errorResponse(c, http.StatusNotFound, "Employee not found", nil)
	}
	return emp, nil
}

// ── Device (DeviceAuth) ─────────────────────────────────────────────────────

// GetPolicy handles GET /api/v1/dlp/policy?sinceVersion=
func (h *DlpHandler) GetPolicy(c echo.Context) error {
	since := int64(0)
	if v := c.QueryParam("sinceVersion"); v != "" {
		n, err := strconv.ParseInt(v, 10, 64)
		if err != nil {
			return errorResponse(c, http.StatusBadRequest, "Invalid sinceVersion", err)
		}
		since = n
	}
	employeeID, _ := c.Get("employee_id").(string)
	machineID, _ := c.Get("device_machine_id").(string)
	resp, err := h.svc.PullPolicy(c.Request().Context(), since, employeeID, machineID)
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to pull DLP policy", err)
	}
	return c.JSON(http.StatusOK, resp)
}

// GetLockdownDraft handles GET /api/v1/dlp/lockdown
func (h *DlpHandler) GetLockdownDraft(c echo.Context) error {
	resp, err := h.svc.GetLockdownDraft(c.Request().Context())
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to load lockdown settings", err)
	}
	return c.JSON(http.StatusOK, resp)
}

// UpdateLockdownDraft handles PUT /api/v1/dlp/lockdown
func (h *DlpHandler) UpdateLockdownDraft(c echo.Context) error {
	var req dto.UpdateLockdownDraftRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	resp, err := h.svc.UpdateLockdownDraft(c.Request().Context(), req)
	if err != nil {
		msg := err.Error()
		if strings.Contains(msg, "cap exceeded") {
			return errorResponse(c, http.StatusBadRequest, msg, err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to save lockdown settings", err)
	}
	return c.JSON(http.StatusOK, resp)
}

func (h *DlpHandler) userID(c echo.Context) string {
	uid, _ := c.Get("user_id").(string)
	return uid
}

func (h *DlpHandler) allowListQuery(c echo.Context) dto.AllowListQuery {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	return dto.AllowListQuery{
		Q:         c.QueryParam("q"),
		CreatedBy: c.QueryParam("createdBy"),
		Enabled:   c.QueryParam("enabled"),
		Page:      page,
		PerPage:   perPage,
	}
}

// ListAllowHosts handles GET /api/v1/dlp/lockdown/hosts
func (h *DlpHandler) ListAllowHosts(c echo.Context) error {
	resp, err := h.svc.ListAllowHosts(c.Request().Context(), h.allowListQuery(c))
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list allow hosts", err)
	}
	return c.JSON(http.StatusOK, resp)
}

// CreateAllowHost handles POST /api/v1/dlp/lockdown/hosts
func (h *DlpHandler) CreateAllowHost(c echo.Context) error {
	var req dto.CreateAllowHostRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	row, err := h.svc.CreateAllowHost(c.Request().Context(), req, h.userID(c))
	if err != nil {
		msg := err.Error()
		if strings.Contains(msg, "required") || strings.Contains(msg, "already") || strings.Contains(msg, "cap exceeded") {
			return errorResponse(c, http.StatusBadRequest, msg, err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to add host", err)
	}
	return c.JSON(http.StatusCreated, row)
}

// UpdateAllowHost handles PATCH /api/v1/dlp/lockdown/hosts/:id
func (h *DlpHandler) UpdateAllowHost(c echo.Context) error {
	id, err := strconv.ParseInt(c.Param("id"), 10, 64)
	if err != nil || id < 1 {
		return errorResponse(c, http.StatusBadRequest, "Invalid id", err)
	}
	var req dto.UpdateAllowHostRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	row, err := h.svc.UpdateAllowHost(c.Request().Context(), id, req, h.userID(c))
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return errorResponse(c, http.StatusNotFound, "Host not found", err)
		}
		msg := err.Error()
		if strings.Contains(msg, "required") || strings.Contains(strings.ToLower(msg), "unique") {
			return errorResponse(c, http.StatusBadRequest, msg, err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to update host", err)
	}
	return c.JSON(http.StatusOK, row)
}

// DeleteAllowHost handles DELETE /api/v1/dlp/lockdown/hosts/:id
func (h *DlpHandler) DeleteAllowHost(c echo.Context) error {
	id, err := strconv.ParseInt(c.Param("id"), 10, 64)
	if err != nil || id < 1 {
		return errorResponse(c, http.StatusBadRequest, "Invalid id", err)
	}
	if err := h.svc.DeleteAllowHost(c.Request().Context(), id); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return errorResponse(c, http.StatusNotFound, "Host not found", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to delete host", err)
	}
	return c.NoContent(http.StatusNoContent)
}

// ListAllowApps handles GET /api/v1/dlp/lockdown/apps
func (h *DlpHandler) ListAllowApps(c echo.Context) error {
	resp, err := h.svc.ListAllowApps(c.Request().Context(), h.allowListQuery(c))
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list allow apps", err)
	}
	return c.JSON(http.StatusOK, resp)
}

// CreateAllowApp handles POST /api/v1/dlp/lockdown/apps
func (h *DlpHandler) CreateAllowApp(c echo.Context) error {
	var req dto.CreateAllowAppRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	row, err := h.svc.CreateAllowApp(c.Request().Context(), req, h.userID(c))
	if err != nil {
		msg := err.Error()
		if isAllowAppClientErr(msg) {
			return errorResponse(c, http.StatusBadRequest, msg, err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to add app", err)
	}
	return c.JSON(http.StatusCreated, row)
}

// UpdateAllowApp handles PATCH /api/v1/dlp/lockdown/apps/:id
func (h *DlpHandler) UpdateAllowApp(c echo.Context) error {
	id, err := strconv.ParseInt(c.Param("id"), 10, 64)
	if err != nil || id < 1 {
		return errorResponse(c, http.StatusBadRequest, "Invalid id", err)
	}
	var req dto.UpdateAllowAppRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	row, err := h.svc.UpdateAllowApp(c.Request().Context(), id, req, h.userID(c))
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return errorResponse(c, http.StatusNotFound, "App entry not found", err)
		}
		msg := err.Error()
		if isAllowAppClientErr(msg) {
			return errorResponse(c, http.StatusBadRequest, msg, err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to update app", err)
	}
	return c.JSON(http.StatusOK, row)
}

func isAllowAppClientErr(msg string) bool {
	return strings.Contains(msg, "needs") ||
		strings.Contains(msg, "cap exceeded") ||
		strings.Contains(msg, "already on the allow list") ||
		strings.Contains(msg, "catalog application not found") ||
		strings.Contains(msg, "catalog app has no") ||
		strings.Contains(msg, "installedApplicationId")
}

// DeleteAllowApp handles DELETE /api/v1/dlp/lockdown/apps/:id
func (h *DlpHandler) DeleteAllowApp(c echo.Context) error {
	id, err := strconv.ParseInt(c.Param("id"), 10, 64)
	if err != nil || id < 1 {
		return errorResponse(c, http.StatusBadRequest, "Invalid id", err)
	}
	if err := h.svc.DeleteAllowApp(c.Request().Context(), id); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return errorResponse(c, http.StatusNotFound, "App entry not found", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to delete app", err)
	}
	return c.NoContent(http.StatusNoContent)
}

// PauseMachineLockdown handles POST /api/v1/employees/:id/machines/:machineId/lockdown-pause
func (h *DlpHandler) PauseMachineLockdown(c echo.Context) error {
	emp, err := h.resolveEmployee(c)
	if err != nil {
		return err
	}
	machineID := strings.TrimSpace(c.Param("machineId"))
	if machineID == "" {
		return errorResponse(c, http.StatusBadRequest, "machineId is required", nil)
	}
	var req dto.PauseLockdownRequest
	_ = c.Bind(&req)
	resp, err := h.svc.PauseLockdown(c.Request().Context(), emp.EmployeeID, machineID, req.Minutes)
	if err != nil {
		if strings.Contains(err.Error(), "not found") {
			return errorResponse(c, http.StatusNotFound, "Machine not found", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to pause lockdown", err)
	}
	return c.JSON(http.StatusOK, resp)
}

// SyncAlerts handles POST /api/v1/dlp/alerts/sync
func (h *DlpHandler) SyncAlerts(c echo.Context) error {
	employeeID, ok := c.Get("employee_id").(string)
	if !ok || employeeID == "" {
		return errorResponse(c, http.StatusUnauthorized, "Missing employee context", nil)
	}
	machineID, _ := c.Get("device_machine_id").(string)
	var req dto.SyncDlpAlertsRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	if len(req.Alerts) == 0 {
		return c.JSON(http.StatusOK, dto.SyncDlpAlertsResponse{AcceptedIDs: []string{}})
	}
	resp, err := h.svc.SyncAlerts(c.Request().Context(), employeeID, machineID, req)
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to sync DLP alerts", err)
	}
	if removed, _ := c.Get("device_removed").(bool); removed {
		resp.Action = "wipe_local"
	}
	return c.JSON(http.StatusOK, resp)
}

// PostHealth handles POST /api/v1/dlp/health
func (h *DlpHandler) PostHealth(c echo.Context) error {
	var req dto.DlpHealthRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	resp, err := h.svc.Health(c.Request().Context(), req)
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to record DLP health", err)
	}
	return c.JSON(http.StatusOK, resp)
}

// VerifyRemovalCode handles POST /api/v1/devices/removal-verify
func (h *DlpHandler) VerifyRemovalCode(c echo.Context) error {
	machineID, _ := c.Get("device_machine_id").(string)
	var req dto.VerifyRemovalCodeRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	resp, err := h.svc.VerifyRemovalCode(c.Request().Context(), machineID, req)
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to verify removal code", err)
	}
	if !resp.Valid {
		return c.JSON(http.StatusUnauthorized, resp)
	}
	return c.JSON(http.StatusOK, resp)
}

// ── Admin JWT ───────────────────────────────────────────────────────────────

func (h *DlpHandler) ListRules(c echo.Context) error {
	rules, err := h.svc.ListRules(c.Request().Context())
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list DLP rules", err)
	}
	out := make([]any, len(rules))
	for i := range rules {
		out[i] = rules[i]
	}
	return c.JSON(http.StatusOK, dto.DlpRuleListResponse{Rules: out, Total: len(out)})
}

func (h *DlpHandler) CreateRule(c echo.Context) error {
	var req dto.CreateDlpRuleRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	rule, err := h.svc.CreateRule(c.Request().Context(), req)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to create DLP rule", err)
	}
	return c.JSON(http.StatusCreated, rule)
}

func (h *DlpHandler) GetRule(c echo.Context) error {
	rule, err := h.svc.GetRule(c.Request().Context(), c.Param("id"))
	if err != nil {
		if strings.Contains(err.Error(), "not found") {
			return errorResponse(c, http.StatusNotFound, "Failed to get DLP rule", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to get DLP rule", err)
	}
	return c.JSON(http.StatusOK, rule)
}

func (h *DlpHandler) UpdateRule(c echo.Context) error {
	id := c.Param("id")
	var req dto.UpdateDlpRuleRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	var changedBy *string
	if uid, ok := c.Get("user_id").(string); ok && uid != "" {
		changedBy = &uid
	}
	rule, err := h.svc.UpdateRule(c.Request().Context(), id, req, changedBy)
	if err != nil {
		if strings.Contains(err.Error(), "not found") {
			return errorResponse(c, http.StatusNotFound, "Failed to update DLP rule", err)
		}
		return errorResponse(c, http.StatusBadRequest, "Failed to update DLP rule", err)
	}
	return c.JSON(http.StatusOK, rule)
}

func (h *DlpHandler) DeleteRule(c echo.Context) error {
	if err := h.svc.DeleteRule(c.Request().Context(), c.Param("id")); err != nil {
		if strings.Contains(err.Error(), "not found") {
			return errorResponse(c, http.StatusNotFound, "Failed to delete DLP rule", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to delete DLP rule", err)
	}
	return c.JSON(http.StatusOK, map[string]string{"message": "rule deleted"})
}

func (h *DlpHandler) ListDlpRuleRevisions(c echo.Context) error {
	revs, err := h.svc.ListDlpRuleRevisions(c.Request().Context(), c.Param("id"))
	if err != nil {
		if strings.Contains(err.Error(), "not found") {
			return errorResponse(c, http.StatusNotFound, "Failed to list DLP rule revisions", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to list DLP rule revisions", err)
	}
	out := make([]any, len(revs))
	for i := range revs {
		out[i] = revs[i]
	}
	return c.JSON(http.StatusOK, dto.DlpRuleRevisionListResponse{Revisions: out, Total: len(out)})
}

func (h *DlpHandler) ListWebBlockerRules(c echo.Context) error {
	rules, err := h.svc.ListWebBlockerRules(c.Request().Context())
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list Web Blocker rules", err)
	}
	out := make([]any, len(rules))
	for i := range rules {
		out[i] = rules[i]
	}
	return c.JSON(http.StatusOK, dto.WebBlockerRuleListResponse{Rules: out, Total: len(out)})
}

func (h *DlpHandler) GetWebBlockerRule(c echo.Context) error {
	rule, err := h.svc.GetWebBlockerRule(c.Request().Context(), c.Param("id"))
	if err != nil {
		if strings.Contains(err.Error(), "not found") {
			return errorResponse(c, http.StatusNotFound, "Failed to get Web Blocker rule", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to get Web Blocker rule", err)
	}
	return c.JSON(http.StatusOK, rule)
}

func (h *DlpHandler) CreateWebBlockerRule(c echo.Context) error {
	var req dto.CreateWebBlockerRuleRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	rule, err := h.svc.CreateWebBlockerRule(c.Request().Context(), req)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to create Web Blocker rule", err)
	}
	return c.JSON(http.StatusCreated, rule)
}

func (h *DlpHandler) UpdateWebBlockerRule(c echo.Context) error {
	var req dto.UpdateWebBlockerRuleRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	var changedBy *string
	if uid, ok := c.Get("user_id").(string); ok && uid != "" {
		changedBy = &uid
	}
	rule, err := h.svc.UpdateWebBlockerRule(c.Request().Context(), c.Param("id"), req, changedBy)
	if err != nil {
		if strings.Contains(err.Error(), "not found") {
			return errorResponse(c, http.StatusNotFound, "Failed to update Web Blocker rule", err)
		}
		return errorResponse(c, http.StatusBadRequest, "Failed to update Web Blocker rule", err)
	}
	return c.JSON(http.StatusOK, rule)
}

func (h *DlpHandler) DeleteWebBlockerRule(c echo.Context) error {
	if err := h.svc.DeleteWebBlockerRule(c.Request().Context(), c.Param("id")); err != nil {
		if strings.Contains(err.Error(), "not found") {
			return errorResponse(c, http.StatusNotFound, "Failed to delete Web Blocker rule", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to delete Web Blocker rule", err)
	}
	return c.JSON(http.StatusOK, map[string]string{"message": "rule deleted"})
}

func (h *DlpHandler) ListWebBlockerRevisions(c echo.Context) error {
	revs, err := h.svc.ListWebBlockerRevisions(c.Request().Context(), c.Param("id"))
	if err != nil {
		if strings.Contains(err.Error(), "not found") {
			return errorResponse(c, http.StatusNotFound, "Failed to list Web Blocker revisions", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to list Web Blocker revisions", err)
	}
	out := make([]any, len(revs))
	for i := range revs {
		out[i] = revs[i]
	}
	return c.JSON(http.StatusOK, dto.WebBlockerRevisionListResponse{Revisions: out, Total: len(out)})
}

func (h *DlpHandler) ExpandWebBlockerDomains(c echo.Context) error {
	var req dto.ExpandWebBlockerDomainsRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	return c.JSON(http.StatusOK, h.svc.ExpandWebBlockerDomains(req.Domains))
}

func (h *DlpHandler) Publish(c echo.Context) error {
	var publishedBy *string
	if uid, ok := c.Get("user_id").(string); ok && uid != "" {
		publishedBy = &uid
	}
	resp, err := h.svc.Publish(c.Request().Context(), publishedBy)
	if err != nil {
		if strings.Contains(err.Error(), "cap exceeded") {
			return errorResponse(c, http.StatusBadRequest, err.Error(), err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to publish DLP policy", err)
	}
	return c.JSON(http.StatusOK, resp)
}

func (h *DlpHandler) ListAlerts(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	f := repository.DlpAlertListFilter{
		Search:     strings.TrimSpace(c.QueryParam("q")),
		Status:     strings.TrimSpace(c.QueryParam("status")),
		Severity:   strings.TrimSpace(c.QueryParam("severity")),
		Type:       strings.TrimSpace(c.QueryParam("type")),
		EmployeeID: strings.TrimSpace(c.QueryParam("employeeId")),
		DateFrom:   parseTimeParam(c.QueryParam("dateFrom")),
		DateTo:     parseTimeParam(c.QueryParam("dateTo")),
		Page:       page,
		PerPage:    perPage,
	}
	resp, err := h.svc.ListAlerts(c.Request().Context(), f)
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list DLP alerts", err)
	}
	return c.JSON(http.StatusOK, resp)
}

func (h *DlpHandler) UpdateAlert(c echo.Context) error {
	var req dto.UpdateDlpAlertRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	var changedBy string
	if uid, ok := c.Get("user_id").(string); ok {
		changedBy = uid
	}
	alert, err := h.svc.UpdateAlert(c.Request().Context(), c.Param("id"), req, changedBy)
	if err != nil {
		if strings.Contains(err.Error(), "not found") {
			return errorResponse(c, http.StatusNotFound, "Failed to update DLP alert", err)
		}
		return errorResponse(c, http.StatusBadRequest, "Failed to update DLP alert", err)
	}
	return c.JSON(http.StatusOK, alert)
}

func (h *DlpHandler) ListAlertHistory(c echo.Context) error {
	resp, err := h.svc.ListAlertHistory(c.Request().Context(), c.Param("id"))
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list DLP alert history", err)
	}
	return c.JSON(http.StatusOK, resp)
}

func (h *DlpHandler) ListShadowIt(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	f := repository.ShadowItListFilter{
		Search:     strings.TrimSpace(c.QueryParam("q")),
		AuthStatus: strings.TrimSpace(c.QueryParam("authStatus")),
		Page:       page,
		PerPage:    perPage,
	}
	resp, err := h.svc.ListShadowIt(c.Request().Context(), f)
	if err != nil {
		return errorResponse(c, http.StatusInternalServerError, "Failed to list Shadow IT apps", err)
	}
	return c.JSON(http.StatusOK, resp)
}

func (h *DlpHandler) UpdateShadowIt(c echo.Context) error {
	id, err := strconv.ParseInt(c.Param("id"), 10, 64)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid id", err)
	}
	var req dto.UpdateShadowItAppRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	app, err := h.svc.UpdateShadowIt(c.Request().Context(), id, req)
	if err != nil {
		if strings.Contains(err.Error(), "not found") {
			return errorResponse(c, http.StatusNotFound, "Failed to update Shadow IT app", err)
		}
		return errorResponse(c, http.StatusBadRequest, "Failed to update Shadow IT app", err)
	}
	return c.JSON(http.StatusOK, app)
}

// IssueRemovalCode handles POST /api/v1/devices/removal-code
func (h *DlpHandler) IssueRemovalCode(c echo.Context) error {
	var req dto.IssueRemovalCodeRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	var issuedBy *string
	if uid, ok := c.Get("user_id").(string); ok && uid != "" {
		issuedBy = &uid
	}
	resp, err := h.svc.IssueRemovalCode(c.Request().Context(), req, issuedBy)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to issue removal code", err)
	}
	return c.JSON(http.StatusCreated, resp)
}
