package dto

import (
	"bytes"
	"encoding/json"
	"fmt"
	"strings"
	"time"
)

// ── Admin: DLP Rules (non-web) ───────────────────────────────────────────────

type CreateDlpRuleRequest struct {
	Name       string   `json:"name"`
	Enabled    *bool    `json:"enabled,omitempty"`
	Trigger    string   `json:"trigger"`
	Action     string   `json:"action"`
	Severity   string   `json:"severity"`
	ScopeAll   *bool    `json:"scopeAll,omitempty"`
	Extensions []string `json:"extensions"`
	Patterns   []string `json:"patterns"`
}

type UpdateDlpRuleRequest struct {
	Name       *string   `json:"name,omitempty"`
	Enabled    *bool     `json:"enabled,omitempty"`
	Trigger    *string   `json:"trigger,omitempty"`
	Action     *string   `json:"action,omitempty"`
	Severity   *string   `json:"severity,omitempty"`
	ScopeAll   *bool     `json:"scopeAll,omitempty"`
	Extensions *[]string `json:"extensions,omitempty"`
	Patterns   *[]string `json:"patterns,omitempty"`
}

type DlpRuleListResponse struct {
	Rules []any `json:"rules"`
	Total int   `json:"total"`
}

type DlpRuleRevisionListResponse struct {
	Revisions []any `json:"revisions"`
	Total     int   `json:"total"`
}

// ── Admin: Web Blocker ──────────────────────────────────────────────────────

type CreateWebBlockerRuleRequest struct {
	Name             string   `json:"name"`
	Enabled          *bool    `json:"enabled,omitempty"`
	Action           string   `json:"action"`
	Severity         string   `json:"severity"`
	ScopeAll         *bool    `json:"scopeAll,omitempty"`
	Domains          []string `json:"domains"`
	Paths            []string `json:"paths"`
	ScheduleEnabled  bool     `json:"scheduleEnabled"`
	ScheduleStart    string   `json:"scheduleStart"`
	ScheduleEnd      string   `json:"scheduleEnd"`
	ScheduleTimezone string   `json:"scheduleTimezone"`
	ScheduleDays     []string `json:"scheduleDays"`
	// ExpandRelated merges www./m. + curated CDN/alternate-apex packs into Domains.
	ExpandRelated bool `json:"expandRelated,omitempty"`
}

type UpdateWebBlockerRuleRequest struct {
	Name             *string   `json:"name,omitempty"`
	Enabled          *bool     `json:"enabled,omitempty"`
	Action           *string   `json:"action,omitempty"`
	Severity         *string   `json:"severity,omitempty"`
	ScopeAll         *bool     `json:"scopeAll,omitempty"`
	Domains          *[]string `json:"domains,omitempty"`
	Paths            *[]string `json:"paths,omitempty"`
	ScheduleEnabled  *bool     `json:"scheduleEnabled,omitempty"`
	ScheduleStart    *string   `json:"scheduleStart,omitempty"`
	ScheduleEnd      *string   `json:"scheduleEnd,omitempty"`
	ScheduleTimezone *string   `json:"scheduleTimezone,omitempty"`
	ScheduleDays     *[]string `json:"scheduleDays,omitempty"`
	ExpandRelated    *bool     `json:"expandRelated,omitempty"`
}

type WebBlockerRuleListResponse struct {
	Rules []any `json:"rules"`
	Total int   `json:"total"`
}

type ExpandWebBlockerDomainsRequest struct {
	Domains []string `json:"domains"`
}

type ExpandWebBlockerDomainsResponse struct {
	Domains   []string `json:"domains"`
	Suggested []string `json:"suggested"`
}

type WebBlockerRevisionListResponse struct {
	Revisions []any `json:"revisions"`
	Total     int   `json:"total"`
}

type PublishDlpPolicyResponse struct {
	PolicyVersion int64     `json:"policyVersion"`
	Hash          string    `json:"hash"`
	PublishedAt   time.Time `json:"publishedAt"`
	RuleCount     int       `json:"ruleCount"`
}

// ── Admin: Lockdown draft ───────────────────────────────────────────────────

// LockdownAllowApp is path and/or publisher and/or SHA-256 (never display-name-only).
type LockdownAllowApp struct {
	Publisher string `json:"publisher,omitempty"`
	Product   string `json:"product,omitempty"`
	Sha256    string `json:"sha256,omitempty"`
	Path      string `json:"path,omitempty"`
}

