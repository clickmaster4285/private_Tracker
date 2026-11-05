package repository

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type DeviceRepo struct {
	pool *pgxpool.Pool
}

func NewDeviceRepo(pool *pgxpool.Pool) *DeviceRepo {
	return &DeviceRepo{pool: pool}
}

// UpsertDevice creates a new active device record or updates an existing one for the same (employee_id, machine_id).
func (r *DeviceRepo) UpsertDevice(
	ctx context.Context,
	employeeID, machineID, platform, clientVersion, deviceName, tokenHash string,
	expiresAt *time.Time,
) (*models.EmployeeDevice, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	// Revoke any existing active device for this employee & machine so tokenHash is rotated safely
	_, _ = tx.Exec(ctx, `
		UPDATE employee_devices
		SET revoked_at = NOW()
		WHERE employee_id = $1 AND machine_id = $2 AND revoked_at IS NULL
	`, employeeID, machineID)

	query := `
		INSERT INTO employee_devices (
			employee_id, machine_id, platform, client_version, device_name, token_hash, expires_at, is_active_device, remove_at
		) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
		RETURNING id, employee_id, machine_id, platform, client_version, device_name,
		          token_hash, created_at, last_seen_at, expires_at, revoked_at, is_active_device, remove_at
	`

	var d models.EmployeeDevice
	err = tx.QueryRow(ctx, query,
		employeeID, machineID, platform, clientVersion, deviceName, tokenHash, expiresAt, true, nil,
	).Scan(
		&d.ID, &d.EmployeeID, &d.MachineID, &d.Platform, &d.ClientVersion, &d.DeviceName,
		&d.TokenHash, &d.CreatedAt, &d.LastSeenAt, &d.ExpiresAt, &d.RevokedAt,
		&d.IsActiveDevice, &d.RemoveAt,
	)
	if err != nil {
		return nil, fmt.Errorf("insert employee_device: %w", err)
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit tx: %w", err)
	}

	return &d, nil
}

// GetByTokenHash retrieves an unrevoked device by its token hash.
func (r *DeviceRepo) GetByTokenHash(ctx context.Context, tokenHash string) (*models.EmployeeDevice, error) {
	query := `
		SELECT id, employee_id, machine_id, platform, client_version, device_name,
		       token_hash, created_at, last_seen_at, expires_at, revoked_at, is_active_device, remove_at
		FROM employee_devices
		WHERE token_hash = $1 AND revoked_at IS NULL
	`

	var d models.EmployeeDevice
	err := r.pool.QueryRow(ctx, query, tokenHash).Scan(
		&d.ID, &d.EmployeeID, &d.MachineID, &d.Platform, &d.ClientVersion, &d.DeviceName,
		&d.TokenHash, &d.CreatedAt, &d.LastSeenAt, &d.ExpiresAt, &d.RevokedAt,
		&d.IsActiveDevice, &d.RemoveAt,
	)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, fmt.Errorf("query device by token: %w", err)
	}

	if d.ExpiresAt != nil && time.Now().After(*d.ExpiresAt) {
		return nil, nil // expired token
	}

	return &d, nil
}

// GetByTokenHashIncludingRevoked retrieves a device by its token hash even when the
// row has been revoked. DeviceAuth uses it to satisfy R6: a machine whose device was
// removed keeps authenticating long enough to push its final batch of data (which is
// persisted), and the middleware then flags `device_removed` so the response tells the
// client to wipe. Expiry is still honoured — an expired token never authenticates.
func (r *DeviceRepo) GetByTokenHashIncludingRevoked(ctx context.Context, tokenHash string) (*models.EmployeeDevice, error) {
	query := `
		SELECT id, employee_id, machine_id, platform, client_version, device_name,
		       token_hash, created_at, last_seen_at, expires_at, revoked_at, is_active_device, remove_at
		FROM employee_devices
		WHERE token_hash = $1
	`

	var d models.EmployeeDevice
	err := r.pool.QueryRow(ctx, query, tokenHash).Scan(
		&d.ID, &d.EmployeeID, &d.MachineID, &d.Platform, &d.ClientVersion, &d.DeviceName,
		&d.TokenHash, &d.CreatedAt, &d.LastSeenAt, &d.ExpiresAt, &d.RevokedAt,
		&d.IsActiveDevice, &d.RemoveAt,
	)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, fmt.Errorf("query device by token (incl revoked): %w", err)
	}

	if d.ExpiresAt != nil && time.Now().After(*d.ExpiresAt) {
		return nil, nil // expired token
	}

	return &d, nil
}

// ── employee_machines: canonical per-machine lifecycle (migration 049) ──

const employeeMachineColumns = `id, employee_id, machine_id, status, first_track_at,
	last_track_at, wipe_requested_at,
	created_at, updated_at`

