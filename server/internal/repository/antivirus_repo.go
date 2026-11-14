package repository

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/jackc/pgx/v5/pgxpool"
)

// AntivirusStaleAfter — a machine that has not reported for this long is shown as
// stale (agent offline, service stopped, or PC switched off).
const AntivirusStaleAfter = 30 * time.Minute

type AntivirusRepo struct {
	pool *pgxpool.Pool
}

func NewAntivirusRepo(pool *pgxpool.Pool) *AntivirusRepo {
	return &AntivirusRepo{pool: pool}
}

var antivirusHealthValues = map[string]bool{
	"protected": true, "outdated": true, "realtime_off": true, "disabled": true,
	"third_party": true, "unavailable": true, "unknown": true,
}

// NormalizeAntivirusHealth maps an unknown client token to 'unknown' (CHECK constraint).
func NormalizeAntivirusHealth(h string) string {
	h = strings.ToLower(strings.TrimSpace(h))
	if antivirusHealthValues[h] {
		return h
	}
	return "unknown"
}

// normalizeMode coerces an ASR/CFA/network-protection mode to off|audit|block.
func normalizeMode(m string) string {
	switch strings.ToLower(strings.TrimSpace(m)) {
	case "audit":
		return "audit"
	case "block", "enabled", "on":
		return "block"
	default:
		return "off"
	}
}

// UpsertStatus stores the latest engine state for one machine.
func (r *AntivirusRepo) UpsertStatus(ctx context.Context, s *dto.SyncAntivirusStatusRequest) error {
	reported := time.Now().UTC()
	if s.ReportedAt != nil && !s.ReportedAt.IsZero() {
		reported = s.ReportedAt.UTC()
	}
	_, err := r.pool.Exec(ctx, `
		INSERT INTO antivirus_status (
		    employee_id, machine_id, health, product_name, running_mode,
		    service_enabled, antivirus_enabled, real_time_enabled, behavior_monitor_enabled,
		    on_access_enabled, ioav_enabled, tamper_protected, cloud_enabled, pua_enabled,
		    network_protection_enabled, signature_version, signature_age_days, signature_updated_at,
		    quick_scan_at, full_scan_at, engine_version, third_party_products, exclusion_count,
		    guard_enabled, last_enforce_at, last_tamper_at, tamper_count_24h, agent_version, error,
		    reported_at,
		    asr_configured, asr_block, controlled_folder_access, network_protection_mode,
		    smart_screen_enabled, memory_integrity_enabled, credential_guard_enabled,
		    email_scan_enabled, exploit_protection_enabled, quarantined_threat_count, hardening_enabled,
		    synced_at)
		VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,
		        $21,$22,$23,$24,$25,$26,$27,$28,$29,$30,
		        $31,$32,$33,$34,$35,$36,$37,$38,$39,$40,$41,NOW())
		ON CONFLICT (employee_id, machine_id) DO UPDATE SET
		    health = EXCLUDED.health, product_name = EXCLUDED.product_name,
		    running_mode = EXCLUDED.running_mode, service_enabled = EXCLUDED.service_enabled,
		    antivirus_enabled = EXCLUDED.antivirus_enabled, real_time_enabled = EXCLUDED.real_time_enabled,
		    behavior_monitor_enabled = EXCLUDED.behavior_monitor_enabled,
		    on_access_enabled = EXCLUDED.on_access_enabled, ioav_enabled = EXCLUDED.ioav_enabled,
		    tamper_protected = EXCLUDED.tamper_protected, cloud_enabled = EXCLUDED.cloud_enabled,
		    pua_enabled = EXCLUDED.pua_enabled,
		    network_protection_enabled = EXCLUDED.network_protection_enabled,
		    signature_version = EXCLUDED.signature_version,
		    signature_age_days = EXCLUDED.signature_age_days,
		    signature_updated_at = EXCLUDED.signature_updated_at,
		    quick_scan_at = EXCLUDED.quick_scan_at, full_scan_at = EXCLUDED.full_scan_at,
		    engine_version = EXCLUDED.engine_version,
		    third_party_products = EXCLUDED.third_party_products,
		    exclusion_count = EXCLUDED.exclusion_count, guard_enabled = EXCLUDED.guard_enabled,
		    last_enforce_at = EXCLUDED.last_enforce_at,
		    last_tamper_at = COALESCE(EXCLUDED.last_tamper_at, antivirus_status.last_tamper_at),
		    tamper_count_24h = EXCLUDED.tamper_count_24h, agent_version = EXCLUDED.agent_version,
		    error = EXCLUDED.error, reported_at = EXCLUDED.reported_at,
		    asr_configured = EXCLUDED.asr_configured, asr_block = EXCLUDED.asr_block,
		    controlled_folder_access = EXCLUDED.controlled_folder_access,
		    network_protection_mode = EXCLUDED.network_protection_mode,
		    smart_screen_enabled = EXCLUDED.smart_screen_enabled,
		    memory_integrity_enabled = EXCLUDED.memory_integrity_enabled,
		    credential_guard_enabled = EXCLUDED.credential_guard_enabled,
		    email_scan_enabled = EXCLUDED.email_scan_enabled,
		    exploit_protection_enabled = EXCLUDED.exploit_protection_enabled,
		    quarantined_threat_count = EXCLUDED.quarantined_threat_count,
		    hardening_enabled = EXCLUDED.hardening_enabled, synced_at = NOW()`,
		s.EmployeeID, s.MachineID, NormalizeAntivirusHealth(s.Health), trimTo(s.ProductName, 200),
		trimTo(s.RunningMode, 100), s.ServiceEnabled, s.AntivirusEnabled, s.RealTimeEnabled,
		s.BehaviorMonitorEnabled, s.OnAccessEnabled, s.IoavEnabled, s.TamperProtected, s.CloudEnabled,
		s.PuaEnabled, s.NetworkProtectionEnabled, trimTo(s.SignatureVersion, 100), s.SignatureAgeDays,
		s.SignatureUpdatedAt, s.QuickScanAt, s.FullScanAt, trimTo(s.EngineVersion, 100),
		trimTo(s.ThirdPartyProducts, 500), s.ExclusionCount, s.GuardEnabled, s.LastEnforceAt,
		s.LastTamperAt, s.TamperCount24h, trimTo(s.AgentVersion, 50), trimTo(s.Error, 500), reported,
		s.AsrConfigured, s.AsrBlock, normalizeMode(s.ControlledFolderAccess), normalizeMode(s.NetworkProtectionMode),
		s.SmartScreenEnabled, s.MemoryIntegrityEnabled, s.CredentialGuardEnabled,
		s.EmailScanEnabled, s.ExploitProtectionEnabled, s.QuarantinedThreatCount, s.HardeningEnabled)
	return err
}

