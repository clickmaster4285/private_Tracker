package dto

import "time"

// ── Device sync (DeviceAuth) ────────────────────────────────────────────────

// PortEntry is one scanned port/device from a client machine.
type PortEntry struct {
	InstanceID   string `json:"instanceId"`
	Category     int    `json:"category"` // 1=external 2=internal 3=wireless/virtual
	PnpClass     string `json:"pnpClass"`
	ClassGuid    string `json:"classGuid"`
	BusType      string `json:"busType"`
	FriendlyName string `json:"friendlyName"`
	Manufacturer string `json:"manufacturer"`
	Present      bool   `json:"present"`
	Status       string `json:"status"`
	Blockable    bool   `json:"blockable"`
}

// SyncPortsRequest is POST /api/v1/hardware-ports/sync.
type SyncPortsRequest struct {
	EmployeeID string      `json:"employeeId"`
	Token      string      `json:"token"`
	MachineID  string      `json:"-"` // stamped from DeviceAuth, never the body
	Entries    []PortEntry `json:"entries"`
}

type SyncPortsResponse struct {
	Accepted int `json:"accepted"`
}

// ── Admin reads (JWT) ───────────────────────────────────────────────────────

// PortInventoryRow is one row in the per-machine port inventory.
type PortInventoryRow struct {
	ID           int64     `json:"id"`
	MachineID    string    `json:"machineId"`
	InstanceID   string    `json:"instanceId"`
	Category     int       `json:"category"`
	PnpClass     string    `json:"pnpClass"`
	ClassGuid    string    `json:"classGuid"`
	BusType      string    `json:"busType"`
	FriendlyName string    `json:"friendlyName"`
	Manufacturer string    `json:"manufacturer"`
	Present      bool      `json:"present"`
	Status       string    `json:"status"`
	Blockable    bool      `json:"blockable"`
	LastSeenAt   time.Time `json:"lastSeenAt"`
}

type PortInventoryResponse struct {
	Ports []PortInventoryRow `json:"ports"`
}

// DeviceControlRuleRow is one draft block/allow/alert rule.
type DeviceControlRuleRow struct {
	ID          int64     `json:"id"`
	TargetKind  string    `json:"targetKind"` // class | instance
	TargetValue string    `json:"targetValue"`
	Label       string    `json:"label"`
	PnpClass    string    `json:"pnpClass"`
	Action      string    `json:"action"` // block | allow | alert
	Scope       string    `json:"scope"`  // global | department | employee | machine
	ScopeValue  string    `json:"scopeValue"`
	Enabled     bool      `json:"enabled"`
	Note        string    `json:"note"`
	CreatedAt   time.Time `json:"createdAt"`
	UpdatedAt   time.Time `json:"updatedAt"`
}

type DeviceControlListResponse struct {
	Rules []DeviceControlRuleRow `json:"rules"`
}

type CreateDeviceControlRuleRequest struct {
	TargetKind  string `json:"targetKind"`
	TargetValue string `json:"targetValue"`
	Label       string `json:"label"`
	PnpClass    string `json:"pnpClass"`
	Action      string `json:"action"`
	Scope       string `json:"scope"`      // global | department | employee | machine (default global)
	ScopeValue  string `json:"scopeValue"` // dept id / employee_id / machine_id
	Note        string `json:"note"`
}

type UpdateDeviceControlRuleRequest struct {
	Action  *string `json:"action,omitempty"`
	Enabled *bool   `json:"enabled,omitempty"`
	Note    *string `json:"note,omitempty"`
}
