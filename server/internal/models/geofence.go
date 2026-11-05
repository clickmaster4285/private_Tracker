package models

import "time"

type GeofenceZone struct {
	ID          int       `json:"id" db:"id"`
	Name        string    `json:"name" db:"name"`
	Latitude    float64   `json:"latitude" db:"latitude"`
	Longitude   float64   `json:"longitude" db:"longitude"`
	RadiusM     float64   `json:"radiusM" db:"radius_m"`
	AlertOnExit bool      `json:"alertOnExit" db:"alert_on_exit"`
	CreatedAt   time.Time `json:"createdAt" db:"created_at"`
	UpdatedAt   time.Time `json:"updatedAt" db:"updated_at"`
}

type GeofenceEvent struct {
	ID               string    `json:"id" db:"id"`
	EmployeeID       string    `json:"employeeId" db:"employee_id"`
	GeofenceZoneID   int       `json:"geofenceZoneId" db:"geofence_zone_id"`
	LocationSampleID *string   `json:"locationSampleId,omitempty" db:"location_sample_id"`
	EventType        string    `json:"eventType" db:"event_type"`
	OccurredAt       time.Time `json:"occurredAt" db:"occurred_at"`
	Latitude         float64   `json:"latitude" db:"latitude"`
	Longitude        float64   `json:"longitude" db:"longitude"`
	CreatedAt        time.Time `json:"createdAt" db:"created_at"`
}