// AntivirusListFilter — fleet view filters (URL-synced on the web).
type AntivirusListFilter struct {
	Search  string // employee name / id / hostname
	Health  string // health token, or "stale"
	Page    int
	PerPage int
}

func (r *AntivirusRepo) buildWhere(f AntivirusListFilter, withHealth bool) (string, []any) {
	conds := []string{"1=1"}
	args := []any{}
	if q := strings.TrimSpace(f.Search); q != "" {
		args = append(args, "%"+strings.ToLower(q)+"%")
		n := len(args)
		conds = append(conds, fmt.Sprintf(
			"(LOWER(COALESCE(e.name,'')) LIKE $%d OR LOWER(a.employee_id) LIKE $%d OR LOWER(a.machine_id) LIKE $%d OR LOWER(COALESCE(h.device_name,'')) LIKE $%d)",
			n, n, n, n))
	}
	if withHealth {
		switch h := strings.TrimSpace(f.Health); {
		case h == "stale":
			args = append(args, AntivirusStaleAfter.Seconds())
			conds = append(conds, fmt.Sprintf("a.synced_at < NOW() - make_interval(secs => $%d)", len(args)))
		case h != "":
			args = append(args, NormalizeAntivirusHealth(h))
			conds = append(conds, fmt.Sprintf("a.health = $%d", len(args)))
		}
	}
	return strings.Join(conds, " AND "), args
}

const antivirusFrom = `
	FROM antivirus_status a
	LEFT JOIN employees e ON e.employee_id = a.employee_id AND e.deleted_at IS NULL
	LEFT JOIN LATERAL (
	    SELECT d.device_name FROM employee_devices d
	    WHERE d.employee_id = a.employee_id AND d.machine_id = a.machine_id AND d.device_name <> ''
	    ORDER BY d.last_seen_at DESC LIMIT 1
	) h ON TRUE`

