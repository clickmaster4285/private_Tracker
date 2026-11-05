package models

import "time"

// EmployeeInstalledApplication is a junction row linking an employee to a deduplicated
// catalog application (installed_applications keyed by app_fingerprint). Per-install
// metadata that can legitimately differ between machines (version, path, date) lives here.
type EmployeeInstalledApplication struct {
	ID                     int64      `json:"id" db:"id"`
	EmployeeID             string     `json:"employeeId" db:"employee_id"`
	MachineID              string     `json:"machineId" db:"machine_id"`
	InstalledApplicationID string     `json:"installedApplicationId" db:"installed_application_id"`
	AppVersion             string     `json:"appVersion" db:"app_version"`
	Publisher              string     `json:"publisher" db:"publisher"`
	InstallPath            string     `json:"installPath" db:"install_path"`
	InstallDate            *time.Time `json:"installDate,omitempty" db:"install_date"`
	FirstSeenAt            time.Time  `json:"firstSeenAt" db:"first_seen_at"`
	LastSeenAt             time.Time  `json:"lastSeenAt" db:"last_seen_at"`
	IsActive               bool       `json:"isActive" db:"is_active"`
}

// EmployeeInstalledPackage is the package-table analogue of EmployeeInstalledApplication.
type EmployeeInstalledPackage struct {
	ID                 int64     `json:"id" db:"id"`
	EmployeeID         string    `json:"employeeId" db:"employee_id"`
	MachineID          string    `json:"machineId" db:"machine_id"`
	InstalledPackageID string    `json:"installedPackageId" db:"installed_package_id"`
	Version            string    `json:"version" db:"version"`
	Publisher          string    `json:"publisher" db:"publisher"`
	InstallPath        string    `json:"installPath" db:"install_path"`
	FirstSeenAt        time.Time `json:"firstSeenAt" db:"first_seen_at"`
	LastSeenAt         time.Time `json:"lastSeenAt" db:"last_seen_at"`
	IsActive           bool      `json:"isActive" db:"is_active"`
}

// EmployeeApplicationDetail is the joined catalog+junction view returned to the web dashboard:
// per-machine install metadata (version/path/date) from the junction row combined with the
// deduplicated catalog row's identity fields (app_name, binary_name, browser flag, categories).
type EmployeeApplicationDetail struct {
	ID          string     `json:"id" db:"id"`
	AppName     string     `json:"appName" db:"app_name"`
	BinaryName  string     `json:"binaryName,omitempty" db:"binary_name"`
	Version     string     `json:"version" db:"app_version"`
	Publisher   string     `json:"publisher" db:"publisher"`
	InstallPath string     `json:"installPath" db:"install_path"`
	InstallDate *time.Time `json:"installDate,omitempty" db:"install_date"`
	IsBrowser   bool       `json:"isBrowser" db:"is_browser"`
	Categories  string     `json:"categories,omitempty" db:"categories"`
	DesktopID   string     `json:"desktopId,omitempty" db:"desktop_id"`
	FirstSeenAt time.Time  `json:"firstSeenAt" db:"first_seen_at"`
	LastSeenAt  time.Time  `json:"lastSeenAt" db:"last_seen_at"`
}

// EmployeePackageDetail is the package-table analogue of EmployeeApplicationDetail.
type EmployeePackageDetail struct {
	ID            string    `json:"id" db:"id"`
	PackageName   string    `json:"packageName" db:"package_name"`
	Version       string    `json:"version" db:"version"`
	Category      string    `json:"category" db:"category"`
	SourceManager string    `json:"sourceManager" db:"source_manager"`
	InstallPath   string    `json:"installPath" db:"install_path"`
	Publisher     string    `json:"publisher" db:"publisher"`
	Description   string    `json:"description" db:"description"`
	FirstSeenAt   time.Time `json:"firstSeenAt" db:"first_seen_at"`
	LastSeenAt    time.Time `json:"lastSeenAt" db:"last_seen_at"`
}

// ActivityStats are derived counts over app_sessions / app_items for one employee.
type ActivityStats struct {
	TotalSessions  int        `json:"totalSessions" db:"total_sessions"`
	OpenSessions   int        `json:"openSessions" db:"open_sessions"`
	TotalItems     int        `json:"totalItems" db:"total_items"`
	LastActivityAt *time.Time `json:"lastActivityAt,omitempty" db:"last_activity_at"`
}
