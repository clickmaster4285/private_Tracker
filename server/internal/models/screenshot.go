package models

import "time"

// Screenshot is the Postgres index row for a WebP stored under SCREENSHOTS_DIR.
type Screenshot struct {
	ID          string    `json:"id"`
	EmployeeID  string    `json:"employeeId"`
	MachineID   string    `json:"machineId"`
	CapturedAt  time.Time `json:"capturedAt"`
	RelPath     string    `json:"relPath"`
	ByteSize    int64     `json:"byteSize"`
	Width       int       `json:"width"`
	Height      int       `json:"height"`
	ContentType string    `json:"contentType"`
	CreatedAt   time.Time `json:"createdAt"`
}

// ScreenshotListItem is the admin list DTO (metadata + lazy image URL).
type ScreenshotListItem struct {
	ID          string    `json:"id"`
	EmployeeID  string    `json:"employeeId"`
	EmployeeName string   `json:"employeeName,omitempty"`
	MachineID   string    `json:"machineId"`
	CapturedAt  time.Time `json:"capturedAt"`
	ByteSize    int64     `json:"byteSize"`
	Width       int       `json:"width"`
	Height      int       `json:"height"`
	ContentType string    `json:"contentType"`
	ImageURL    string    `json:"imageUrl"`
}
