package handlers

import (
	"log"
	"net/http"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/labstack/echo/v4"
)

type TermsConsentHandler struct {
	repo *repository.TermsConsentRepo
}

func NewTermsConsentHandler(repo *repository.TermsConsentRepo) *TermsConsentHandler {
	return &TermsConsentHandler{repo: repo}
}

// SyncTermsConsent handles POST /api/v1/terms-consent/sync — a CLIENT (device)
// endpoint. It lives under DeviceAuth, whose middleware is the only one that sets
// the "employee_id" context value this handler reads (the web-admin JWTAuth group
// never sets it — see the Client-vs-Web API Auth Separation Rule in AGENTS.md §6).
// Request: { entries: [{ featureId, termsVersion, action, acceptedAt, revokedAt }] }
// Response: { synced: N }
func (h *TermsConsentHandler) SyncTermsConsent(c echo.Context) error {
	var req struct {
		EmployeeID string `json:"employeeId"`
		Token      string `json:"token"`
		Entries    []struct {
			FeatureID    string  `json:"featureId"`
			TermsVersion string  `json:"termsVersion"`
			Action       string  `json:"action"`
			AcceptedAt   string  `json:"acceptedAt"`
			RevokedAt    *string `json:"revokedAt"`
		} `json:"entries"`
	}

	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}

	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}

	if len(req.Entries) == 0 {
		return c.JSON(http.StatusOK, map[string]interface{}{"synced": 0})
	}

	entries := make([]repository.TermsConsentEntry, 0, len(req.Entries))
	for _, e := range req.Entries {
		acceptedAt := time.Now() // default to now
		if t, err := time.Parse(time.RFC3339, e.AcceptedAt); err == nil {
			acceptedAt = t
		}

		action := e.Action
		if action == "" {
			action = "accepted"
		}

		entries = append(entries, repository.TermsConsentEntry{
			EmployeeID:   empID,
			FeatureID:    e.FeatureID,
			TermsVersion: e.TermsVersion,
			Action:       action,
			CreatedAt:    acceptedAt,
		})
	}

	synced, err := h.repo.BulkInsert(c.Request().Context(), entries)
	if err != nil {
		log.Printf("[terms_consent] SyncTermsConsent error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}

	return c.JSON(http.StatusOK, map[string]interface{}{"synced": synced})
}

// ListTermsConsent handles GET /api/v1/terms-consent?employeeId=...
// Used by the web privacy settings page.
func (h *TermsConsentHandler) ListTermsConsent(c echo.Context) error {
	employeeID := c.QueryParam("employeeId")
	if employeeID == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "employeeId is required"})
	}

	entries, err := h.repo.ListByEmployee(c.Request().Context(), employeeID, 100)
	if err != nil {
		log.Printf("[terms_consent] ListTermsConsent error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to list", Detail: err.Error()})
	}

	return c.JSON(http.StatusOK, map[string]interface{}{
		"entries": entries,
		"total":   len(entries),
	})
}

// HasAccepted handles GET /api/v1/terms-consent/check?employeeId=...&featureId=...
// Quick check if an employee has accepted a specific feature's T&C.
func (h *TermsConsentHandler) HasAccepted(c echo.Context) error {
	employeeID := c.QueryParam("employeeId")
	featureID := c.QueryParam("featureId")
	if employeeID == "" || featureID == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "employeeId and featureId are required"})
	}

	accepted, err := h.repo.HasAccepted(c.Request().Context(), employeeID, featureID, "1.0.0")
	if err != nil {
		log.Printf("[terms_consent] HasAccepted error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to check", Detail: err.Error()})
	}

	return c.JSON(http.StatusOK, map[string]interface{}{
		"accepted":   accepted,
		"employeeId": employeeID,
		"featureId":  featureID,
	})
}
