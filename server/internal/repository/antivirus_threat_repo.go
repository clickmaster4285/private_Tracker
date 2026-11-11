package repository

import (
	"context"
	"fmt"
	"strconv"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/dto"
)

// MaxThreatsPerSnapshot caps one machine's threat snapshot (Defender keeps ~30 days of history).
const MaxThreatsPerSnapshot = 500

var antivirusThreatStatus = map[string]bool{
	"detected": true, "cleaned": true, "quarantined": true, "removed": true,
	"allowed": true, "blocked": true, "failed": true, "unknown": true,
}

var antivirusThreatSeverity = map[string]bool{
	"low": true, "medium": true, "high": true, "critical": true, "unknown": true,
}

func normalizeToken(v string, allowed map[string]bool) string {
	v = strings.ToLower(strings.TrimSpace(v))
	if allowed[v] {
		return v
	}
	return "unknown"
}

// ReplaceThreats stores a FULL per-machine threat snapshot in one transaction: every entry is
// upserted, and rows of that machine missing from the snapshot are deleted (Defender purged
// them). Entries with a non-numeric ThreatID are skipped. Returns the accepted count.
func (r *AntivirusRepo) ReplaceThreats(ctx context.Context, req *dto.SyncAntivirusThreatsRequest) (int, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return 0, err
	}
	defer tx.Rollback(ctx) //nolint:errcheck

	keep := make([]int64, 0, len(req.Threats))
	seen := map[int64]bool{}
	for i, t := range req.Threats {
		if i >= MaxThreatsPerSnapshot {
			break
		}
		id, perr := strconv.ParseInt(strings.TrimSpace(t.ThreatID), 10, 64)
		if perr != nil || seen[id] {
			continue
		}
		seen[id] = true
		res := make([]string, 0, len(t.Resources))
		for j, x := range t.Resources {
			if j >= 50 {
				break
			}
			if x = strings.TrimSpace(x); x != "" {
				res = append(res, trimTo(x, 1000))
			}
		}
		if _, err := tx.Exec(ctx, `
			INSERT INTO antivirus_threats (
			    employee_id, machine_id, threat_id, threat_name, severity, category, status,
			    is_active, did_execute, resources, process_name, detection_user, detection_source,
			    initial_detection_at, last_status_change_at, synced_at)
			VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,NOW())
			ON CONFLICT (employee_id, machine_id, threat_id) DO UPDATE SET
			    threat_name = CASE WHEN EXCLUDED.threat_name <> '' THEN EXCLUDED.threat_name
			                       ELSE antivirus_threats.threat_name END,
			    severity = EXCLUDED.severity, category = EXCLUDED.category, status = EXCLUDED.status,
			    is_active = EXCLUDED.is_active, did_execute = EXCLUDED.did_execute,
			    resources = CASE WHEN EXCLUDED.resources <> '' THEN EXCLUDED.resources
			                     ELSE antivirus_threats.resources END,
			    process_name = EXCLUDED.process_name, detection_user = EXCLUDED.detection_user,
			    detection_source = EXCLUDED.detection_source,
			    initial_detection_at = COALESCE(EXCLUDED.initial_detection_at, antivirus_threats.initial_detection_at),
			    last_status_change_at = COALESCE(EXCLUDED.last_status_change_at, antivirus_threats.last_status_change_at),
			    synced_at = NOW()`,
			req.EmployeeID, req.MachineID, id, trimTo(t.ThreatName, 300),
			normalizeToken(t.Severity, antivirusThreatSeverity), trimTo(t.Category, 100),
			normalizeToken(t.Status, antivirusThreatStatus), t.IsActive, t.DidExecute,
			strings.Join(res, "\n"), trimTo(t.ProcessName, 1000), trimTo(t.DetectionUser, 200),
			trimTo(t.DetectionSource, 100), t.InitialDetectionAt, t.LastStatusChangeAt,
		); err != nil {
			return 0, err
		}
		keep = append(keep, id)
	}

	// A failed probe sends an error and an empty list — never wipe the machine's history then.
	if strings.TrimSpace(req.Error) == "" {
		if _, err := tx.Exec(ctx, `
			DELETE FROM antivirus_threats
			WHERE employee_id = $1 AND machine_id = $2 AND NOT (threat_id = ANY($3))`,
			req.EmployeeID, req.MachineID, keep); err != nil {
			return 0, err
		}
	}
	return len(keep), tx.Commit(ctx)
}

// AntivirusThreatFilter — fleet / per-machine threat list (URL-synced on the web).
type AntivirusThreatFilter struct {
	EmployeeID string
	MachineID  string
	Search     string // employee / PC / threat name / file path
	State      string // "" | active | quarantined | removed | failed | allowed
	Page       int
	PerPage    int
}

func (f AntivirusThreatFilter) where(withState bool) (string, []any) {
	conds := []string{"1=1"}
	args := []any{}
	if v := strings.TrimSpace(f.EmployeeID); v != "" {
		args = append(args, v)
		conds = append(conds, fmt.Sprintf("t.employee_id = $%d", len(args)))
	}
	if v := strings.TrimSpace(f.MachineID); v != "" {
		args = append(args, v)
		conds = append(conds, fmt.Sprintf("t.machine_id = $%d", len(args)))
	}
	if q := strings.TrimSpace(f.Search); q != "" {
		args = append(args, "%"+strings.ToLower(q)+"%")
		n := len(args)
		conds = append(conds, fmt.Sprintf(
			"(LOWER(COALESCE(e.name,'')) LIKE $%d OR LOWER(t.employee_id) LIKE $%d OR LOWER(COALESCE(h.device_name,'')) LIKE $%d OR LOWER(t.threat_name) LIKE $%d OR LOWER(t.resources) LIKE $%d)",
			n, n, n, n, n))
	}
	if withState {
		switch strings.TrimSpace(f.State) {
		case "active":
			conds = append(conds, "t.is_active")
		case "quarantined":
			conds = append(conds, "t.status = 'quarantined'")
		case "removed":
			conds = append(conds, "t.status IN ('removed','cleaned')")
		case "failed":
			conds = append(conds, "t.status = 'failed'")
		case "allowed":
			conds = append(conds, "t.status = 'allowed'")
		}
	}
	return strings.Join(conds, " AND "), args
}

