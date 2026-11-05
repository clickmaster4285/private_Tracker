package dto

import "time"

// Re-export admin PATCH DTOs live in employee_dto.go (SetMachineTrackingMode*).

// DeviceControlResponse is GET /api/v1/devices/control (DeviceAuth).
type DeviceControlResponse struct {
	TrackingMode        string    `json:"trackingMode"`
	EffectiveModeSource string    `json:"effectiveModeSource"`
	StopWindow          string    `json:"stopWindow"`
	RequireStopReason   bool      `json:"requireStopReason"`
	KeepDlpWhenStopped  bool      `json:"keepDlpWhenStopped"`
	MaxStoppedHours     *int      `json:"maxStoppedHours,omitempty"`
	DesiredState        string    `json:"desiredState"`
	Phase               string    `json:"phase"`
	ControlEpoch        int64     `json:"controlEpoch"`
	ServerTime          time.Time `json:"serverTime"`
	Unchanged           bool      `json:"unchanged,omitempty"`
}

// TrackingEventEntry is one Start/Stop event from the client.
type TrackingEventEntry struct {
	ClientEventID     string  `json:"clientEventId"`
	EventType         string  `json:"eventType"`
	OccurredAt        string  `json:"occurredAt"`
	Reason            *string `json:"reason,omitempty"`
	WorkLocation      *string `json:"workLocation,omitempty"`
	LocationVerified  bool    `json:"locationVerified"`
	FlushResult       *string `json:"flushResult,omitempty"`
	UnsyncedRemaining *int    `json:"unsyncedRemaining,omitempty"`
	ClientVersion     *string `json:"clientVersion,omitempty"`
}

// SyncTrackingEventsRequest is POST /api/v1/devices/tracking-events.
type SyncTrackingEventsRequest struct {
	Events []TrackingEventEntry `json:"events"`
}

// SyncTrackingEventsResponse mirrors DLP alert ack (explicit accepted/rejected).
type SyncTrackingEventsResponse struct {
	AcceptedIDs []string `json:"acceptedIds"`
	RejectedIDs []string `json:"rejectedIds,omitempty"`
	Message     string   `json:"message,omitempty"`
}

// ── Admin Device Configuration surfaces (JWT) ───────────────────────────────

// RoamingMachineDetail is one physical PC under a roaming employee.
type RoamingMachineDetail struct {
	MachineID           string  `json:"machineId"`
	Hostname            string  `json:"hostname"`
	Status              string  `json:"status"`
	EffectiveMode       string  `json:"effectiveMode"`
	EffectiveModeSource string  `json:"effectiveModeSource"`
	DesiredState        string  `json:"desiredState"`
	Phase               string  `json:"phase"`
	TrackingStateSince  *string `json:"trackingStateSince,omitempty"`
	LastTrackAt         *string `json:"lastTrackAt,omitempty"`
	LastControlSeenAt   *string `json:"lastControlSeenAt,omitempty"`
}

// RoamingEmployeeRow is one employee with a current roaming PC + prior history.
// The list is employee-scoped (not one row per old machine).
type RoamingEmployeeRow struct {
	EmployeeUUID string                 `json:"employeeUuid"`
	EmployeeID   string                 `json:"employeeId"`
	EmployeeName string                 `json:"employeeName"`
	Department   string                 `json:"department"`
	Current      RoamingMachineDetail   `json:"current"`
	History      []RoamingMachineDetail `json:"history"`
}

// RoamingMachineListResponse is GET /device-configuration/roaming-machines.
// Total/page count employees (one row each), not raw machine rows.
type RoamingMachineListResponse struct {
	Employees  []RoamingEmployeeRow `json:"employees"`
	Total      int                  `json:"total"`
	Page       int                  `json:"page"`
	PerPage    int                  `json:"perPage"`
	TotalPages int                  `json:"totalPages"`
}

// TrackingEventLogRow is one Start/Stop audit row for the Logs tab.
type TrackingEventLogRow struct {
	ID             string  `json:"id"`
	EmployeeUUID   string  `json:"employeeUuid"`
	EmployeeID     string  `json:"employeeId"`
	EmployeeName   string  `json:"employeeName"`
	MachineID      string  `json:"machineId"`
	Hostname       string  `json:"hostname"`
	EventType      string  `json:"eventType"`
	OccurredAt     string  `json:"occurredAt"`
	ReceivedAt     string  `json:"receivedAt"`
	Reason         *string `json:"reason,omitempty"`
	FlushResult    *string `json:"flushResult,omitempty"`
	ClientVersion  *string `json:"clientVersion,omitempty"`
	WorkLocation   *string `json:"workLocation,omitempty"`
}

// TrackingEventLogListResponse is GET /device-configuration/tracking-events.
type TrackingEventLogListResponse struct {
	Events     []TrackingEventLogRow `json:"events"`
	Total      int                   `json:"total"`
	Page       int                   `json:"page"`
	PerPage    int                   `json:"perPage"`
	TotalPages int                   `json:"totalPages"`
}

// TrackerModeTypeInfo describes Fixed / Roaming for the Mode Types tab.
type TrackerModeTypeInfo struct {
	Mode        string `json:"mode"`
	Label       string `json:"label"`
	Summary     string `json:"summary"`
	CanStop     bool   `json:"canStop"`
	Details     string `json:"details"`
	OrgDefault  bool   `json:"orgDefault"`
}

// TrackerModeTypesResponse is GET /device-configuration/mode-types.
type TrackerModeTypesResponse struct {
	Modes           []TrackerModeTypeInfo `json:"modes"`
	OrgDefaultMode  string                `json:"orgDefaultMode"`
	OrgStopWindow   string                `json:"orgStopWindow"`
	KeepDlpWhenStopped bool               `json:"keepDlpWhenStopped"`
}
