package dto

import "time"

// ────────────────────────────────
// Request DTOs
// ────────────────────────────────// CreateEmployeeRequest is the payload for creating a new employee.
// ShiftID is the FK to the shifts catalog (see migration 027_shifts.sql);
// nil means "no shift assigned".
type CreateEmployeeRequest struct {
	Name         string `json:"name"`
	Email        string `json:"email"`
	DepartmentID int    `json:"departmentId"`
	ShiftID      *int   `json:"shiftId,omitempty"`
}

// UpdateEmployeeRequest is the payload for updating an employee.
// A nil pointer means "leave the field as-is"; an explicit field with
// null/absent values for ShiftID is handled by the partial-update map.
type UpdateEmployeeRequest struct {
	Name            *string `json:"name,omitempty"`
	Email           *string `json:"email,omitempty"`
	DepartmentID    *int    `json:"departmentId,omitempty"`
	ShiftID         *int    `json:"shiftId,omitempty"`
	TrackingEnabled *bool   `json:"trackingEnabled,omitempty"`
	TrackingStatus  *string `json:"trackingStatus,omitempty"`
	IsOnline        *bool   `json:"isOnline,omitempty"`
}

// EmployeeLoginRequest is the payload for employee login via desktop client.
type EmployeeLoginRequest struct {
	EmployeeID    string `json:"employeeId"`
	SecretKey     string `json:"secretKey"`
	MachineID     string `json:"machineId,omitempty"`
	Platform      string `json:"platform,omitempty"`
	ClientVersion string `json:"clientVersion,omitempty"`
	DeviceName    string `json:"deviceName,omitempty"`
}

// GenerateSecretResponse is returned when generating a login secret.
type GenerateSecretResponse struct {
	Secret    string `json:"secret"`
	ExpiresIn int    `json:"expiresIn"` // seconds
}

// ────────────────────────────────
// Response DTOs
// ────────────────────────────────

// EmployeeResponse is the public API response for an employee.
// Shift is the resolved name (joined from shifts); ShiftID is the FK.
// A NULL shift renders as an empty `shift` string and a null `shiftId`.
type EmployeeResponse struct {
	ID              string     `json:"id"`
	EmployeeID      string     `json:"employeeId"`
	Name            string     `json:"name"`
	Email           string     `json:"email"`
	Department      string     `json:"department"`
	DepartmentID    int        `json:"departmentId"`
	ShiftID         *int       `json:"shiftId"`
	Shift           string     `json:"shift"`
	TrackingEnabled bool       `json:"trackingEnabled"`
	TrackingStatus  string     `json:"trackingStatus"`
	IsOnline        bool       `json:"isOnline"`
	ClientVersion   string     `json:"clientVersion"`
	Avatar          string     `json:"avatar"`
	AvatarColor     string     `json:"avatarColor"`
	HasUserLogin    bool       `json:"hasUserLogin"`
	FaceEnrolled    bool       `json:"faceEnrolled"`
	CreatedAt       time.Time  `json:"createdAt"`
	UpdatedAt       time.Time  `json:"updatedAt"`
	DeletedAt       *time.Time `json:"deletedAt"`
}

// EmployeeListResponse is a paginated list response.
type EmployeeListResponse struct {
	Data       []EmployeeResponse `json:"data"`
	Total      int                `json:"total"`
	Page       int                `json:"page"`
	PerPage    int                `json:"perPage"`
	TotalPages int                `json:"totalPages"`
}

// ────────────────────────────────
// Import / Export (web dashboard — Excel upload/download)
// ────────────────────────────────

// ImportEmployeeRow is a single normalized employee row from an Excel import.
// The employeeId is preserved verbatim from the spreadsheet (upsert key).
type ImportEmployeeRow struct {
	EmployeeID string `json:"employeeId"`
	Name       string `json:"name"`
	Email      string `json:"email"`
	Department string `json:"department"`
	Shift      string `json:"shift,omitempty"`
}

// ImportEmployeesRequest is the payload for bulk employee import.
type ImportEmployeesRequest struct {
	Employees []ImportEmployeeRow `json:"employees"`
}

