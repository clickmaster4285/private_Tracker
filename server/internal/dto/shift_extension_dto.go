package dto

import "time"

// ShiftExtensionDay is one work date's extra time inside GET /schedules/me (PC side).
type ShiftExtensionDay struct {
	Date          string `json:"date"` // YYYY-MM-DD in the shift timezone
	BeforeMinutes int    `json:"beforeMinutes"`
	AfterMinutes  int    `json:"afterMinutes"`
}

// ShiftExtensionInput is the admin "Give extra time" request.
type ShiftExtensionInput struct {
	EmployeeID    string `json:"employeeId"`
	StartDate     string `json:"startDate"`         // YYYY-MM-DD
	EndDate       string `json:"endDate,omitempty"` // YYYY-MM-DD; empty = startDate
	BeforeMinutes int    `json:"beforeMinutes"`
	AfterMinutes  int    `json:"afterMinutes"`
	Reason        string `json:"reason"`
}

// ShiftExtensionResponse is one grant as shown on the web.
type ShiftExtensionResponse struct {
	ID            int64     `json:"id"`
	EmployeeID    string    `json:"employeeId"`
	StartDate     string    `json:"startDate"`
	EndDate       string    `json:"endDate"`
	BeforeMinutes int       `json:"beforeMinutes"`
	AfterMinutes  int       `json:"afterMinutes"`
	Reason        string    `json:"reason"`
	CreatedBy     string    `json:"createdBy"`
	CreatedAt     time.Time `json:"createdAt"`
}

type ShiftExtensionListResponse struct {
	Data []ShiftExtensionResponse `json:"data"`
}