type LockdownDraftResponse struct {
	Lockdown      bool               `json:"lockdown"`
	AllowHosts    []string           `json:"allowHosts"`
	AllowApps     []LockdownAllowApp `json:"allowApps"`
	PlatformAllow []string           `json:"platformAllow"` // compiled from server env (read-only)
	MaxAllowHosts int                `json:"maxAllowHosts"`
	MaxAllowApps  int                `json:"maxAllowApps"`
	HostCount     int                `json:"hostCount"`
	AppCount      int                `json:"appCount"`
}

type UpdateLockdownDraftRequest struct {
	Lockdown   *bool               `json:"lockdown,omitempty"`
	AllowHosts *[]string           `json:"allowHosts,omitempty"`
	AllowApps  *[]LockdownAllowApp `json:"allowApps,omitempty"`
}

// AllowHostRow is one draft web allow-list entry (GET /dlp/lockdown/hosts).
type AllowHostRow struct {
	ID            int64     `json:"id"`
	Host          string    `json:"host"`
	Note          string    `json:"note,omitempty"`
	Enabled       bool      `json:"enabled"`
	CreatedBy     *string   `json:"createdBy,omitempty"`
	CreatedByName string    `json:"createdByName,omitempty"`
	UpdatedBy     *string   `json:"updatedBy,omitempty"`
	UpdatedByName string    `json:"updatedByName,omitempty"`
	CreatedAt     time.Time `json:"createdAt"`
	UpdatedAt     time.Time `json:"updatedAt"`
}

type AllowHostListResponse struct {
	Hosts      []AllowHostRow `json:"hosts"`
	Total      int            `json:"total"`
	Page       int            `json:"page"`
	PerPage    int            `json:"perPage"`
	TotalPages int            `json:"totalPages"`
}

type CreateAllowHostRequest struct {
	Host    string `json:"host"`
	Note    string `json:"note,omitempty"`
	Enabled *bool  `json:"enabled,omitempty"`
}

type UpdateAllowHostRequest struct {
	Host    *string `json:"host,omitempty"`
	Note    *string `json:"note,omitempty"`
	Enabled *bool   `json:"enabled,omitempty"`
}

// AllowAppRow is one draft app allow-list entry (GET /dlp/lockdown/apps).
type AllowAppRow struct {
	ID                       int64     `json:"id"`
	InstalledApplicationID   string    `json:"installedApplicationId,omitempty"`
	AppName                  string    `json:"appName,omitempty"`
	Publisher                string    `json:"publisher,omitempty"`
	Product                  string    `json:"product,omitempty"`
	Sha256                   string    `json:"sha256,omitempty"`
	InstallPath              string    `json:"installPath,omitempty"`
	Note                     string    `json:"note,omitempty"`
	Enabled                  bool      `json:"enabled"`
	CreatedBy                *string   `json:"createdBy,omitempty"`
	CreatedByName            string    `json:"createdByName,omitempty"`
	UpdatedBy                *string   `json:"updatedBy,omitempty"`
	UpdatedByName            string    `json:"updatedByName,omitempty"`
	CreatedAt                time.Time `json:"createdAt"`
	UpdatedAt                time.Time `json:"updatedAt"`
}

type AllowAppListResponse struct {
	Apps       []AllowAppRow `json:"apps"`
	Total      int           `json:"total"`
	Page       int           `json:"page"`
	PerPage    int           `json:"perPage"`
	TotalPages int           `json:"totalPages"`
}

type CreateAllowAppRequest struct {
	// Preferred: pick from installed_applications catalog (same as /configuration/apps).
	InstalledApplicationID string `json:"installedApplicationId,omitempty"`
	// Legacy / lab: publisher and/or sha256 when no catalog id.
	Publisher string `json:"publisher,omitempty"`
	Product   string `json:"product,omitempty"`
	Sha256    string `json:"sha256,omitempty"`
	Path      string `json:"path,omitempty"`
	Note      string `json:"note,omitempty"`
	Enabled   *bool  `json:"enabled,omitempty"`
}