// ImportRowResult reports the outcome of one imported row.
type ImportRowResult struct {
	RowIndex   int    `json:"rowIndex"`
	EmployeeID string `json:"employeeId"`
	Name       string `json:"name"`
	Status     string `json:"status"` // imported | updated | skipped
	Reason     string `json:"reason,omitempty"`
}

// ImportEmployeesResponse is the result of a bulk import.
type ImportEmployeesResponse struct {
	Imported int               `json:"imported"`
	Updated  int               `json:"updated"`
	Skipped  int               `json:"skipped"`
	Results  []ImportRowResult `json:"results"`
}

// EmployeeExportRow is a flat row for the Excel export.
type EmployeeExportRow struct {
	EmployeeID string `json:"employeeId"`
	Name       string `json:"name"`
	Email      string `json:"email"`
	Department string `json:"department"`
	Shift      string `json:"shift"`
}

// EmployeeLoginResponse is returned on successful employee login.
type EmployeeLoginResponse struct {
	Employee    EmployeeResponse `json:"employee"`
	Token       string           `json:"token,omitempty"`
	DeviceToken string           `json:"deviceToken,omitempty"`
	DeviceID    string           `json:"deviceId,omitempty"`
}

// ────────────────────────────────
// Employee Detail (web dashboard — GET /employees/:id/detail)
// Aggregate view of an employee plus every synced machine data surface.
// ────────────────────────────────

// DeviceHardwareDetail is the latest device_hardware_info snapshot for the employee's machine.
type DeviceHardwareDetail struct {
	ID             string     `json:"id"`
	MacAddress     string     `json:"macAddress"`
	Hostname       string     `json:"hostname"`
	OsName         string     `json:"osName"`
	OsVersion      string     `json:"osVersion"`
	CpuModel       string     `json:"cpuModel"`
	CpuCores       int        `json:"cpuCores"`
	RamTotalMb     int64      `json:"ramTotalMb"`
	StorageDevices string     `json:"storageDevices"`
	GpuModel       string     `json:"gpuModel"`
	GpuVramMb      int64      `json:"gpuVramMb"`
	CollectedAt    time.Time  `json:"collectedAt"`
	SyncedAt       *time.Time `json:"syncedAt,omitempty"`
}

// StorageDeviceDetail is a storage_devices child row (drive/model/capacity).
type StorageDeviceDetail struct {
	ID         string    `json:"id"`
	DeviceType string    `json:"deviceType"`
	Model      string    `json:"model"`
	CapacityMb int64     `json:"capacityMb"`
	CreatedAt  time.Time `json:"createdAt"`
}

// InstalledApplicationDetail is the joined catalog+junction view of one app on this employee's machine.
type InstalledApplicationDetail struct {
	ID          string     `json:"id"`
	AppName     string     `json:"appName"`
	BinaryName  string     `json:"binaryName,omitempty"`
	Version     string     `json:"version"`
	Publisher   string     `json:"publisher"`
	InstallPath string     `json:"installPath"`
	InstallDate *time.Time `json:"installDate,omitempty"`
	IsBrowser   bool       `json:"isBrowser"`
	Categories  string     `json:"categories,omitempty"`
	DesktopID   string     `json:"desktopId,omitempty"`
	FirstSeenAt time.Time  `json:"firstSeenAt"`
	LastSeenAt  time.Time  `json:"lastSeenAt"`
}

// InstalledPackageDetail is the joined catalog+junction view of one package on this employee's machine.
type InstalledPackageDetail struct {
	ID            string    `json:"id"`
	PackageName   string    `json:"packageName"`
	Version       string    `json:"version"`
	Category      string    `json:"category"`
	SourceManager string    `json:"sourceManager"`
	InstallPath   string    `json:"installPath"`
	Publisher     string    `json:"publisher"`
	Description   string    `json:"description"`
	FirstSeenAt   time.Time `json:"firstSeenAt"`
	LastSeenAt    time.Time `json:"lastSeenAt"`
}

