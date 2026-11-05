package models

import "time"

type AppSession struct {
	ID               string     `json:"id" db:"id"`
	EmployeeID       string     `json:"employeeId" db:"employee_id"`
	ProcessName      string     `json:"processName" db:"process_name"`
	AppDisplayName   string     `json:"appDisplayName" db:"app_display_name"`
	StartedAt        time.Time  `json:"startedAt" db:"started_at"`
	EndedAt          *time.Time `json:"endedAt,omitempty" db:"ended_at"`
	MachineID        string     `json:"machineId" db:"machine_id"`
	SessionID        string     `json:"sessionId" db:"session_id"`
	Platform         string     `json:"platform" db:"platform"`
	ProcessID        *int       `json:"processId,omitempty" db:"process_id"`
	ParentProcessID  *int       `json:"parentProcessId,omitempty" db:"parent_process_id"`
	InstalledAppID   *string    `json:"installedAppId,omitempty" db:"installed_app_id"`
	InstalledPackageID *string  `json:"installedPackageId,omitempty" db:"installed_package_id"`
	GroupedBy        *string    `json:"groupedBy,omitempty" db:"grouped_by"`
	CgroupScope      *string    `json:"cgroupScope,omitempty" db:"cgroup_scope"`
	ContextLabel     *string    `json:"contextLabel,omitempty" db:"context_label"`
	// Foreground/background focus durations in seconds (2026-08-15). The desktop
	// client re-syncs rows as the totals grow and again on close.
	ForegroundSeconds  float64    `json:"foregroundSeconds" db:"foreground_seconds"`
	BackgroundSeconds  float64    `json:"backgroundSeconds" db:"background_seconds"`
	SyncedAt           *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	// 3-state lifecycle (2026-09-02): ACTIVE → STALE → CLOSED.
	// Server-side sweeper transitions status based on last_sync_at.
	// `ended_at` is only populated when status reaches CLOSED (client
	// may still push activity while STALE → status flips back to ACTIVE
	// and `ended_at` is cleared).
	Status          string     `json:"status" db:"status"`
	LastActivityAt  *time.Time `json:"lastActivityAt,omitempty" db:"last_activity_at"`
	LastSyncAt      *time.Time `json:"lastSyncAt,omitempty" db:"last_sync_at"`
	CreatedAt       time.Time  `json:"createdAt" db:"created_at"`
}

// AppItem is a generic child of AppSession, replacing BrowserContext, FileExplorerContext, UrlRecord, UrlVisit.
// Self-referencing via ParentItemID for nesting: app_session -> tab -> terminal/browser_navigation
// ItemType: 'tab', 'browser_tab', 'browser_navigation', 'terminal', 'folder', 'file', etc.
type AppItem struct {
	ID           string     `json:"id" db:"id"`
	EmployeeID   string     `json:"employeeId" db:"employee_id"`
	MachineID    string     `json:"machineId" db:"machine_id"`
	AppSessionID string     `json:"appSessionId" db:"app_session_id"`
	ParentItemID *string    `json:"parentItemId,omitempty" db:"parent_item_id"`
	ItemType     string     `json:"itemType" db:"item_type"`
	Title        string     `json:"title" db:"title"`
	Identifier   string     `json:"identifier" db:"identifier"`
	Url          string     `json:"url" db:"url"`
	Domain       string     `json:"domain" db:"domain"`
	OpenedAt     time.Time  `json:"openedAt" db:"opened_at"`
	ClosedAt     *time.Time `json:"closedAt,omitempty" db:"closed_at"`
	ProcessID    *int       `json:"processId,omitempty" db:"process_id"`
	ObjectType   string     `json:"objectType" db:"object_type"`
	Action       string     `json:"action" db:"action"`
	JourneyID    string     `json:"journeyId" db:"journey_id"`
	Sequence     int        `json:"sequence" db:"sequence"`
	PreviousPath string     `json:"previousPath" db:"previous_path"`
	CurrentPath  string     `json:"currentPath" db:"current_path"`
	WindowID     *int       `json:"windowId,omitempty" db:"window_id"`
	TabID        *int       `json:"tabId,omitempty" db:"tab_id"`
	MetadataJSON string     `json:"metadataJson" db:"metadata_json"`
	SyncedAt     *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt    time.Time  `json:"createdAt" db:"created_at"`
}
