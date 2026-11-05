package models

import "time"

// Shift represents a work-shift definition in the company shift catalog.
// One row per named shift schedule; employees reference it via
// employees.shift_id (see migration 027_shifts.sql).
type Shift struct {
	ID            int        `json:"id" db:"id"`
	Name          string     `json:"name" db:"name"`
	StartTime     string     `json:"startTime" db:"start_time"`
	EndTime       string     `json:"endTime" db:"end_time"`
	WorkingDays   string     `json:"workingDays" db:"working_days"`
	Timezone      string     `json:"timezone" db:"timezone"`
	GraceMinutes  int        `json:"graceMinutes" db:"grace_minutes"`
	OvertimeHours int        `json:"overtimeHours" db:"overtime_hours"`
	Description   string     `json:"description" db:"description"`
	EmployeeCount int        `json:"employeeCount" db:"employee_count"`
	CreatedAt     time.Time  `json:"createdAt" db:"created_at"`
	UpdatedAt     time.Time  `json:"updatedAt" db:"updated_at"`
	DeletedAt     *time.Time `json:"deletedAt" db:"deleted_at"`
}