// HardwareDeviceDetail is one peripheral (USB hotplug) row.
type HardwareDeviceDetail struct {
	ID          string     `json:"id"`
	DeviceClass string     `json:"deviceClass"`
	Vendor      string     `json:"vendor"`
	Product     string     `json:"product"`
	Serial      string     `json:"serial"`
	BusPath     string     `json:"busPath,omitempty"`
	PluggedAt   time.Time  `json:"pluggedAt"`
	UnpluggedAt *time.Time `json:"unpluggedAt,omitempty"`
}

// PermissionStatusDetail is one permission-method check row.
type PermissionStatusDetail struct {
	CheckID     string    `json:"checkId"`
	SessionID   string    `json:"sessionId"`
	SessionType string    `json:"sessionType"`
	Platform    string    `json:"platform"`
	CheckedAt   time.Time `json:"checkedAt"`
	Method      string    `json:"method"`
	Works       bool      `json:"works"`
	Details     string    `json:"details"`
}

// EmployeeActivityStats are derived counts over app_sessions / app_items.
type EmployeeActivityStats struct {
	TotalSessions  int        `json:"totalSessions"`
	OpenSessions   int        `json:"openSessions"`
	TotalItems     int        `json:"totalItems"`
	LastActivityAt *time.Time `json:"lastActivityAt,omitempty"`
}

// EmployeeDeviceDto is a registered device for the employee detail response.
// Hostname is the most recent hostname the machine itself reported; the web labels the
// device "{employee name} ({hostname})" so a device is never shown as "Unknown device".
// is_active_device tracks whether the device is still actively reporting.
// remove_at records when the device was marked inactive (for audit trail).
//
// Status / FirstTrackAt / LastTrackAt come from the canonical `employee_machines`
// lifecycle registry (migration 049): Status is "active" | "old", and the machine list
// is deduplicated to one entry per physical machine even though the auth-token registry
// (employee_devices) may hold several revoked rows for it.
type EmployeeDeviceDto struct {
	ID              string     `json:"id"`
	EmployeeID      string     `json:"employeeId"`
	MachineID       string     `json:"machineId"`
	Platform        string     `json:"platform"`
	ClientVersion   string     `json:"clientVersion"`
	DeviceName      string     `json:"deviceName"`
	Hostname        string     `json:"hostname"`
	LastSeenAt      time.Time  `json:"lastSeenAt"`
	RevokedAt       *time.Time `json:"revokedAt,omitempty"`
	IsActiveDevice  bool       `json:"isActiveDevice"`
	RemoveAt        *time.Time `json:"removeAt,omitempty"`
	Status          string     `json:"status"`
	FirstTrackAt    time.Time  `json:"firstTrackAt"`
	LastTrackAt     time.Time  `json:"lastTrackAt"`
	WipeRequestedAt *time.Time `json:"wipeRequestedAt,omitempty"`
}

// RemoveEmployeeDeviceResponse reports what "Remove current device" changed.
// Nothing is deleted: the counts describe revoked registrations and the
// sessions/items that were frozen because the machine can no longer report.
type RemoveEmployeeDeviceResponse struct {
	RevokedDevices int    `json:"revokedDevices"`
	ClosedSessions int    `json:"closedSessions"`
	ClosedItems    int    `json:"closedItems"`
	TrackingStatus string `json:"trackingStatus"`
}

// EmployeeDetailResponse is the full machine picture for one employee.
type EmployeeDetailResponse struct {
	Employee        EmployeeResponse             `json:"employee"`
	DeviceHardware  *DeviceHardwareDetail        `json:"deviceHardware,omitempty"`
	StorageDevices  []StorageDeviceDetail        `json:"storageDevices"`
	NetworkInfo     *NetworkInfoResponse         `json:"networkInfo,omitempty"`
	Applications    []InstalledApplicationDetail `json:"applications"`
	Packages        []InstalledPackageDetail     `json:"packages"`
	HardwareDevices []HardwareDeviceDetail       `json:"hardwareDevices"`
	AppStatus       map[string]string            `json:"appStatus"`
	Permissions     []PermissionStatusDetail     `json:"permissions"`
	Stats           EmployeeActivityStats        `json:"stats"`
	Devices         []EmployeeDeviceDto          `json:"devices"`
}
