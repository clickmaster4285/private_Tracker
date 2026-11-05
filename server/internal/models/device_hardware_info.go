package models

import "time"

type DeviceHardwareInfo struct {
	ID             string     `json:"id" db:"id"`
	EmployeeID     string     `json:"employeeId" db:"employee_id"`
	MachineID      string     `json:"machineId" db:"machine_id"`
	DeviceID       string     `json:"deviceId" db:"device_id"`
	MacAddress     string     `json:"macAddress" db:"mac_address"`
	Hostname       string     `json:"hostname" db:"hostname"`
	OsName         string     `json:"osName" db:"os_name"`
	OsVersion      string     `json:"osVersion" db:"os_version"`
	CpuModel       string     `json:"cpuModel" db:"cpu_model"`
	CpuCores       int        `json:"cpuCores" db:"cpu_cores"`
	RamTotalMB     int64      `json:"ramTotalMb" db:"ram_total_mb"`
	StorageDevices string     `json:"storageDevices" db:"storage_devices"`
	GpuModel       string     `json:"gpuModel" db:"gpu_model"`
	GpuVramMB      int64      `json:"gpuVramMb" db:"gpu_vram_mb"`
	CollectedAt    time.Time  `json:"collectedAt" db:"collected_at"`
	SyncedAt       *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt      time.Time  `json:"createdAt" db:"created_at"`
}

type InstalledApplication struct {
	ID              string     `json:"id" db:"id"`
	EmployeeID      string     `json:"employeeId" db:"employee_id"`
	AppName         string     `json:"appName" db:"app_name"`
	AppVersion      string     `json:"appVersion" db:"app_version"`
	Publisher       string     `json:"publisher" db:"publisher"`
	InstallPath     string     `json:"installPath" db:"install_path"`
	InstallDate     *time.Time `json:"installDate,omitempty" db:"install_date"`
	UninstallString string     `json:"uninstallString" db:"uninstall_string"`
	ChangeType      string     `json:"changeType" db:"change_type"`
	DetectedAt      time.Time  `json:"detectedAt" db:"detected_at"`
	SyncedAt        *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt       time.Time  `json:"createdAt" db:"created_at"`
	BinaryName      string     `json:"binaryName,omitempty" db:"binary_name"`
	IsBrowser       bool       `json:"isBrowser" db:"is_browser"`
	DesktopID       string     `json:"desktopId,omitempty" db:"desktop_id"`
	Categories      string     `json:"categories,omitempty" db:"categories"`
	AppFingerprint  string     `json:"-" db:"app_fingerprint"`
}

type NetworkInfo struct {
	ID                   string     `json:"id" db:"id"`
	EmployeeID           string     `json:"employeeId" db:"employee_id"`
	MachineID            string     `json:"machineId" db:"machine_id"`
	PublicIP             string     `json:"publicIp" db:"public_ip"`
	PrivateIP            string     `json:"privateIp" db:"private_ip"`
	MacAddress           string     `json:"macAddress" db:"mac_address"`
	NetworkInterfaceName string     `json:"networkInterfaceName" db:"network_interface_name"`
	CollectedAt          time.Time  `json:"collectedAt" db:"collected_at"`
	SyncedAt             *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt            time.Time  `json:"createdAt" db:"created_at"`
}

type InstalledPackage struct {
	ID                 string     `json:"id" db:"id"`
	EmployeeID         string     `json:"employeeId" db:"employee_id"`
	PackageName        string     `json:"packageName" db:"package_name"`
	Version            string     `json:"version" db:"version"`
	Category           string     `json:"category" db:"category"`
	SourceManager      string     `json:"sourceManager" db:"source_manager"`
	InstallPath        string     `json:"installPath" db:"install_path"`
	Publisher          string     `json:"publisher" db:"publisher"`
	Description        string     `json:"description" db:"description"`
	DetectedAt         time.Time  `json:"detectedAt" db:"detected_at"`
	SyncedAt           *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt          time.Time  `json:"createdAt" db:"created_at"`
	PackageFingerprint string     `json:"-" db:"package_fingerprint"`
}

type SessionEvent struct {
	ID         string     `json:"id" db:"id"`
	EmployeeID string     `json:"employeeId" db:"employee_id"`
	MachineID  string     `json:"machineId" db:"machine_id"`
	EventType  string     `json:"eventType" db:"event_type"`
	OsUsername string     `json:"osUsername" db:"os_username"`
	EventAt    time.Time  `json:"eventAt" db:"event_at"`
	EventCount int        `json:"count" db:"event_count"`
	FirstAt    time.Time  `json:"firstAt" db:"first_at"`
	LastAt     time.Time  `json:"lastAt" db:"last_at"`
	SyncedAt   *time.Time `json:"syncedAt,omitempty" db:"synced_at"`
	CreatedAt  time.Time  `json:"createdAt" db:"created_at"`
}
