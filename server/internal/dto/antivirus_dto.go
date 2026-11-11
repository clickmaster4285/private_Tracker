package dto

import "time"

// ── Device sync (DeviceAuth) ────────────────────────────────────────────────

// SyncAntivirusStatusRequest is the latest engine state from one machine
// (POST /api/v1/antivirus/status/sync). EmployeeID / MachineID are stamped from
// DeviceAuth, never from the body.
type SyncAntivirusStatusRequest struct {
	EmployeeID string `json:"-"`
	MachineID  string `json:"-"`

	Health                   string     `json:"health"`
	ProductName              string     `json:"productName"`
	RunningMode              string     `json:"runningMode"`
	ServiceEnabled           bool       `json:"serviceEnabled"`
	AntivirusEnabled         bool       `json:"antivirusEnabled"`
	RealTimeEnabled          bool       `json:"realTimeEnabled"`
	BehaviorMonitorEnabled   bool       `json:"behaviorMonitorEnabled"`
	OnAccessEnabled          bool       `json:"onAccessEnabled"`
	IoavEnabled              bool       `json:"ioavEnabled"`
	TamperProtected          bool       `json:"tamperProtected"`
	CloudEnabled             bool       `json:"cloudEnabled"`
	PuaEnabled               bool       `json:"puaEnabled"`
	NetworkProtectionEnabled bool       `json:"networkProtectionEnabled"`
	SignatureVersion         string     `json:"signatureVersion"`
	SignatureAgeDays         int        `json:"signatureAgeDays"`
	SignatureUpdatedAt       *time.Time `json:"signatureUpdatedAt"`
	QuickScanAt              *time.Time `json:"quickScanAt"`
	FullScanAt               *time.Time `json:"fullScanAt"`
	EngineVersion            string     `json:"engineVersion"`
	ThirdPartyProducts       string     `json:"thirdPartyProducts"`
	ExclusionCount           int        `json:"exclusionCount"`
	AsrConfigured            int        `json:"asrConfigured"`
	AsrBlock                 int        `json:"asrBlock"`
	ControlledFolderAccess   string     `json:"controlledFolderAccess"`
	NetworkProtectionMode    string     `json:"networkProtectionMode"`
	SmartScreenEnabled       bool       `json:"smartScreenEnabled"`
	MemoryIntegrityEnabled   bool       `json:"memoryIntegrityEnabled"`
	CredentialGuardEnabled   bool       `json:"credentialGuardEnabled"`
	EmailScanEnabled         bool       `json:"emailScanEnabled"`
	ExploitProtectionEnabled bool       `json:"exploitProtectionEnabled"`
	QuarantinedThreatCount   int        `json:"quarantinedThreatCount"`
	HardeningEnabled         bool       `json:"hardeningEnabled"`
	GuardEnabled             bool       `json:"guardEnabled"`
	LastEnforceAt            *time.Time `json:"lastEnforceAt"`
	LastTamperAt             *time.Time `json:"lastTamperAt"`
	TamperCount24h           int        `json:"tamperCount24h"`
	AgentVersion             string     `json:"agentVersion"`
	Error                    string     `json:"error"`
	ReportedAt               *time.Time `json:"reportedAt"`
}

type SyncAntivirusStatusResponse struct {
	Accepted bool   `json:"accepted"`
	Action   string `json:"action,omitempty"`
}

// ── Admin reads (JWT) ───────────────────────────────────────────────────────

