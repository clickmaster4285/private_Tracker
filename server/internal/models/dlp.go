package models

import (
	"time"

	"github.com/jackc/pgx/v5/pgtype"
)

// DlpRule is an admin-editable non-web DLP policy row (USB, install, …).
type DlpRule struct {
	ID         string    `json:"id"`
	Name       string    `json:"name"`
	Enabled    bool      `json:"enabled"`
	Trigger    string    `json:"trigger"`
	Action     string    `json:"action"`
	Severity   string    `json:"severity"`
	ScopeAll   bool      `json:"scopeAll"`
	Extensions []string  `json:"extensions"`
	Patterns   []string  `json:"patterns"`
	CreatedAt  time.Time `json:"createdAt"`
	UpdatedAt  time.Time `json:"updatedAt"`
}

// DlpRuleRevision is a snapshot of a classic DLP rule taken before each UPDATE.
type DlpRuleRevision struct {
	ID            int64     `json:"id"`
	RuleID        string    `json:"ruleId"`
	Revision      int       `json:"revision"`
	Name          string    `json:"name"`
	Enabled       bool      `json:"enabled"`
	Trigger       string    `json:"trigger"`
	Action        string    `json:"action"`
	Severity      string    `json:"severity"`
	ScopeAll      bool      `json:"scopeAll"`
	Extensions    []string  `json:"extensions"`
	Patterns      []string  `json:"patterns"`
	ChangedBy     *string   `json:"changedBy,omitempty"`
	ChangedByName string    `json:"changedByName"`
	CreatedAt     time.Time `json:"createdAt"`
}

// WebBlockerRule is an admin-editable website block rule (dedicated tables).
type WebBlockerRule struct {
	ID                string    `json:"id"`
	Name              string    `json:"name"`
	Enabled           bool      `json:"enabled"`
	Action            string    `json:"action"`
	Severity          string    `json:"severity"`
	ScopeAll          bool      `json:"scopeAll"`
	Domains           []string  `json:"domains"`
	Paths             []string  `json:"paths"`
	ScheduleEnabled   bool      `json:"scheduleEnabled"`
	ScheduleStart     string    `json:"scheduleStart"`
	ScheduleEnd       string    `json:"scheduleEnd"`
	ScheduleTimezone  string    `json:"scheduleTimezone"`
	ScheduleDays      []string  `json:"scheduleDays"`
	CreatedAt         time.Time `json:"createdAt"`
	UpdatedAt         time.Time `json:"updatedAt"`
}

// WebBlockerRuleRevision is a snapshot of a rule taken before each UPDATE.
type WebBlockerRuleRevision struct {
	ID               int64     `json:"id"`
	RuleID           string    `json:"ruleId"`
	Revision         int       `json:"revision"`
	Name             string    `json:"name"`
	Enabled          bool      `json:"enabled"`
	Action           string    `json:"action"`
	Severity         string    `json:"severity"`
	ScopeAll         bool      `json:"scopeAll"`
	Domains          []string  `json:"domains"`
	Paths            []string  `json:"paths"`
	ScheduleEnabled  bool      `json:"scheduleEnabled"`
	ScheduleStart    string    `json:"scheduleStart"`
	ScheduleEnd      string    `json:"scheduleEnd"`
	ScheduleTimezone string    `json:"scheduleTimezone"`
	ScheduleDays     []string  `json:"scheduleDays"`
	ChangedBy        *string   `json:"changedBy,omitempty"`
	ChangedByName    string    `json:"changedByName"`
	CreatedAt        time.Time `json:"createdAt"`
}

// DlpPolicyVersion is an immutable published policy header (snapshot rows live in child tables).
type DlpPolicyVersion struct {
	Version     int64       `json:"policyVersion"`
	BundleHash  string      `json:"hash"`
	PublishedAt time.Time   `json:"publishedAt"`
	PublishedBy pgtype.UUID `json:"-"`
}

// DlpAlert is a device-synced incident.
type DlpAlert struct {
	ID            string            `json:"id"`
	RuleID        *string           `json:"ruleId,omitempty"`
	PolicyVersion int64             `json:"policyVersion"`
	EmployeeID    string            `json:"employeeId"`
	MachineID     string            `json:"machineId"`
	Type          string            `json:"type"`
	Severity      string            `json:"severity"`
	ActionTaken   string            `json:"actionTaken"`
	OccurredAt    time.Time         `json:"occurredAt"`
	Evidence      map[string]string `json:"evidence"`
	Status        string            `json:"status"`
	AssignedTo    string            `json:"assignedTo"`
	Notes         string            `json:"notes"`
	SyncedAt      time.Time         `json:"syncedAt"`
	CreatedAt     time.Time         `json:"createdAt"`
	UpdatedAt     time.Time         `json:"updatedAt"`
	EmployeeName  string            `json:"employeeName,omitempty"`
}

// DlpAlertEvent is one field change in the triage audit trail.
type DlpAlertEvent struct {
	ID            int64     `json:"id"`
	AlertID       string    `json:"alertId"`
	ChangedBy     *string   `json:"changedBy,omitempty"`
	ChangedByName string    `json:"changedByName"`
	Field         string    `json:"field"` // status | assigned_to | notes
	OldValue      string    `json:"oldValue"`
	NewValue      string    `json:"newValue"`
	CreatedAt     time.Time `json:"createdAt"`
}

// ShadowItApp is a Shadow IT catalog row.
type ShadowItApp struct {
	ID              int64     `json:"id"`
	Name            string    `json:"name"`
	NameNormalized  string    `json:"nameNormalized"`
	Kind            string    `json:"kind"`
	AuthStatus      string    `json:"authStatus"`
	Risk            string    `json:"risk"`
	FirstDetectedAt time.Time `json:"firstDetectedAt"`
	UserCount       int       `json:"userCount"`
	LastSeenAt      time.Time `json:"lastSeenAt"`
	CreatedAt       time.Time `json:"createdAt"`
	UpdatedAt       time.Time `json:"updatedAt"`
}