type UpdateAllowAppRequest struct {
	Publisher *string `json:"publisher,omitempty"`
	Product   *string `json:"product,omitempty"`
	Sha256    *string `json:"sha256,omitempty"`
	Path      *string `json:"path,omitempty"`
	Note      *string `json:"note,omitempty"`
	Enabled   *bool   `json:"enabled,omitempty"`
}

type AllowListQuery struct {
	Q         string
	CreatedBy string
	// Enabled: "" = all, "true" | "false"
	Enabled   string
	Page      int
	PerPage   int
}

type PauseLockdownRequest struct {
	// Minutes until lockdown resumes. Cap 240 (4h). Default 60 when omitted/zero.
	Minutes int `json:"minutes,omitempty"`
}

type PauseLockdownResponse struct {
	MachineID           string    `json:"machineId"`
	LockdownPausedUntil time.Time `json:"lockdownPausedUntil"`
}

// ── Device: Policy pull (wire bundle still JSON — assembled from relational rows) ─

type DlpPolicyPullResponse struct {
	PolicyVersion int64           `json:"policyVersion"`
	Hash          string          `json:"hash"`
	PublishedAt   *time.Time      `json:"publishedAt,omitempty"`
	Bundle        json.RawMessage `json:"bundle"`
	Unchanged     bool            `json:"unchanged,omitempty"`
	// LockdownPausedUntil is machine-scoped maintenance unlock. Omitted when not paused.
	LockdownPausedUntil *time.Time `json:"lockdownPausedUntil,omitempty"`
}

// ── Device: Alert sync ──────────────────────────────────────────────────────

// EvidenceMap accepts flat string maps OR nested JSON (arrays/objects/numbers)
// by stringifying non-string values. Prevents 400 when the Windows service
// sends evidence like {"domains":["youtube.com"]} from JsonSerializer.
type EvidenceMap map[string]string

func (m *EvidenceMap) UnmarshalJSON(data []byte) error {
	data = bytes.TrimSpace(data)
	if len(data) == 0 || bytes.Equal(data, []byte("null")) {
		*m = EvidenceMap{}
		return nil
	}
	var asStrings map[string]string
	if err := json.Unmarshal(data, &asStrings); err == nil {
		*m = EvidenceMap(asStrings)
		return nil
	}
	var raw map[string]json.RawMessage
	if err := json.Unmarshal(data, &raw); err != nil {
		return fmt.Errorf("evidence: %w", err)
	}
	out := make(EvidenceMap, len(raw))
	for k, v := range raw {
		v = bytes.TrimSpace(v)
		var s string
		if err := json.Unmarshal(v, &s); err == nil {
			out[k] = s
			continue
		}
		out[k] = string(v)
	}
	*m = out
	return nil
}

// FlexibleTime accepts RFC3339 with/without fractional seconds and without a zone
// (treats zoneless as UTC) so .NET System.Text.Json DateTime payloads bind cleanly.
type FlexibleTime struct{ time.Time }

func (t *FlexibleTime) UnmarshalJSON(data []byte) error {
	data = bytes.TrimSpace(data)
	if bytes.Equal(data, []byte("null")) {
		t.Time = time.Time{}
		return nil
	}
	var s string
	if err := json.Unmarshal(data, &s); err != nil {
		return err
	}
	s = strings.TrimSpace(s)
	if s == "" {
		t.Time = time.Time{}
		return nil
	}
	formats := []string{
		time.RFC3339Nano,
		time.RFC3339,
		"2006-01-02T15:04:05.999999999Z07:00",
		"2006-01-02T15:04:05.9999999Z",
		"2006-01-02T15:04:05Z",
		"2006-01-02T15:04:05.9999999",
		"2006-01-02T15:04:05",
	}
	var parseErr error
	for _, f := range formats {
		parsed, err := time.Parse(f, s)
		if err == nil {
			if parsed.Location() == time.Local && !strings.ContainsAny(s, "Z+-") {
				parsed = time.Date(
					parsed.Year(), parsed.Month(), parsed.Day(),
					parsed.Hour(), parsed.Minute(), parsed.Second(), parsed.Nanosecond(),
					time.UTC)
			}
			t.Time = parsed.UTC()
			return nil
		}
		parseErr = err
	}
	return fmt.Errorf("occurredAt: %w", parseErr)
}