// ListStatus returns one page of the fleet view + health counts for the same filters
// (minus the health filter so the chips stay meaningful).
func (r *AntivirusRepo) ListStatus(ctx context.Context, f AntivirusListFilter) ([]dto.AntivirusStatusRow, int, dto.AntivirusHealthCounts, error) {
	var counts dto.AntivirusHealthCounts
	if f.Page < 1 {
		f.Page = 1
	}
	if f.PerPage < 1 || f.PerPage > 100 {
		f.PerPage = 30
	}

	cw, cargs := r.buildWhere(f, false)
	cargs = append(cargs, AntivirusStaleAfter.Seconds())
	staleArg := len(cargs)
	countSQL := fmt.Sprintf(`
		SELECT COUNT(*),
		       COUNT(*) FILTER (WHERE a.health = 'protected'),
		       COUNT(*) FILTER (WHERE a.health = 'outdated'),
		       COUNT(*) FILTER (WHERE a.health = 'realtime_off'),
		       COUNT(*) FILTER (WHERE a.health = 'disabled'),
		       COUNT(*) FILTER (WHERE a.health = 'third_party'),
		       COUNT(*) FILTER (WHERE a.health IN ('unavailable','unknown')),
		       COUNT(*) FILTER (WHERE a.synced_at < NOW() - make_interval(secs => $%d)),
		       COUNT(*) FILTER (WHERE a.last_tamper_at > NOW() - INTERVAL '24 hours')
		%s WHERE %s`, staleArg, antivirusFrom, cw)
	if err := r.pool.QueryRow(ctx, countSQL, cargs...).Scan(
		&counts.Total, &counts.Protected, &counts.Outdated, &counts.RealtimeOff, &counts.Disabled,
		&counts.ThirdParty, &counts.Unavailable, &counts.Stale, &counts.Tampered24h,
	); err != nil {
		return nil, 0, counts, err
	}
	if err := r.pool.QueryRow(ctx, `
		SELECT COUNT(*) FROM dlp_alerts
		WHERE type = 'malware' AND occurred_at > NOW() - INTERVAL '24 hours'`).Scan(&counts.Threats24h); err != nil {
		return nil, 0, counts, err
	}

	wsql, args := r.buildWhere(f, true)
	var total int
	if err := r.pool.QueryRow(ctx, "SELECT COUNT(*) "+antivirusFrom+" WHERE "+wsql, args...).Scan(&total); err != nil {
		return nil, 0, counts, err
	}

	args = append(args, AntivirusStaleAfter.Seconds(), f.PerPage, (f.Page-1)*f.PerPage)
	n := len(args)
	listSQL := fmt.Sprintf(`
		SELECT a.id, a.employee_id, COALESCE(e.name,''), a.machine_id, COALESCE(h.device_name,''),
		       a.health, a.product_name, a.running_mode, a.real_time_enabled, a.behavior_monitor_enabled,
		       a.on_access_enabled, a.ioav_enabled, a.tamper_protected, a.cloud_enabled, a.pua_enabled,
		       a.network_protection_enabled, a.signature_version, a.signature_age_days,
		       a.signature_updated_at, a.quick_scan_at, a.full_scan_at, a.engine_version,
		       a.third_party_products, a.exclusion_count, a.guard_enabled, a.last_enforce_at,
		       a.last_tamper_at, a.tamper_count_24h, a.agent_version, a.error, a.reported_at, a.synced_at,
		       a.asr_configured, a.asr_block, a.controlled_folder_access, a.network_protection_mode,
		       a.smart_screen_enabled, a.memory_integrity_enabled, a.credential_guard_enabled,
		       a.email_scan_enabled, a.exploit_protection_enabled, a.quarantined_threat_count, a.hardening_enabled,
		       (a.synced_at < NOW() - make_interval(secs => $%d)) AS is_stale,
		       (SELECT COUNT(*) FROM dlp_alerts x
		         WHERE x.type = 'malware' AND x.machine_id = a.machine_id
		           AND x.employee_id = a.employee_id
		           AND x.occurred_at > NOW() - INTERVAL '24 hours')::int AS threats_24h
		%s WHERE %s
		ORDER BY (a.health = 'protected') ASC, a.synced_at DESC
		LIMIT $%d OFFSET $%d`, n-2, antivirusFrom, wsql, n-1, n)

	rows, err := r.pool.Query(ctx, listSQL, args...)
	if err != nil {
		return nil, 0, counts, err
	}
	defer rows.Close()
	out := make([]dto.AntivirusStatusRow, 0)
	for rows.Next() {
		var x dto.AntivirusStatusRow
		if err := rows.Scan(
			&x.ID, &x.EmployeeID, &x.EmployeeName, &x.MachineID, &x.Hostname,
			&x.Health, &x.ProductName, &x.RunningMode, &x.RealTimeEnabled, &x.BehaviorMonitorEnabled,
			&x.OnAccessEnabled, &x.IoavEnabled, &x.TamperProtected, &x.CloudEnabled, &x.PuaEnabled,
			&x.NetworkProtectionEnabled, &x.SignatureVersion, &x.SignatureAgeDays,
			&x.SignatureUpdatedAt, &x.QuickScanAt, &x.FullScanAt, &x.EngineVersion,
			&x.ThirdPartyProducts, &x.ExclusionCount, &x.GuardEnabled, &x.LastEnforceAt,
			&x.LastTamperAt, &x.TamperCount24h, &x.AgentVersion, &x.Error, &x.ReportedAt, &x.SyncedAt,
			&x.AsrConfigured, &x.AsrBlock, &x.ControlledFolderAccess, &x.NetworkProtectionMode,
			&x.SmartScreenEnabled, &x.MemoryIntegrityEnabled, &x.CredentialGuardEnabled,
			&x.EmailScanEnabled, &x.ExploitProtectionEnabled, &x.QuarantinedThreatCount, &x.HardeningEnabled,
			&x.IsStale, &x.Threats24h,
		); err != nil {
			return nil, 0, counts, err
		}
		out = append(out, x)
	}
	return out, total, counts, rows.Err()
}

