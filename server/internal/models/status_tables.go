package models

import "time"

// AppStatus — key/value status rows (heartbeat, login state, permission bookmarks).
// Natural key per employee: (employee_id, key). Ephemeral status.
// NOTE: the app_status table has NO synced_at column (migration 017) — the SyncedAt field is
// kept for API symmetry but must never be scanned (db:"-" prevents RowToStructByName traps).
type AppStatus struct {
	EmployeeID string     `json:"employeeId" db:"employee_id"`
	MachineID  string     `json:"machineId" db:"machine_id"`
	Key        string     `json:"key" db:"key"`
	Value      string     `json:"value" db:"value"`
	UpdatedAt  time.Time  `json:"updatedAt" db:"updated_at"`
	SyncedAt   *time.Time `json:"syncedAt,omitempty" db:"-"`
	CreatedAt  time.Time  `json:"createdAt" db:"created_at"`
}

// HardwareDevice — USB / peripheral hotplug history (plug-in & plug-out).
type HardwareDevice struct {
	ID           string     `json:"id" db:"id"`
	EmployeeID   string     `json:"employeeId" db:"employee_id"`
	MachineID    string     `json:"machineId" db:"machine_id"`
	DeviceClass  string     `json:"deviceClass" db:"device_class"`
	Vendor       string     `json:"vendor" db:"vendor"`
	Product      string     `json:"product" db:"product"`
	Serial       string     `json:"serial" db:"serial"`
	BusPath      string     `json:"busPath" db:"bus_path"`
	DeviceNode   string     `json:"deviceNode" db:"device_node"`
	PluggedAt    time.Time  `json:"pluggedAt" db:"plugged_at"`
	UnpluggedAt  *time.Time `json:"unpluggedAt,omitempty" db:"unplugged_at"`
	SyncedAt     *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt    time.Time  `json:"createdAt" db:"created_at"`
}

// PermissionStatus — one row per permission method per employee.
type PermissionStatus struct {
	CheckID      string     `json:"checkId" db:"check_id"`
	EmployeeID   string     `json:"employeeId" db:"employee_id"`
	MachineID    string     `json:"machineId" db:"machine_id"`
	SessionID    string     `json:"sessionId" db:"session_id"`
	SessionType  string     `json:"sessionType" db:"session_type"`
	Platform     string     `json:"platform" db:"platform"`
	CheckedAt    time.Time  `json:"checkedAt" db:"checked_at"`
	Method       string     `json:"method" db:"method"`
	Works        bool       `json:"works" db:"works"`
	Details      string     `json:"details" db:"details"`
	SyncedAt     *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt    time.Time  `json:"createdAt" db:"created_at"`
}

// StorageDevice — relational child of device_hardware_info.
type StorageDevice struct {
	ID               string     `json:"id" db:"id"`
	EmployeeID       string     `json:"employeeId" db:"employee_id"`
	MachineID        string     `json:"machineId" db:"machine_id"`
	DeviceHardwareID string     `json:"deviceHardwareId" db:"device_hardware_id"`
	DeviceType       string     `json:"deviceType" db:"device_type"`
	Model            string     `json:"model" db:"model"`
	CapacityMB       int64      `json:"capacityMb" db:"capacity_mb"`
	SyncedAt         *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt        time.Time  `json:"createdAt" db:"created_at"`
}

// LocationSample — GPS / WiFi / IP location fix from the desktop client.
type LocationSample struct {
	ID           string     `json:"id" db:"id"`
	EmployeeID   string     `json:"employeeId" db:"employee_id"`
	MachineID    string     `json:"machineId" db:"machine_id"`
	EmployeeName string     `json:"employeeName,omitempty" db:"-"`
	Latitude     float64    `json:"latitude" db:"latitude"`
	Longitude  float64    `json:"longitude" db:"longitude"`
	AccuracyM  *float64   `json:"accuracyM,omitempty" db:"accuracy_m"`
	AltitudeM  *float64   `json:"altitudeM,omitempty" db:"altitude_m"`
	Source     string     `json:"source" db:"source"`
	Address    *string    `json:"address,omitempty" db:"address"`
	CapturedAt time.Time  `json:"capturedAt" db:"captured_at"`
	SyncedAt   *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt  time.Time  `json:"createdAt" db:"created_at"`
}
