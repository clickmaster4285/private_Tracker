package repository

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
)

// TrackingControlRepo owns device tracking policy / events / control epoch.
type TrackingControlRepo struct {
	pool *pgxpool.Pool
}

func NewTrackingControlRepo(pool *pgxpool.Pool) *TrackingControlRepo {
	return &TrackingControlRepo{pool: pool}
}

// GlobalEpoch returns device_control_meta.global_epoch (fail-safe 0).
func (r *TrackingControlRepo) GlobalEpoch(ctx context.Context) (int64, error) {
	var epoch int64
	err := r.pool.QueryRow(ctx, `SELECT global_epoch FROM device_control_meta WHERE id = 1`).Scan(&epoch)
	if errors.Is(err, pgx.ErrNoRows) {
		return 0, nil
	}
	return epoch, err
}

// BumpGlobalEpoch increments the cheap change detector (admin mutations — P2+).
func (r *TrackingControlRepo) BumpGlobalEpoch(ctx context.Context) (int64, error) {
	var epoch int64
	err := r.pool.QueryRow(ctx, `
		UPDATE device_control_meta
		SET global_epoch = global_epoch + 1, updated_at = NOW()
		WHERE id = 1
		RETURNING global_epoch`).Scan(&epoch)
	return epoch, err
}

// TouchControlSeen stamps last_control_seen_at for the machine (creates nothing).
func (r *TrackingControlRepo) TouchControlSeen(ctx context.Context, employeeID, machineID string) error {
	if employeeID == "" || machineID == "" {
		return nil
	}
	_, err := r.pool.Exec(ctx, `
		UPDATE employee_machines
		SET last_control_seen_at = NOW(), updated_at = NOW()
		WHERE employee_id = $1 AND machine_id = $2 AND deleted_at IS NULL`,
		employeeID, machineID)
	return err
}