func (t FlexibleTime) MarshalJSON() ([]byte, error) {
	if t.Time.IsZero() {
		return []byte("null"), nil
	}
	return json.Marshal(t.Time.UTC().Format(time.RFC3339Nano))
}

type SyncDlpAlertItem struct {
	ID            string       `json:"id"`
	RuleID        *string      `json:"ruleId,omitempty"`
	PolicyVersion int64        `json:"policyVersion"`
	Type          string       `json:"type"`
	Severity      string       `json:"severity"`
	ActionTaken   string       `json:"actionTaken"`
	OccurredAt    FlexibleTime `json:"occurredAt"`
	Evidence      EvidenceMap  `json:"evidence"`
}

type SyncDlpAlertsRequest struct {
	Alerts []SyncDlpAlertItem `json:"alerts"`
}

type SyncDlpAlertsResponse struct {
	AcceptedIDs []string `json:"acceptedIds"`
	RejectedIDs []string `json:"rejectedIds,omitempty"`
	Action      string   `json:"action,omitempty"`
}

// ── Device: Health ──────────────────────────────────────────────────────────

type DlpHealthRequest struct {
	PolicyVersion int64  `json:"policyVersion"`
	EnforceOK     bool   `json:"enforceOk"`
	LastError     string `json:"lastError,omitempty"`
	Platform      string `json:"platform,omitempty"`
	ServiceVer    string `json:"serviceVersion,omitempty"`
}

type DlpHealthResponse struct {
	OK            bool  `json:"ok"`
	LatestVersion int64 `json:"latestPolicyVersion"`
}

// ── Admin: Alerts ───────────────────────────────────────────────────────────

type UpdateDlpAlertRequest struct {
	Status     *string `json:"status,omitempty"`
	AssignedTo *string `json:"assignedTo,omitempty"`
	Notes      *string `json:"notes,omitempty"`
}

type DlpAlertHistoryEvent struct {
	ID            int64     `json:"id"`
	AlertID       string    `json:"alertId"`
	ChangedBy     *string   `json:"changedBy,omitempty"`
	ChangedByName string    `json:"changedByName"`
	Field         string    `json:"field"`
	OldValue      string    `json:"oldValue"`
	NewValue      string    `json:"newValue"`
	CreatedAt     time.Time `json:"createdAt"`
}

type DlpAlertHistoryResponse struct {
	Events []DlpAlertHistoryEvent `json:"events"`
}

type DlpAlertListResponse struct {
	Alerts        []any    `json:"alerts"`
	Total         int      `json:"total"`
	Page          int      `json:"page"`
	PerPage       int      `json:"perPage"`
	Open          int      `json:"open"`
	Investigating int      `json:"investigating"`
	Resolved      int      `json:"resolved"`
	FalsePositive int      `json:"falsePositive"`
	Types         []string `json:"types"`
}

// ── Shadow IT ───────────────────────────────────────────────────────────────

type UpdateShadowItAppRequest struct {
	AuthStatus *string `json:"authStatus,omitempty"`
	Risk       *string `json:"risk,omitempty"`
	Action     *string `json:"action,omitempty"` // approve | block | flag
}

type ShadowItListResponse struct {
	Apps         []any `json:"apps"`
	Total        int   `json:"total"`
	Page         int   `json:"page"`
	PerPage      int   `json:"perPage"`
	Approved     int   `json:"approved"`
	Unauthorized int   `json:"unauthorized"`
	UnderReview  int   `json:"underReview"`
}

// ── Removal codes ───────────────────────────────────────────────────────────

type IssueRemovalCodeRequest struct {
	MachineID  string `json:"machineId"`
	EmployeeID string `json:"employeeId"`
	Purpose    string `json:"purpose"`
	TTLMinutes int    `json:"ttlMinutes,omitempty"`
}

type IssueRemovalCodeResponse struct {
	Code      string    `json:"code"`
	ExpiresAt time.Time `json:"expiresAt"`
	Purpose   string    `json:"purpose"`
	MachineID string    `json:"machineId"`
}

type VerifyRemovalCodeRequest struct {
	Code    string `json:"code"`
	Purpose string `json:"purpose,omitempty"`
}

type VerifyRemovalCodeResponse struct {
	Valid     bool   `json:"valid"`
	Purpose   string `json:"purpose,omitempty"`
	MachineID string `json:"machineId,omitempty"`
}