// ── Remote commands ─────────────────────────────────────────────────────────

// CreateCommand queues a remote action; requestedBy may be "" (SET NULL). An identical
// command (same machine/type/arg) that is still pending is reused instead of duplicated.
// Returns the id and whether an existing pending command was reused.
func (r *AntivirusRepo) CreateCommand(ctx context.Context, employeeID, machineID, cmdType, arg, requestedBy string) (int64, bool, error) {
	arg = trimTo(arg, 1000)
	var existing int64
	err := r.pool.QueryRow(ctx, `
		SELECT id FROM antivirus_commands
		WHERE employee_id = $1 AND machine_id = $2 AND type = $3 AND arg = $4 AND status = 'pending'
		ORDER BY created_at DESC LIMIT 1`, employeeID, machineID, cmdType, arg).Scan(&existing)
	if err == nil && existing > 0 {
		return existing, true, nil
	}
	var by any
	if requestedBy != "" {
		by = requestedBy
	}
	var id int64
	err = r.pool.QueryRow(ctx, `
		INSERT INTO antivirus_commands (employee_id, machine_id, type, arg, requested_by)
		VALUES ($1, $2, $3, $4, $5)
		RETURNING id`,
		employeeID, machineID, cmdType, arg, by).Scan(&id)
	return id, false, err
}

// MachineReports reports whether this (employee, machine) has ever sent antivirus status —
// a command for an unknown machine would never be pulled.
func (r *AntivirusRepo) MachineReports(ctx context.Context, employeeID, machineID string) (bool, error) {
	var ok bool
	err := r.pool.QueryRow(ctx, `
		SELECT EXISTS (SELECT 1 FROM antivirus_status WHERE employee_id = $1 AND machine_id = $2)`,
		employeeID, machineID).Scan(&ok)
	return ok, err
}

// AntivirusCommandFilter — fleet / per-machine command history (URL-synced on the web).
type AntivirusCommandFilter struct {
	EmployeeID string
	MachineID  string
	Status     string
	Type       string
	Search     string
	Page       int
	PerPage    int
}