// ResolveEffectiveControl projects mode/stop policy for one machine (server-projected).
// Fail-safe: missing policy hierarchy ⇒ fixed / anytime / keep DLP / running+collecting.
func (r *TrackingControlRepo) ResolveEffectiveControl(ctx context.Context, employeeID, machineID string) (*models.EffectiveTrackingControl, error) {
	out := &models.EffectiveTrackingControl{
		TrackingMode:        models.TrackingModeFixed,
		EffectiveModeSource: "default",
		StopWindow:          models.StopWindowAnytime,
		RequireStopReason:   false,
		KeepDlpWhenStopped:  true,
		DesiredState:        models.TrackingDesiredRunning,
		Phase:               models.TrackingPhaseCollecting,
		ControlEpoch:        0,
		ControlVersion:      0,
	}

	globalEpoch, err := r.GlobalEpoch(ctx)
	if err != nil {
		return nil, err
	}
	out.ControlEpoch = globalEpoch

	if employeeID == "" {
		return out, nil
	}

	var (
		modeOverride                                               *string
		desired, phase                                             *string
		controlVersion                                             *int64
		deptID                                                     *int
		empMode, empWindow                                         *string
		empRequire, empKeep                                        *bool
		empMax                                                     *int
		deptMode, deptWindow                                       *string
		deptRequire, deptKeep                                      *bool
		deptMax                                                    *int
		orgMode, orgWindow                                         *string
		orgRequire, orgKeep                                        *bool
		orgMax                                                     *int
	)

	err = r.pool.QueryRow(ctx, `
		SELECT
			em.tracking_mode_override,
			em.tracking_desired_state,
			em.tracking_phase,
			em.control_version,
			e.department_id,
			ep.tracking_mode, ep.stop_window, ep.require_stop_reason, ep.keep_dlp_when_stopped, ep.max_stopped_hours,
			dp.tracking_mode, dp.stop_window, dp.require_stop_reason, dp.keep_dlp_when_stopped, dp.max_stopped_hours,
			op.tracking_mode, op.stop_window, op.require_stop_reason, op.keep_dlp_when_stopped, op.max_stopped_hours
		FROM employees e
		LEFT JOIN employee_machines em
			ON em.employee_id = e.employee_id AND em.machine_id = $2 AND em.deleted_at IS NULL
		LEFT JOIN device_tracking_policies ep
			ON ep.scope_type = 'employee' AND ep.scope_id = e.employee_id AND ep.deleted_at IS NULL
		LEFT JOIN device_tracking_policies dp
			ON dp.scope_type = 'department' AND dp.scope_id = e.department_id::text AND dp.deleted_at IS NULL
		LEFT JOIN device_tracking_policies op
			ON op.scope_type = 'org' AND op.scope_id = '' AND op.deleted_at IS NULL
		WHERE e.employee_id = $1 AND e.deleted_at IS NULL
	`, employeeID, machineID).Scan(
		&modeOverride, &desired, &phase, &controlVersion, &deptID,
		&empMode, &empWindow, &empRequire, &empKeep, &empMax,
		&deptMode, &deptWindow, &deptRequire, &deptKeep, &deptMax,
		&orgMode, &orgWindow, &orgRequire, &orgKeep, &orgMax,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		out.ControlEpoch = maxInt64(globalEpoch, 0)
		return out, nil
	}
	if err != nil {
		return nil, err
	}

	if controlVersion != nil {
		out.ControlVersion = *controlVersion
		out.ControlEpoch = maxInt64(globalEpoch, *controlVersion)
	} else {
		out.ControlEpoch = globalEpoch
	}
	if desired != nil && *desired != "" {
		out.DesiredState = *desired
	}
	if phase != nil && *phase != "" {
		out.Phase = *phase
	}

	// Mode: machine override → employee → department → org → fixed
	switch {
	case modeOverride != nil && *modeOverride != "":
		out.TrackingMode = *modeOverride
		out.EffectiveModeSource = "machine"
	case empMode != nil && *empMode != "":
		out.TrackingMode = *empMode
		out.EffectiveModeSource = "employee"
	case deptMode != nil && *deptMode != "":
		out.TrackingMode = *deptMode
		out.EffectiveModeSource = "department"
	case orgMode != nil && *orgMode != "":
		out.TrackingMode = *orgMode
		out.EffectiveModeSource = "org"
	default:
		out.TrackingMode = models.TrackingModeFixed
		out.EffectiveModeSource = "default"
	}

	// Ancillary fields: same COALESCE chain (machine has no override columns for these).
	if w := coalesceStr(empWindow, deptWindow, orgWindow); w != "" {
		out.StopWindow = w
	}
	if b, ok := coalesceBool(empRequire, deptRequire, orgRequire); ok {
		out.RequireStopReason = b
	}
	if b, ok := coalesceBool(empKeep, deptKeep, orgKeep); ok {
		out.KeepDlpWhenStopped = b
	} else {
		out.KeepDlpWhenStopped = true
	}
	out.MaxStoppedHours = firstIntPtr(empMax, deptMax, orgMax)

	return out, nil
}

// InsertTrackingEvent inserts one event; duplicate client_event_id returns alreadyExists=true.
func (r *TrackingControlRepo) InsertTrackingEvent(ctx context.Context, ev *models.DeviceTrackingEvent) (alreadyExists bool, err error) {
	_, err = r.pool.Exec(ctx, `
		INSERT INTO device_tracking_events (
			id, employee_id, machine_id, event_type, occurred_at, received_at,
			client_event_id, reason, work_location, location_verified,
			flush_result, unsynced_remaining, client_version
		) VALUES (
			$1,$2,$3,$4,$5,NOW(),
			$6,$7,$8,$9,
			$10,$11,$12
		)`,
		ev.ID, ev.EmployeeID, ev.MachineID, ev.EventType, ev.OccurredAt,
		ev.ClientEventID, ev.Reason, ev.WorkLocation, ev.LocationVerified,
		ev.FlushResult, ev.UnsyncedRemaining, ev.ClientVersion,
	)
	if err != nil {
		var pgErr *pgconn.PgError
		if errors.As(err, &pgErr) && pgErr.Code == "23505" {
			return true, nil
		}
		return false, err
	}
	return false, nil
}

