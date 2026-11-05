package repository

import (
	"context"
	"fmt"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type GeofenceRepo struct {
	pool *pgxpool.Pool
}

func NewGeofenceRepo(pool *pgxpool.Pool) *GeofenceRepo {
	return &GeofenceRepo{pool: pool}
}

func (r *GeofenceRepo) ListZones(ctx context.Context) ([]models.GeofenceZone, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, name, latitude, longitude, radius_m, alert_on_exit, created_at, updated_at
		FROM geofence_zones
		WHERE deleted_at IS NULL
		ORDER BY name ASC
	`)
	if err != nil {
		return nil, fmt.Errorf("list geofence_zones: %w", err)
	}
	defer rows.Close()

	var zones []models.GeofenceZone
	for rows.Next() {
		var z models.GeofenceZone
		if err := rows.Scan(&z.ID, &z.Name, &z.Latitude, &z.Longitude, &z.RadiusM, &z.AlertOnExit, &z.CreatedAt, &z.UpdatedAt); err != nil {
			return nil, fmt.Errorf("scan geofence_zone: %w", err)
		}
		zones = append(zones, z)
	}
	return zones, rows.Err()
}

func (r *GeofenceRepo) CreateZone(ctx context.Context, z models.GeofenceZone) (*models.GeofenceZone, error) {
	err := r.pool.QueryRow(ctx, `
		INSERT INTO geofence_zones (name, latitude, longitude, radius_m, alert_on_exit)
		VALUES ($1, $2, $3, $4, $5)
		RETURNING id, name, latitude, longitude, radius_m, alert_on_exit, created_at, updated_at
	`, z.Name, z.Latitude, z.Longitude, z.RadiusM, z.AlertOnExit).Scan(
		&z.ID, &z.Name, &z.Latitude, &z.Longitude, &z.RadiusM, &z.AlertOnExit, &z.CreatedAt, &z.UpdatedAt,
	)
	if err != nil {
		return nil, fmt.Errorf("create geofence_zone: %w", err)
	}
	return &z, nil
}

func (r *GeofenceRepo) UpdateZone(ctx context.Context, id int, z models.GeofenceZone) (*models.GeofenceZone, error) {
	err := r.pool.QueryRow(ctx, `
		UPDATE geofence_zones
		SET name = $2, latitude = $3, longitude = $4, radius_m = $5, alert_on_exit = $6, updated_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING id, name, latitude, longitude, radius_m, alert_on_exit, created_at, updated_at
	`, id, z.Name, z.Latitude, z.Longitude, z.RadiusM, z.AlertOnExit).Scan(
		&z.ID, &z.Name, &z.Latitude, &z.Longitude, &z.RadiusM, &z.AlertOnExit, &z.CreatedAt, &z.UpdatedAt,
	)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("update geofence_zone: %w", err)
	}
	return &z, nil
}

func (r *GeofenceRepo) DeleteZone(ctx context.Context, id int) error {
	tag, err := r.pool.Exec(ctx, `
		UPDATE geofence_zones SET deleted_at = NOW(), updated_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL
	`, id)
	if err != nil {
		return fmt.Errorf("delete geofence_zone: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return pgx.ErrNoRows
	}
	return nil
}

func (r *GeofenceRepo) GetLastEventForZone(ctx context.Context, employeeID string, zoneID int) (*models.GeofenceEvent, error) {
	var e models.GeofenceEvent
	err := r.pool.QueryRow(ctx, `
		SELECT id, employee_id, geofence_zone_id, location_sample_id, event_type,
		       occurred_at, latitude, longitude, created_at
		FROM geofence_events
		WHERE employee_id = $1 AND geofence_zone_id = $2
		ORDER BY occurred_at DESC
		LIMIT 1
	`, employeeID, zoneID).Scan(
		&e.ID, &e.EmployeeID, &e.GeofenceZoneID, &e.LocationSampleID, &e.EventType,
		&e.OccurredAt, &e.Latitude, &e.Longitude, &e.CreatedAt,
	)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("get last geofence_event: %w", err)
	}
	return &e, nil
}

func (r *GeofenceRepo) InsertEvent(ctx context.Context, e models.GeofenceEvent) error {
	_, err := r.pool.Exec(ctx, `
		INSERT INTO geofence_events
			(id, employee_id, geofence_zone_id, location_sample_id, event_type, occurred_at, latitude, longitude)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
		ON CONFLICT (id) DO NOTHING
	`, e.ID, e.EmployeeID, e.GeofenceZoneID, e.LocationSampleID, e.EventType, e.OccurredAt, e.Latitude, e.Longitude)
	if err != nil {
		return fmt.Errorf("insert geofence_event: %w", err)
	}
	return nil
}

// InsideZoneName returns the first zone name containing the point, or empty string.
func (r *GeofenceRepo) InsideZoneName(ctx context.Context, lat, lon float64, insideFn func(float64, float64, float64, float64, float64) bool) (string, error) {
	zones, err := r.ListZones(ctx)
	if err != nil {
		return "", err
	}
	for _, z := range zones {
		if insideFn(lat, lon, z.Latitude, z.Longitude, z.RadiusM) {
			return z.Name, nil
		}
	}
	return "", nil
}

func NowUTC() time.Time { return time.Now().UTC() }
