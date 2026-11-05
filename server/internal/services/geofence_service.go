package services

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

type GeofenceService struct {
	repo *repository.GeofenceRepo
}

func NewGeofenceService(repo *repository.GeofenceRepo) *GeofenceService {
	return &GeofenceService{repo: repo}
}

func (s *GeofenceService) ListZones(ctx context.Context) (*dto.GeofenceZoneListResponse, error) {
	zones, err := s.repo.ListZones(ctx)
	if err != nil {
		return nil, err
	}
	out := make([]dto.GeofenceZoneResponse, len(zones))
	for i, z := range zones {
		out[i] = toGeofenceDTO(z)
	}
	return &dto.GeofenceZoneListResponse{Data: out}, nil
}

func (s *GeofenceService) CreateZone(ctx context.Context, req dto.CreateGeofenceZoneRequest) (*dto.GeofenceZoneResponse, error) {
	if req.Name == "" {
		return nil, fmt.Errorf("name is required")
	}
	if req.RadiusM <= 0 {
		req.RadiusM = 200
	}
	alert := true
	if req.AlertOnExit != nil {
		alert = *req.AlertOnExit
	}
	z, err := s.repo.CreateZone(ctx, models.GeofenceZone{
		Name:        req.Name,
		Latitude:    req.Latitude,
		Longitude:   req.Longitude,
		RadiusM:     req.RadiusM,
		AlertOnExit: alert,
	})
	if err != nil {
		return nil, err
	}
	resp := toGeofenceDTO(*z)
	return &resp, nil
}

func (s *GeofenceService) UpdateZone(ctx context.Context, id int, req dto.UpdateGeofenceZoneRequest) (*dto.GeofenceZoneResponse, error) {
	zones, err := s.repo.ListZones(ctx)
	if err != nil {
		return nil, err
	}
	var current *models.GeofenceZone
	for i := range zones {
		if zones[i].ID == id {
			current = &zones[i]
			break
		}
	}
	if current == nil {
		return nil, fmt.Errorf("geofence zone not found")
	}
	if req.Name != nil {
		current.Name = *req.Name
	}
	if req.Latitude != nil {
		current.Latitude = *req.Latitude
	}
	if req.Longitude != nil {
		current.Longitude = *req.Longitude
	}
	if req.RadiusM != nil {
		current.RadiusM = *req.RadiusM
	}
	if req.AlertOnExit != nil {
		current.AlertOnExit = *req.AlertOnExit
	}
	updated, err := s.repo.UpdateZone(ctx, id, *current)
	if err != nil {
		return nil, err
	}
	if updated == nil {
		return nil, fmt.Errorf("geofence zone not found")
	}
	resp := toGeofenceDTO(*updated)
	return &resp, nil
}

func (s *GeofenceService) DeleteZone(ctx context.Context, id int) error {
	return s.repo.DeleteZone(ctx, id)
}

// EvaluateSamplesOnIngest emits enter/exit geofence_events when a location sample crosses a zone boundary.
func (s *GeofenceService) EvaluateSamplesOnIngest(ctx context.Context, employeeID string, samples []models.LocationSample) error {
	zones, err := s.repo.ListZones(ctx)
	if err != nil || len(zones) == 0 {
		return err
	}
	for _, sample := range samples {
		for _, zone := range zones {
			inside := IsInsideGeofence(sample.Latitude, sample.Longitude, zone.Latitude, zone.Longitude, zone.RadiusM)
			last, err := s.repo.GetLastEventForZone(ctx, employeeID, zone.ID)
			if err != nil {
				return err
			}
			var wantType string
			if inside {
				if last == nil || last.EventType == "exit" {
					wantType = "enter"
				}
			} else if last != nil && last.EventType == "enter" && zone.AlertOnExit {
				wantType = "exit"
			}
			if wantType == "" {
				continue
			}
			sampleID := sample.ID
			if err := s.repo.InsertEvent(ctx, models.GeofenceEvent{
				ID:               newGeofenceEventID(),
				EmployeeID:       employeeID,
				GeofenceZoneID:   zone.ID,
				LocationSampleID: &sampleID,
				EventType:        wantType,
				OccurredAt:       sample.CapturedAt,
				Latitude:         sample.Latitude,
				Longitude:        sample.Longitude,
			}); err != nil {
				return err
			}
		}
	}
	return nil
}

func (s *GeofenceService) GeofenceLabel(ctx context.Context, lat, lon float64) (string, error) {
	name, err := s.repo.InsideZoneName(ctx, lat, lon, IsInsideGeofence)
	if err != nil {
		return "", err
	}
	if name != "" {
		return "Inside: " + name, nil
	}
	return "Outside", nil
}

func toGeofenceDTO(z models.GeofenceZone) dto.GeofenceZoneResponse {
	return dto.GeofenceZoneResponse{
		ID:          z.ID,
		Name:        z.Name,
		Latitude:    z.Latitude,
		Longitude:   z.Longitude,
		RadiusM:     z.RadiusM,
		AlertOnExit: z.AlertOnExit,
		CreatedAt:   z.CreatedAt,
		UpdatedAt:   z.UpdatedAt,
	}
}

func newGeofenceEventID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}