// UpdateMachineTrackingState sets desired/phase after an accepted event.
func (r *TrackingControlRepo) UpdateMachineTrackingState(ctx context.Context, employeeID, machineID, desired, phase string) error {
	if employeeID == "" || machineID == "" {
		return nil
	}
	_, err := r.pool.Exec(ctx, `
		UPDATE employee_machines
		SET tracking_desired_state = $3,
		    tracking_phase = $4,
		    tracking_state_since = NOW(),
		    updated_at = NOW()
		WHERE employee_id = $1 AND machine_id = $2 AND deleted_at IS NULL`,
		employeeID, machineID, desired, phase)
	return err
}

func maxInt64(a, b int64) int64 {
	if a > b {
		return a
	}
	return b
}

func coalesceStr(ptrs ...*string) string {
	for _, p := range ptrs {
		if p != nil && *p != "" {
			return *p
		}
	}
	return ""
}

func coalesceBool(ptrs ...*bool) (bool, bool) {
	for _, p := range ptrs {
		if p != nil {
			return *p, true
		}
	}
	return false, false
}

func firstIntPtr(ptrs ...*int) *int {
	for _, p := range ptrs {
		if p != nil {
			return p
		}
	}
	return nil
}

// EnsureMachineRow creates a lifecycle row if missing (login may not have run yet).
func (r *TrackingControlRepo) EnsureMachineRow(ctx context.Context, employeeID, machineID string) error {
	if employeeID == "" || machineID == "" {
		return nil
	}
	_, err := UpsertEmployeeMachineActive(ctx, r.pool, employeeID, machineID)
	return err
}

// SetMachineTrackingModeOverride sets or clears tracking_mode_override and bumps control_version.
// mode nil clears the override (inherit). Returns new machine control_version.
func (r *TrackingControlRepo) SetMachineTrackingModeOverride(ctx context.Context, employeeID, machineID string, mode *string) (int64, error) {
	if employeeID == "" || machineID == "" {
		return 0, fmt.Errorf("employee and machine required")
	}
	if err := r.EnsureMachineRow(ctx, employeeID, machineID); err != nil {
		return 0, err
	}
	var version int64
	err := r.pool.QueryRow(ctx, `
		UPDATE employee_machines
		SET tracking_mode_override = $3,
		    control_version = control_version + 1,
		    updated_at = NOW()
		WHERE employee_id = $1 AND machine_id = $2 AND deleted_at IS NULL
		RETURNING control_version`,
		employeeID, machineID, mode,
	).Scan(&version)
	if errors.Is(err, pgx.ErrNoRows) {
		return 0, fmt.Errorf("machine not found")
	}
	return version, err
}

// InsertPolicyAudit appends a device_policy_audit row (best-effort; caller may ignore error).
func (r *TrackingControlRepo) InsertPolicyAudit(ctx context.Context, action, scopeType, scopeID, machineID, employeeID string, oldVal, newVal any) error {
	_, err := r.pool.Exec(ctx, `
		INSERT INTO device_policy_audit (action, scope_type, scope_id, machine_id, employee_id, old_value, new_value)
		VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7::jsonb)`,
		action, scopeType, scopeID, machineID, employeeID, oldVal, newVal)
	return err
}

// NewEventID returns a UUID string for inserts.
func NewEventID() string {
	return uuid.NewString()
}

// FormatErr wraps repo errors for services.
func FormatTrackingErr(op string, err error) error {
	if err == nil {
		return nil
	}
	return fmt.Errorf("tracking_control %s: %w", op, err)
}

// ClampOccurredAt keeps client clocks sane (±24h around received).
func ClampOccurredAt(occurred, received time.Time) time.Time {
	const window = 24 * time.Hour
	if occurred.After(received.Add(window)) {
		return received
	}
	if occurred.Before(received.Add(-window)) {
		return received.Add(-window)
	}
	return occurred
}

