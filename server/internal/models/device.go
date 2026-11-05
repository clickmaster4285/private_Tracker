package models

import "time"

// EmployeeDevice represents a registered employee desktop device.
type EmployeeDevice struct {
	ID            string     `json:"id" db:"id"`
	EmployeeID    string     `json:"employeeId" db:"employee_id"`
	MachineID     string     `json:"machineId" db:"machine_id"`
	Platform      string     `json:"platform" db:"platform"`
	ClientVersion string     `json:"clientVersion" db:"client_version"`
	DeviceName    string     `json:"deviceName" db:"device_name"`
	TokenHash     string     `json:"-" db:"token_hash"`
	CreatedAt     time.Time  `json:"createdAt" db:"created_at"`
	LastSeenAt    time.Time  `json:"lastSeenAt" db:"last_seen_at"`
	ExpiresAt     *time.Time `json:"expiresAt,omitempty" db:"expires_at"`
	RevokedAt     *time.Time `json:"revokedAt,omitempty" db:"revoked_at"`
	IsActiveDevice bool       `json:"isActiveDevice" db:"is_active_device"`
	RemoveAt      *time.Time `json:"removeAt" db:"remove_at"`
}

// MachineStatusActive / MachineStatusOld are the only values employee_machines.status may hold.
const (
	MachineStatusActive = "active"
	MachineStatusOld    = "old"
)

// EmployeeMachine is the canonical lifecycle record for a physical desktop machine
// an employee has used (one row per (employee_id, machine_id)). It is deliberately
// separate from EmployeeDevice: that table is the auth-token registry and rotates a
// row on every re-login, while this one answers "which machines has this employee
// had, and which is current?" — the model behind the web Device Specs dropdown.
type EmployeeMachine struct {
	ID              string     `json:"id" db:"id"`
	EmployeeID      string     `json:"employeeId" db:"employee_id"`
	MachineID       string     `json:"machineId" db:"machine_id"`
	Status          string     `json:"status" db:"status"`
	FirstTrackAt    time.Time  `json:"firstTrackAt" db:"first_track_at"`
	LastTrackAt     time.Time  `json:"lastTrackAt" db:"last_track_at"`
	WipeRequestedAt *time.Time `json:"wipeRequestedAt,omitempty" db:"wipe_requested_at"`
	// Migration 056 — Fixed/Roaming control plane (nullable until override set).
	TrackingModeOverride *string    `json:"trackingModeOverride,omitempty" db:"tracking_mode_override"`
	TrackingDesiredState string     `json:"trackingDesiredState" db:"tracking_desired_state"`
	TrackingPhase        string     `json:"trackingPhase" db:"tracking_phase"`
	TrackingStateSince   time.Time  `json:"trackingStateSince" db:"tracking_state_since"`
	LastControlSeenAt    *time.Time `json:"lastControlSeenAt,omitempty" db:"last_control_seen_at"`
	ControlVersion       int64      `json:"controlVersion" db:"control_version"`
	CreatedAt            time.Time  `json:"createdAt" db:"created_at"`
	UpdatedAt            time.Time  `json:"updatedAt" db:"updated_at"`
}