func scanEmployeeMachine(row pgx.Row) (*models.EmployeeMachine, error) {
	var m models.EmployeeMachine
	err := row.Scan(
		&m.ID, &m.EmployeeID, &m.MachineID, &m.Status, &m.FirstTrackAt,
		&m.LastTrackAt, &m.WipeRequestedAt,
		&m.CreatedAt, &m.UpdatedAt,
	)
	if err != nil {
		return nil, err
	}
	return &m, nil
}

// UpsertEmployeeMachineActive records that (employee, machine) is tracked now: it
// creates the lifecycle row on first sight and, on every later login, preserves
// first_track_at, advances last_track_at and flips the status back to 'active'
// (a machine that was marked old and is used again becomes current once more).
func (r *DeviceRepo) UpsertEmployeeMachineActive(ctx context.Context, employeeID, machineID string) (*models.EmployeeMachine, error) {
	return UpsertEmployeeMachineActive(ctx, r.pool, employeeID, machineID)
}

// TouchEmployeeMachineLastTrack advances last_track_at for one machine. DeviceAuth
// calls it alongside TouchLastSeen so the registry's activity window stays current
// (migrations 047/050 attribute legacy rows against this window).
func (r *DeviceRepo) TouchEmployeeMachineLastTrack(ctx context.Context, employeeID, machineID string) error {
	return TouchEmployeeMachineLastTrack(ctx, r.pool, employeeID, machineID)
}

// ListEmployeeMachines returns one lifecycle row per physical machine, most recently
// tracked first — the source of the web Device Specs machine dropdown.
func (r *DeviceRepo) ListEmployeeMachines(ctx context.Context, employeeID string) ([]models.EmployeeMachine, error) {
	return ListEmployeeMachines(ctx, r.pool, employeeID)
}

// ── shared pool-level helpers (used by DeviceRepo, NewSchemaRepo and the services) ──

// UpsertEmployeeMachineActive is the pool-level implementation behind the repo methods.
func UpsertEmployeeMachineActive(ctx context.Context, pool *pgxpool.Pool, employeeID, machineID string) (*models.EmployeeMachine, error) {
	if employeeID == "" || machineID == "" {
		return nil, nil
	}
	query := `
		INSERT INTO employee_machines (employee_id, machine_id, status, first_track_at, last_track_at)
		VALUES ($1, $2, 'active', NOW(), NOW())
		ON CONFLICT (employee_id, machine_id) WHERE deleted_at IS NULL
		DO UPDATE SET status = 'active', last_track_at = NOW(), updated_at = NOW()
		RETURNING ` + employeeMachineColumns
	return scanEmployeeMachine(pool.QueryRow(ctx, query, employeeID, machineID))
}

// TouchEmployeeMachineLastTrack is the pool-level implementation behind the repo methods.
func TouchEmployeeMachineLastTrack(ctx context.Context, pool *pgxpool.Pool, employeeID, machineID string) error {
	if employeeID == "" || machineID == "" {
		return nil
	}
	_, err := pool.Exec(ctx, `
		UPDATE employee_machines
		SET last_track_at = NOW(), updated_at = NOW()
		WHERE employee_id = $1 AND machine_id = $2 AND deleted_at IS NULL
	`, employeeID, machineID)
	return err
}

// MarkEmployeeMachineOld flips one machine to 'old' and advances last_track_at.
// requestWipe additionally stamps wipe_requested_at (the R6 "Remove current device"
// trigger). A machine that is already 'old' keeps its original wipe_requested_at.
func MarkEmployeeMachineOld(ctx context.Context, pool *pgxpool.Pool, employeeID, machineID string, requestWipe bool) error {
	if employeeID == "" || machineID == "" {
		return nil
	}
	_, err := pool.Exec(ctx, `
		UPDATE employee_machines
		SET status = 'old',
		    last_track_at = NOW(),
		    wipe_requested_at = CASE WHEN $3 THEN COALESCE(wipe_requested_at, NOW()) ELSE wipe_requested_at END,
		    updated_at = NOW()
		WHERE employee_id = $1 AND machine_id = $2 AND deleted_at IS NULL
	`, employeeID, machineID, requestWipe)
	return err
}

// MarkAllEmployeeMachinesOld flips every ACTIVE machine of an employee to 'old'. Machines
// already 'old' are left untouched (their last_track_at / wipe_requested_at stay historical).
func MarkAllEmployeeMachinesOld(ctx context.Context, pool *pgxpool.Pool, employeeID string) error {
	if employeeID == "" {
		return nil
	}
	_, err := pool.Exec(ctx, `
		UPDATE employee_machines
		SET status = 'old', last_track_at = NOW(), updated_at = NOW()
		WHERE employee_id = $1 AND status = 'active' AND deleted_at IS NULL
	`, employeeID)
	return err
}