// OrgDefaultPolicy returns the org-scope tracking policy (fail-safe fixed).
func (r *TrackingControlRepo) OrgDefaultPolicy(ctx context.Context) (mode, stopWindow string, keepDLP bool, err error) {
	mode = models.TrackingModeFixed
	stopWindow = models.StopWindowAnytime
	keepDLP = true
	var m, w *string
	var keep *bool
	err = r.pool.QueryRow(ctx, `
		SELECT tracking_mode, stop_window, keep_dlp_when_stopped
		FROM device_tracking_policies
		WHERE scope_type = 'org' AND scope_id = '' AND deleted_at IS NULL
		LIMIT 1`).Scan(&m, &w, &keep)
	if errors.Is(err, pgx.ErrNoRows) {
		return mode, stopWindow, keepDLP, nil
	}
	if err != nil {
		return mode, stopWindow, keepDLP, err
	}
	if m != nil && *m != "" {
		mode = *m
	}
	if w != nil && *w != "" {
		stopWindow = *w
	}
	if keep != nil {
		keepDLP = *keep
	}
	return mode, stopWindow, keepDLP, nil
}

// ListRoamingMachinesFilter scopes the roaming-PC admin list.
type ListRoamingMachinesFilter struct {
	Search  string
	Page    int
	PerPage int
}

// RoamingMachineScan is one row from ListRoamingMachines.
type RoamingMachineScan struct {
	EmployeeUUID        string
	EmployeeID          string
	EmployeeName        string
	Department          string
	MachineID           string
	Hostname            string
	Status              string
	EffectiveMode       string
	EffectiveModeSource string
	DesiredState        string
	Phase               string
	TrackingStateSince  *time.Time
	LastTrackAt         *time.Time
	LastControlSeenAt   *time.Time
}

