package models

import "time"

// Tracking mode / desired state / phase vocabulary — keep in sync with client enums
// and web types (R5 single source; add contract test in P2).

const (
	TrackingModeFixed   = "fixed"
	TrackingModeRoaming = "roaming"

	TrackingDesiredRunning = "running"
	TrackingDesiredStopped = "stopped"

	TrackingPhaseStarting   = "starting"
	TrackingPhaseCollecting = "collecting"
	TrackingPhaseDraining   = "draining"
	TrackingPhaseIdle       = "idle"
	TrackingPhaseError      = "error"

	TrackingEventStart = "start"
	TrackingEventStop  = "stop"

	StopWindowAnytime      = "anytime"
	StopWindowOutsideShift = "outside_shift"

	WorkLocationOnSite  = "on_site"
	WorkLocationRemote  = "remote"
	WorkLocationUnknown = "unknown"

	PolicyScopeOrg        = "org"
	PolicyScopeDepartment = "department"
	PolicyScopeEmployee   = "employee"
)

// DeviceTrackingPolicy is one row in the org→department→employee policy hierarchy.
type DeviceTrackingPolicy struct {
	ID                  string     `json:"id" db:"id"`
	ScopeType           string     `json:"scopeType" db:"scope_type"`
	ScopeID             string     `json:"scopeId" db:"scope_id"`
	TrackingMode        string     `json:"trackingMode" db:"tracking_mode"`
	StopWindow          string     `json:"stopWindow" db:"stop_window"`
	RequireStopReason   bool       `json:"requireStopReason" db:"require_stop_reason"`
	KeepDlpWhenStopped  bool       `json:"keepDlpWhenStopped" db:"keep_dlp_when_stopped"`
	MaxStoppedHours     *int       `json:"maxStoppedHours,omitempty" db:"max_stopped_hours"`
	UpdatedBy           *string    `json:"updatedBy,omitempty" db:"updated_by"`
	CreatedAt           time.Time  `json:"createdAt" db:"created_at"`
	UpdatedAt           time.Time  `json:"updatedAt" db:"updated_at"`
	DeletedAt           *time.Time `json:"deletedAt,omitempty" db:"deleted_at"`
}

// DeviceTrackingEvent is an append-only Start/Stop audit row (idempotent by ClientEventID).
type DeviceTrackingEvent struct {
	ID                string     `json:"id" db:"id"`
	EmployeeID        string     `json:"employeeId" db:"employee_id"`
	MachineID         string     `json:"machineId" db:"machine_id"`
	EventType         string     `json:"eventType" db:"event_type"`
	OccurredAt        time.Time  `json:"occurredAt" db:"occurred_at"`
	ReceivedAt        time.Time  `json:"receivedAt" db:"received_at"`
	ClientEventID     string     `json:"clientEventId" db:"client_event_id"`
	Reason            *string    `json:"reason,omitempty" db:"reason"`
	WorkLocation      *string    `json:"workLocation,omitempty" db:"work_location"`
	LocationVerified  bool       `json:"locationVerified" db:"location_verified"`
	FlushResult       *string    `json:"flushResult,omitempty" db:"flush_result"`
	UnsyncedRemaining *int       `json:"unsyncedRemaining,omitempty" db:"unsynced_remaining"`
	ClientVersion     *string    `json:"clientVersion,omitempty" db:"client_version"`
}

// EffectiveTrackingControl is the server-projected policy for one machine.
type EffectiveTrackingControl struct {
	TrackingMode       string
	EffectiveModeSource string // machine|employee|department|org|default
	StopWindow         string
	RequireStopReason  bool
	KeepDlpWhenStopped bool
	MaxStoppedHours    *int
	DesiredState       string
	Phase              string
	ControlEpoch       int64
	ControlVersion     int64
}