const antivirusThreatFrom = `
	FROM antivirus_threats t
	LEFT JOIN employees e ON e.employee_id = t.employee_id AND e.deleted_at IS NULL
	LEFT JOIN LATERAL (
	    SELECT d.device_name FROM employee_devices d
	    WHERE d.employee_id = t.employee_id AND d.machine_id = t.machine_id AND d.device_name <> ''
	    ORDER BY d.last_seen_at DESC LIMIT 1
	) h ON TRUE`

// ListThreats returns one page of threats (newest status change first) + state counts for the
// same filters minus the state filter.
func (r *AntivirusRepo) ListThreats(ctx context.Context, f AntivirusThreatFilter) ([]dto.AntivirusThreatRow, int, dto.AntivirusThreatCounts, error) {
	var counts dto.AntivirusThreatCounts
	if f.Page < 1 {
		f.Page = 1
	}
	if f.PerPage < 1 || f.PerPage > 100 {
		f.PerPage = 30
	}

	cw, cargs := f.where(false)
	if err := r.pool.QueryRow(ctx, `
		SELECT COUNT(*),
		       COUNT(*) FILTER (WHERE t.is_active),
		       COUNT(*) FILTER (WHERE t.status = 'quarantined'),
		       COUNT(*) FILTER (WHERE t.status IN ('removed','cleaned')),
		       COUNT(*) FILTER (WHERE t.status = 'failed'),
		       COUNT(*) FILTER (WHERE t.status = 'allowed')
		`+antivirusThreatFrom+` WHERE `+cw, cargs...).Scan(
		&counts.Total, &counts.Active, &counts.Quarantined, &counts.Removed, &counts.Failed, &counts.Allowed,
	); err != nil {
		return nil, 0, counts, err
	}

	w, args := f.where(true)
	var total int
	if err := r.pool.QueryRow(ctx, "SELECT COUNT(*) "+antivirusThreatFrom+" WHERE "+w, args...).Scan(&total); err != nil {
		return nil, 0, counts, err
	}
	args = append(args, f.PerPage, (f.Page-1)*f.PerPage)
	n := len(args)
	rows, err := r.pool.Query(ctx, fmt.Sprintf(`
		SELECT t.id, t.employee_id, COALESCE(e.name,''), t.machine_id, COALESCE(h.device_name,''),
		       t.threat_id::text, t.threat_name, t.severity, t.category, t.status, t.is_active,
		       t.did_execute, t.resources, t.process_name, t.detection_user, t.detection_source,
		       t.initial_detection_at, t.last_status_change_at, t.first_synced_at, t.synced_at,
		       (SELECT c.type FROM antivirus_commands c
		         WHERE c.employee_id = t.employee_id AND c.machine_id = t.machine_id
		           AND c.type IN ('remove_threat','restore_threat') AND c.arg = t.threat_id::text
		           AND c.status IN ('pending','running')
		         ORDER BY c.created_at DESC LIMIT 1) AS pending_command
		%s WHERE %s
		ORDER BY t.is_active DESC, t.last_status_change_at DESC NULLS LAST, t.id DESC
		LIMIT $%d OFFSET $%d`, antivirusThreatFrom, w, n-1, n), args...)
	if err != nil {
		return nil, 0, counts, err
	}
	defer rows.Close()
	out := make([]dto.AntivirusThreatRow, 0)
	for rows.Next() {
		var x dto.AntivirusThreatRow
		var res string
		if err := rows.Scan(&x.ID, &x.EmployeeID, &x.EmployeeName, &x.MachineID, &x.Hostname,
			&x.ThreatID, &x.ThreatName, &x.Severity, &x.Category, &x.Status, &x.IsActive,
			&x.DidExecute, &res, &x.ProcessName, &x.DetectionUser, &x.DetectionSource,
			&x.InitialDetectionAt, &x.LastStatusChangeAt, &x.FirstSyncedAt, &x.SyncedAt,
			&x.PendingCommand); err != nil {
			return nil, 0, counts, err
		}
		x.Resources = []string{}
		for _, line := range strings.Split(res, "\n") {
			if line = strings.TrimSpace(line); line != "" {
				x.Resources = append(x.Resources, line)
			}
		}
		out = append(out, x)
	}
	return out, total, counts, rows.Err()
}

// UserHasPermission reports whether a web user may use a permission-gated surface: a system
// role (company_admin) always may; any other role needs the submodule key granted.
func (r *AntivirusRepo) UserHasPermission(ctx context.Context, userID, key string) (bool, error) {
	if strings.TrimSpace(userID) == "" {
		return false, nil
	}
	var ok bool
	err := r.pool.QueryRow(ctx, `
		SELECT EXISTS (
		    SELECT 1 FROM users u
		    JOIN roles ro ON ro.id = u.role_id
		    WHERE u.id = $1 AND u.deleted_at IS NULL
		      AND (ro.is_system OR EXISTS (
		          SELECT 1 FROM role_submodule_permissions rsp
		          JOIN submodules s ON s.id = rsp.submodule_id
		          WHERE rsp.role_id = u.role_id AND s.key = $2)))`, userID, key).Scan(&ok)
	return ok, err
}