// ListRoamingMachines returns one primary roaming machine per employee (active
// preferred, then most recently tracked), plus every other roaming machine for
// those employees so the UI can expand prior history. Pagination counts employees.
func (r *TrackingControlRepo) ListRoamingMachines(ctx context.Context, f ListRoamingMachinesFilter) ([]RoamingMachineScan, int, error) {
	page, perPage := f.Page, f.PerPage
	if page < 1 {
		page = 1
	}
	if perPage < 1 {
		perPage = 20
	}
	if perPage > 100 {
		perPage = 100
	}
	offset := (page - 1) * perPage
	search := strings.TrimSpace(f.Search)

	const resolvedCTE = `
		WITH resolved AS (
			SELECT
				e.id::text AS employee_uuid,
				e.employee_id,
				e.name AS employee_name,
				COALESCE(d.name, '') AS department,
				em.machine_id,
				COALESCE((
					SELECT h.hostname FROM device_hardware_info h
					WHERE h.employee_id = em.employee_id AND h.machine_id = em.machine_id
					  AND h.deleted_at IS NULL AND COALESCE(h.hostname, '') <> ''
					ORDER BY h.collected_at DESC NULLS LAST
					LIMIT 1
				), COALESCE((
					SELECT ed.device_name FROM employee_devices ed
					WHERE ed.employee_id = em.employee_id AND ed.machine_id = em.machine_id
					  AND COALESCE(ed.device_name, '') <> ''
					ORDER BY ed.last_seen_at DESC NULLS LAST
					LIMIT 1
				), '')) AS hostname,
				em.status,
				COALESCE(
					NULLIF(em.tracking_mode_override, ''),
					ep.tracking_mode,
					dp.tracking_mode,
					op.tracking_mode,
					'fixed'
				) AS effective_mode,
				CASE
					WHEN em.tracking_mode_override IS NOT NULL AND em.tracking_mode_override <> '' THEN 'machine'
					WHEN ep.tracking_mode IS NOT NULL THEN 'employee'
					WHEN dp.tracking_mode IS NOT NULL THEN 'department'
					WHEN op.tracking_mode IS NOT NULL THEN 'org'
					ELSE 'default'
				END AS effective_mode_source,
				em.tracking_desired_state,
				em.tracking_phase,
				em.tracking_state_since,
				em.last_track_at,
				em.last_control_seen_at
			FROM employee_machines em
			JOIN employees e ON e.employee_id = em.employee_id AND e.deleted_at IS NULL
			LEFT JOIN departments d ON d.id = e.department_id
			LEFT JOIN device_tracking_policies ep
				ON ep.scope_type = 'employee' AND ep.scope_id = e.employee_id AND ep.deleted_at IS NULL
			LEFT JOIN device_tracking_policies dp
				ON dp.scope_type = 'department' AND dp.scope_id = e.department_id::text AND dp.deleted_at IS NULL
			LEFT JOIN device_tracking_policies op
				ON op.scope_type = 'org' AND op.scope_id = '' AND op.deleted_at IS NULL
			WHERE em.deleted_at IS NULL
		),
		roaming AS (
			SELECT * FROM resolved
			WHERE effective_mode = 'roaming'
		),
		matched_employees AS (
			SELECT DISTINCT employee_id
			FROM roaming
			WHERE ($1 = '' OR employee_name ILIKE '%' || $1 || '%'
			     OR employee_id ILIKE '%' || $1 || '%'
			     OR machine_id ILIKE '%' || $1 || '%'
			     OR hostname ILIKE '%' || $1 || '%'
			     OR department ILIKE '%' || $1 || '%')
		),
		ranked AS (
			SELECT r.*,
				ROW_NUMBER() OVER (
					PARTITION BY r.employee_id
					ORDER BY CASE WHEN r.status = 'active' THEN 0 ELSE 1 END,
					         r.last_track_at DESC NULLS LAST,
					         r.machine_id ASC
				) AS rn
			FROM roaming r
			JOIN matched_employees m ON m.employee_id = r.employee_id
		)
	`

	countQ := resolvedCTE + `SELECT COUNT(*) FROM matched_employees`
	var total int
	if err := r.pool.QueryRow(ctx, countQ, search).Scan(&total); err != nil {
		return nil, 0, err
	}
	if total == 0 {
		return []RoamingMachineScan{}, 0, nil
	}

	// Page of employee_ids ordered by their primary machine's last_track_at.
	idsQ := resolvedCTE + `
		SELECT employee_id
		FROM ranked
		WHERE rn = 1
		ORDER BY last_track_at DESC NULLS LAST, employee_name ASC
		LIMIT $2 OFFSET $3`
	idRows, err := r.pool.Query(ctx, idsQ, search, perPage, offset)
	if err != nil {
		return nil, 0, err
	}
	pageIDs := make([]string, 0, perPage)
	for idRows.Next() {
		var id string
		if err := idRows.Scan(&id); err != nil {
			idRows.Close()
			return nil, 0, err
		}
		pageIDs = append(pageIDs, id)
	}
	idRows.Close()
	if err := idRows.Err(); err != nil {
		return nil, 0, err
	}
	if len(pageIDs) == 0 {
		return []RoamingMachineScan{}, total, nil
	}

	// All roaming machines for the paged employees (current + history).
	// array_position keeps the page order (primary last_track_at DESC).
	listQ := resolvedCTE + `
		SELECT employee_uuid, employee_id, employee_name, department, machine_id, hostname,
		       status, effective_mode, effective_mode_source,
		       tracking_desired_state, tracking_phase,
		       tracking_state_since, last_track_at, last_control_seen_at,
		       rn
		FROM ranked
		WHERE employee_id = ANY($2::text[])
		ORDER BY array_position($2::text[], employee_id),
		         CASE WHEN status = 'active' THEN 0 ELSE 1 END,
		         last_track_at DESC NULLS LAST,
		         machine_id ASC`
	rows, err := r.pool.Query(ctx, listQ, search, pageIDs)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	out := make([]RoamingMachineScan, 0)
	for rows.Next() {
		var row RoamingMachineScan
		var rn int
		if err := rows.Scan(
			&row.EmployeeUUID, &row.EmployeeID, &row.EmployeeName, &row.Department,
			&row.MachineID, &row.Hostname, &row.Status,
			&row.EffectiveMode, &row.EffectiveModeSource,
			&row.DesiredState, &row.Phase,
			&row.TrackingStateSince, &row.LastTrackAt, &row.LastControlSeenAt,
			&rn,
		); err != nil {
			return nil, 0, err
		}
		_ = rn
		out = append(out, row)
	}
	return out, total, rows.Err()
}