// AntivirusStatusRow is one machine in the fleet view. IsStale / Threats24h are
// server-projected (Server-Projected Flags Rule).
type AntivirusStatusRow struct {
	ID                       int64      `json:"id"`
	EmployeeID               string     `json:"employeeId"`
	EmployeeName             string     `json:"employeeName"`
	MachineID                string     `json:"machineId"`
	Hostname                 string     `json:"hostname"`
	Health                   string     `json:"health"`
	ProductName              string     `json:"productName"`
	RunningMode              string     `json:"runningMode"`
	RealTimeEnabled          bool       `json:"realTimeEnabled"`
	BehaviorMonitorEnabled   bool       `json:"behaviorMonitorEnabled"`
	OnAccessEnabled          bool       `json:"onAccessEnabled"`
	IoavEnabled              bool       `json:"ioavEnabled"`
	TamperProtected          bool       `json:"tamperProtected"`
	CloudEnabled             bool       `json:"cloudEnabled"`
	PuaEnabled               bool       `json:"puaEnabled"`
	NetworkProtectionEnabled bool       `json:"networkProtectionEnabled"`
	SignatureVersion         string     `json:"signatureVersion"`
	SignatureAgeDays         int        `json:"signatureAgeDays"`
	SignatureUpdatedAt       *time.Time `json:"signatureUpdatedAt"`
	QuickScanAt              *time.Time `json:"quickScanAt"`
	FullScanAt               *time.Time `json:"fullScanAt"`
	EngineVersion            string     `json:"engineVersion"`
	ThirdPartyProducts       string     `json:"thirdPartyProducts"`
	ExclusionCount           int        `json:"exclusionCount"`
	AsrConfigured            int        `json:"asrConfigured"`
	AsrBlock                 int        `json:"asrBlock"`
	ControlledFolderAccess   string     `json:"controlledFolderAccess"`
	NetworkProtectionMode    string     `json:"networkProtectionMode"`
	SmartScreenEnabled       bool       `json:"smartScreenEnabled"`
	MemoryIntegrityEnabled   bool       `json:"memoryIntegrityEnabled"`
	CredentialGuardEnabled   bool       `json:"credentialGuardEnabled"`
	EmailScanEnabled         bool       `json:"emailScanEnabled"`
	ExploitProtectionEnabled bool       `json:"exploitProtectionEnabled"`
	QuarantinedThreatCount   int        `json:"quarantinedThreatCount"`
	HardeningEnabled         bool       `json:"hardeningEnabled"`
	GuardEnabled             bool       `json:"guardEnabled"`
	LastEnforceAt            *time.Time `json:"lastEnforceAt"`
	LastTamperAt             *time.Time `json:"lastTamperAt"`
	TamperCount24h           int        `json:"tamperCount24h"`
	AgentVersion             string     `json:"agentVersion"`
	Error                    string     `json:"error"`
	ReportedAt               time.Time  `json:"reportedAt"`
	SyncedAt                 time.Time  `json:"syncedAt"`
	IsStale                  bool       `json:"isStale"`
	Threats24h               int        `json:"threats24h"`
}

// AntivirusHealthCounts — fleet summary chips (same filters minus health).
type AntivirusHealthCounts struct {
	Total       int `json:"total"`
	Protected   int `json:"protected"`
	Outdated    int `json:"outdated"`
	RealtimeOff int `json:"realtimeOff"`
	Disabled    int `json:"disabled"`
	ThirdParty  int `json:"thirdParty"`
	Unavailable int `json:"unavailable"`
	Stale       int `json:"stale"`
	Threats24h  int `json:"threats24h"`
	Tampered24h int `json:"tampered24h"`
}

type AntivirusStatusListResponse struct {
	Data       []AntivirusStatusRow  `json:"data"`
	Total      int                   `json:"total"`
	Page       int                   `json:"page"`
	PerPage    int                   `json:"perPage"`
	TotalPages int                   `json:"totalPages"`
	Counts     AntivirusHealthCounts `json:"counts"`
}

// ── Remote commands (admin-side fix) ────────────────────────────────────────

// CreateAntivirusCommandRequest queues a remote action for one machine (JWT).
type CreateAntivirusCommandRequest struct {
	EmployeeID string `json:"employeeId"`
	MachineID  string `json:"machineId"`
	Type       string `json:"type"`
	Arg        string `json:"arg"`
}

// AntivirusCommandRow is one queued/finished command (admin view).
type AntivirusCommandRow struct {
	ID           int64      `json:"id"`
	EmployeeID   string     `json:"employeeId"`
	MachineID    string     `json:"machineId"`
	Type         string     `json:"type"`
	Arg          string     `json:"arg"`
	Status       string     `json:"status"`
	Result       string     `json:"result"`
	RequestedBy  *string    `json:"requestedBy"`
	CreatedAt    time.Time  `json:"createdAt"`
	DispatchedAt *time.Time `json:"dispatchedAt"`
	CompletedAt  *time.Time `json:"completedAt"`
}

type AntivirusCommandListResponse struct {
	Data []AntivirusCommandRow `json:"data"`
}

// AntivirusCommandDevice is the slim shape the Windows service pulls (DeviceAuth).
type AntivirusCommandDevice struct {
	ID   int64  `json:"id"`
	Type string `json:"type"`
	Arg  string `json:"arg"`
}

type AntivirusCommandPullResponse struct {
	Commands []AntivirusCommandDevice `json:"commands"`
	Action   string                   `json:"action,omitempty"` // wipe_local when the device was removed
}

// AntivirusCommandResultRequest reports a command outcome from the device (DeviceAuth).
type AntivirusCommandResultRequest struct {
	ID     int64  `json:"id"`
	Status string `json:"status"` // running | done | failed
	Result string `json:"result"`
}