// ListCommands returns one page of command history, newest first (admin view).
func (r *AntivirusRepo) ListCommands(ctx context.Context, f AntivirusCommandFilter) ([]dto.AntivirusCommandRow, int, error) {
	if f.Page < 1 {
		f.Page = 1
	}
	if f.PerPage < 1 || f.PerPage > 100 {
		f.PerPage = 30
	}
	conds := []string{"1=1"}
	args := []any{}
	add := func(cond string, v any) {
		args = append(args, v)
		conds = append(conds, fmt.Sprintf(cond, len(args)))
	}
	if v := strings.TrimSpace(f.EmployeeID); v != "" {
		add("c.employee_id = $%d", v)
	}
	if v := strings.TrimSpace(f.MachineID); v != "" {
		add("c.machine_id = $%d", v)
	}
	if v := strings.TrimSpace(f.Status); v != "" {
		add("c.status = $%d", v)
	}
	if v := strings.TrimSpace(f.Type); v != "" {
		add("c.type = $%d", v)
	}
	if q := strings.TrimSpace(f.Search); q != "" {
		args = append(args, "%"+strings.ToLower(q)+"%")
		n := len(args)
		conds = append(conds, fmt.Sprintf(
			"(LOWER(COALESCE(e.name,'')) LIKE $%d OR LOWER(c.employee_id) LIKE $%d OR LOWER(c.machine_id) LIKE $%d OR LOWER(COALESCE(h.device_name,'')) LIKE $%d OR LOWER(c.arg) LIKE $%d)",
			n, n, n, n, n))
	}
	from := `
		FROM antivirus_commands c
		LEFT JOIN employees e ON e.employee_id = c.employee_id AND e.deleted_at IS NULL
		LEFT JOIN users u ON u.id = c.requested_by
		LEFT JOIN LATERAL (
		    SELECT d.device_name FROM employee_devices d
		    WHERE d.employee_id = c.employee_id AND d.machine_id = c.machine_id AND d.device_name <> ''
		    ORDER BY d.last_seen_at DESC LIMIT 1
		) h ON TRUE
		WHERE ` + strings.Join(conds, " AND ")

	var total int
	if err := r.pool.QueryRow(ctx, "SELECT COUNT(*) "+from, args...).Scan(&total); err != nil {
		return nil, 0, err
	}
	args = append(args, f.PerPage, (f.Page-1)*f.PerPage)
	n := len(args)
	rows, err := r.pool.Query(ctx, fmt.Sprintf(`
		SELECT c.id, c.employee_id, COALESCE(e.name,''), c.machine_id, COALESCE(h.device_name,''),
		       c.type, c.arg, c.status, c.result, c.requested_by::text, COALESCE(u.name,''),
		       c.created_at, c.dispatched_at, c.completed_at
		%s
		ORDER BY c.created_at DESC, c.id DESC
		LIMIT $%d OFFSET $%d`, from, n-1, n), args...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()
	out := make([]dto.AntivirusCommandRow, 0)
	for rows.Next() {
		var x dto.AntivirusCommandRow
		if err := rows.Scan(&x.ID, &x.EmployeeID, &x.EmployeeName, &x.MachineID, &x.Hostname,
			&x.Type, &x.Arg, &x.Status, &x.Result, &x.RequestedBy, &x.RequestedByName,
			&x.CreatedAt, &x.DispatchedAt, &x.CompletedAt); err != nil {
			return nil, 0, err
		}
		out = append(out, x)
	}
	return out, total, rows.Err()
}

// PullPending atomically claims up to max pending commands for the calling device
// (pending -> running), so a command is dispatched exactly once.
func (r *AntivirusRepo) PullPending(ctx context.Context, employeeID, machineID string, max int) ([]dto.AntivirusCommandDevice, error) {
	if max < 1 || max > 20 {
		max = 10
	}
	rows, err := r.pool.Query(ctx, `
		WITH claimed AS (
		    SELECT id FROM antivirus_commands
		    WHERE employee_id = $1 AND machine_id = $2 AND status = 'pending'
		    ORDER BY created_at
		    LIMIT $3
		    FOR UPDATE SKIP LOCKED
		)
		UPDATE antivirus_commands c
		SET status = 'running', dispatched_at = NOW()
		FROM claimed
		WHERE c.id = claimed.id
		RETURNING c.id, c.type, c.arg`, employeeID, machineID, max)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make([]dto.AntivirusCommandDevice, 0)
	for rows.Next() {
		var x dto.AntivirusCommandDevice
		if err := rows.Scan(&x.ID, &x.Type, &x.Arg); err != nil {
			return nil, err
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

// SetResult records a command outcome; scoped to the calling device so one machine
// cannot close another's command.
func (r *AntivirusRepo) SetResult(ctx context.Context, employeeID, machineID string, id int64, status, result string) error {
	done := status == "done" || status == "failed"
	_, err := r.pool.Exec(ctx, `
		UPDATE antivirus_commands
		SET status = $4, result = $5,
		    completed_at = CASE WHEN $6 THEN NOW() ELSE completed_at END
		WHERE id = $1 AND employee_id = $2 AND machine_id = $3`,
		id, employeeID, machineID, status, trimTo(result, 2000), done)
	return err
}

// ExpireStaleCommands marks commands stuck pending/running past the cutoff as expired
// (device offline or crashed before finishing). Called opportunistically on pull.
func (r *AntivirusRepo) ExpireStaleCommands(ctx context.Context, employeeID, machineID string) {
	_, _ = r.pool.Exec(ctx, `
		UPDATE antivirus_commands
		SET status = 'expired', completed_at = NOW()
		WHERE employee_id = $1 AND machine_id = $2
		  AND ((status = 'pending' AND created_at < NOW() - INTERVAL '24 hours')
		    OR (status = 'running' AND COALESCE(dispatched_at, created_at) < NOW() - INTERVAL '14 hours'))`,
		employeeID, machineID)
}

func trimTo(s string, max int) string {
	s = strings.TrimSpace(s)
	if len(s) <= max {
		return s
	}
	return s[:max]
}