// ListTrackingEventsFilter scopes the Start/Stop audit log.
type ListTrackingEventsFilter struct {
	Search    string
	EmployeeID string
	MachineID string
	EventType string
	Page      int
	PerPage   int
}

// TrackingEventScan is one row from ListTrackingEvents.
type TrackingEventScan struct {
	ID            string
	EmployeeUUID  string
	EmployeeID    string
	EmployeeName  string
	MachineID     string
	Hostname      string
	EventType     string
	OccurredAt    time.Time
	ReceivedAt    time.Time
	Reason        *string
	FlushResult   *string
	ClientVersion *string
	WorkLocation  *string
}

// ListTrackingEvents returns Start/Stop audit rows newest-first.
func (r *TrackingControlRepo) ListTrackingEvents(ctx context.Context, f ListTrackingEventsFilter) ([]TrackingEventScan, int, error) {
	page, perPage := f.Page, f.PerPage
	if page < 1 {
		page = 1
	}
	if perPage < 1 {
		perPage = 30
	}
	if perPage > 100 {
		perPage = 100
	}
	offset := (page - 1) * perPage
	search := strings.TrimSpace(f.Search)
	emp := strings.TrimSpace(f.EmployeeID)
	machine := strings.TrimSpace(f.MachineID)
	et := strings.ToLower(strings.TrimSpace(f.EventType))
	if et != models.TrackingEventStart && et != models.TrackingEventStop {
		et = ""
	}

	countQ := `
		SELECT COUNT(*)
		FROM device_tracking_events te
		JOIN employees e ON e.employee_id = te.employee_id AND e.deleted_at IS NULL
		WHERE ($1 = '' OR e.name ILIKE '%' || $1 || '%'
		         OR te.employee_id ILIKE '%' || $1 || '%'
		         OR te.machine_id ILIKE '%' || $1 || '%')
		  AND ($2 = '' OR te.employee_id = $2)
		  AND ($3 = '' OR te.machine_id = $3)
		  AND ($4 = '' OR te.event_type = $4)`
	var total int
	if err := r.pool.QueryRow(ctx, countQ, search, emp, machine, et).Scan(&total); err != nil {
		return nil, 0, err
	}

	listQ := `
		SELECT te.id::text, e.id::text, te.employee_id, e.name, te.machine_id,
		       COALESCE((
					SELECT h.hostname FROM device_hardware_info h
					WHERE h.employee_id = te.employee_id AND h.machine_id = te.machine_id
					  AND h.deleted_at IS NULL AND COALESCE(h.hostname, '') <> ''
					ORDER BY h.collected_at DESC NULLS LAST
					LIMIT 1
		       ), ''),
		       te.event_type, te.occurred_at, te.received_at,
		       te.reason, te.flush_result, te.client_version, te.work_location
		FROM device_tracking_events te
		JOIN employees e ON e.employee_id = te.employee_id AND e.deleted_at IS NULL
		WHERE ($1 = '' OR e.name ILIKE '%' || $1 || '%'
		         OR te.employee_id ILIKE '%' || $1 || '%'
		         OR te.machine_id ILIKE '%' || $1 || '%')
		  AND ($2 = '' OR te.employee_id = $2)
		  AND ($3 = '' OR te.machine_id = $3)
		  AND ($4 = '' OR te.event_type = $4)
		ORDER BY te.occurred_at DESC
		LIMIT $5 OFFSET $6`
	rows, err := r.pool.Query(ctx, listQ, search, emp, machine, et, perPage, offset)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	out := make([]TrackingEventScan, 0, perPage)
	for rows.Next() {
		var row TrackingEventScan
		if err := rows.Scan(
			&row.ID, &row.EmployeeUUID, &row.EmployeeID, &row.EmployeeName, &row.MachineID,
			&row.Hostname, &row.EventType, &row.OccurredAt, &row.ReceivedAt,
			&row.Reason, &row.FlushResult, &row.ClientVersion, &row.WorkLocation,
		); err != nil {
			return nil, 0, err
		}
		out = append(out, row)
	}
	return out, total, rows.Err()
}
