package models

import "time"

// Employee represents a tracked employee in the system.
//
// Shift is the resolved shift name (denormalized at read time via LEFT JOIN
// shifts; see migration 027_shifts.sql). ShiftID is the FK that is the sole
// source of truth for the assignment — Shift is what the API surface carries.
type Employee struct {
	ID              string    `json:"id" db:"id"`
	EmployeeID      string    `json:"employeeId" db:"employee_id"`
	Name            string    `json:"name" db:"name"`
	Email           string    `json:"email" db:"email"`
	Department      string    `json:"department"`
	DepartmentID    int       `json:"departmentId" db:"department_id"`
	ShiftID         *int      `json:"shiftId" db:"shift_id"`
	Shift           string    `json:"shift" db:"shift"`
	TrackingEnabled bool      `json:"trackingEnabled" db:"tracking_enabled"`
	TrackingStatus  string    `json:"trackingStatus" db:"tracking_status"`
	IsOnline        bool      `json:"isOnline" db:"is_online"`
	ClientVersion   string    `json:"clientVersion" db:"client_version"`
	Avatar          string    `json:"avatar" db:"avatar"`
	AvatarColor     string    `json:"avatarColor" db:"avatar_color"`
	CreatedAt       time.Time    `json:"createdAt" db:"created_at"`
	UpdatedAt       time.Time    `json:"updatedAt" db:"updated_at"`
	DeletedAt       *time.Time   `json:"deletedAt" db:"deleted_at"`
	// HasUserLogin is true when a row in the `users` table exists for this
	// employee's employee_id. Projected via EXISTS(…) so it costs one indexed
	// probe per page row and scales to any number of employees.
	HasUserLogin   bool      `json:"hasUserLogin" db:"has_user_login"`
}

// EmployeePublic is the public-facing employee info.
type EmployeePublic struct {
	ID              string    `json:"id"`
	EmployeeID      string    `json:"employeeId"`
	Name            string    `json:"name"`
	Email           string    `json:"email"`
	Department      string    `json:"department"`
	DepartmentID    int       `json:"departmentId"`
	ShiftID         *int      `json:"shiftId"`
	Shift           string    `json:"shift"`
	TrackingEnabled bool      `json:"trackingEnabled"`
	TrackingStatus  string    `json:"trackingStatus"`
	IsOnline        bool      `json:"isOnline"`
	Avatar          string    `json:"avatar"`
	AvatarColor     string    `json:"avatarColor"`
	CreatedAt       time.Time `json:"createdAt"`
	UpdatedAt       time.Time `json:"updatedAt"`
}

// ToPublic converts an Employee to EmployeePublic.
func (e *Employee) ToPublic() EmployeePublic {
	return EmployeePublic{
		ID:              e.ID,
		EmployeeID:      e.EmployeeID,
		Name:            e.Name,
		Email:           e.Email,
		Department:      e.Department,
		DepartmentID:    e.DepartmentID,
		ShiftID:         e.ShiftID,
		Shift:           e.Shift,
		TrackingEnabled: e.TrackingEnabled,
		TrackingStatus:  e.TrackingStatus,
		IsOnline:        e.IsOnline,
		Avatar:          e.Avatar,
		AvatarColor:     e.AvatarColor,
		CreatedAt:       e.CreatedAt,
		UpdatedAt:       e.UpdatedAt,
	}
}
