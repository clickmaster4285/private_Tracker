package dto

import "time"

type GeofenceZoneResponse struct {
	ID          int       `json:"id"`
	Name        string    `json:"name"`
	Latitude    float64   `json:"latitude"`
	Longitude   float64   `json:"longitude"`
	RadiusM     float64   `json:"radiusM"`
	AlertOnExit bool      `json:"alertOnExit"`
	CreatedAt   time.Time `json:"createdAt"`
	UpdatedAt   time.Time `json:"updatedAt"`
}

type CreateGeofenceZoneRequest struct {
	Name        string  `json:"name"`
	Latitude    float64 `json:"latitude"`
	Longitude   float64 `json:"longitude"`
	RadiusM     float64 `json:"radiusM"`
	AlertOnExit *bool   `json:"alertOnExit"`
}

type UpdateGeofenceZoneRequest struct {
	Name        *string  `json:"name"`
	Latitude    *float64 `json:"latitude"`
	Longitude   *float64 `json:"longitude"`
	RadiusM     *float64 `json:"radiusM"`
	AlertOnExit *bool    `json:"alertOnExit"`
}

type GeofenceZoneListResponse struct {
	Data []GeofenceZoneResponse `json:"data"`
}