// MarkAllEmployeeMachinesOldWithWipe flips every ACTIVE machine of an employee to 'old' AND
// stamps wipe_requested_at on each — the "Remove current device" trigger (R6). The
// machines' next syncs persist their outstanding data and are answered wipe_local.
// Machines already 'old' are skipped so their last_track_at / wipe_requested_at are never
// clobbered by a later removal.
func MarkAllEmployeeMachinesOldWithWipe(ctx context.Context, pool *pgxpool.Pool, employeeID string) error {
	if employeeID == "" {
		return nil
	}
	_, err := pool.Exec(ctx, `
		UPDATE employee_machines
		SET status = 'old',
		    last_track_at = NOW(),
		    wipe_requested_at = COALESCE(wipe_requested_at, NOW()),
		    updated_at = NOW()
		WHERE employee_id = $1 AND status = 'active' AND deleted_at IS NULL
	`, employeeID)
	return err
}

// ListEmployeeMachines is the shared helper behind both repos' ListEmployeeMachines —
// canonical per-machine lifecycle rows (active first, then most recently tracked).
func ListEmployeeMachines(ctx context.Context, pool *pgxpool.Pool, employeeID string) ([]models.EmployeeMachine, error) {
	query := `SELECT ` + employeeMachineColumns + `
		FROM employee_machines
		WHERE employee_id = $1 AND deleted_at IS NULL
		ORDER BY (status = 'active') DESC, last_track_at DESC`

	rows, err := pool.Query(ctx, query, employeeID)
	if err != nil {
		return nil, fmt.Errorf("list employee machines: %w", err)
	}
	defer rows.Close()

	machines := make([]models.EmployeeMachine, 0)
	for rows.Next() {
		m, err := scanEmployeeMachine(rows)
		if err != nil {
			return nil, fmt.Errorf("scan employee machine: %w", err)
		}
		machines = append(machines, *m)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate employee machines: %w", err)
	}
	return machines, nil
}

// TouchLastSeen updates last_seen_at and optionally refreshes client_version / platform.
// Empty clientVersion or platform leave the existing column unchanged — so callers that
// only heartbeat (no identity headers) never wipe a known version.
func (r *DeviceRepo) TouchLastSeen(ctx context.Context, deviceID, clientVersion, platform string) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE employee_devices
		SET last_seen_at = NOW(),
		    client_version = CASE WHEN NULLIF(TRIM($2), '') IS NOT NULL THEN TRIM($2) ELSE client_version END,
		    platform = CASE WHEN NULLIF(TRIM($3), '') IS NOT NULL THEN TRIM($3) ELSE platform END
		WHERE id = $1 AND revoked_at IS NULL
	`, deviceID, clientVersion, platform)
	return err
}

// IsActive reports whether deviceID still exists, is unrevoked, and is unexpired.
// Used by long-lived DeviceAuth WebSockets to re-validate after upgrade (F14c).
func (r *DeviceRepo) IsActive(ctx context.Context, deviceID string) (bool, error) {
	var revokedAt *time.Time
	var expiresAt *time.Time
	err := r.pool.QueryRow(ctx, `
		SELECT revoked_at, expires_at
		FROM employee_devices
		WHERE id = $1
	`, deviceID).Scan(&revokedAt, &expiresAt)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return false, nil
		}
		return false, fmt.Errorf("device is-active: %w", err)
	}
	if revokedAt != nil {
		return false, nil
	}
	if expiresAt != nil && time.Now().After(*expiresAt) {
		return false, nil
	}
	return true, nil
}

// ListByEmployeeID returns all devices for an employee ordered by last_seen_at DESC.
func (r *DeviceRepo) ListByEmployeeID(ctx context.Context, employeeID string) ([]models.EmployeeDevice, error) {
	query := `
		SELECT id, employee_id, machine_id, platform, client_version, device_name,
		       token_hash, created_at, last_seen_at, expires_at, revoked_at, is_active_device, remove_at
		FROM employee_devices
		WHERE employee_id = $1
		ORDER BY last_seen_at DESC
	`

	rows, err := r.pool.Query(ctx, query, employeeID)
	if err != nil {
		return nil, fmt.Errorf("list employee devices: %w", err)
	}
	defer rows.Close()

	var devices []models.EmployeeDevice
	for rows.Next() {
		var d models.EmployeeDevice
		if err := rows.Scan(
			&d.ID, &d.EmployeeID, &d.MachineID, &d.Platform, &d.ClientVersion, &d.DeviceName,
			&d.TokenHash, &d.CreatedAt, &d.LastSeenAt, &d.ExpiresAt, &d.RevokedAt,
			&d.IsActiveDevice, &d.RemoveAt,
		); err != nil {
			return nil, fmt.Errorf("scan employee device: %w", err)
		}
		devices = append(devices, d)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate employee devices: %w", err)
	}

	return devices, nil
}

// RevokeDevice sets revoked_at for a specific device.
func (r *DeviceRepo) RevokeDevice(ctx context.Context, deviceID string) error {
	tag, err := r.pool.Exec(ctx, `
		UPDATE employee_devices
		SET revoked_at = NOW()
		WHERE id = $1 AND revoked_at IS NULL
	`, deviceID)
	if err != nil {
		return fmt.Errorf("revoke device: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("device not found or already revoked")
	}
	return nil
}
