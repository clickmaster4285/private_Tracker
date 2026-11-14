package repository

import (
	"context"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

// MaxDeviceControlRules caps published device-control entries per policy bundle.
const MaxDeviceControlRules = 500

type PortRepo struct {
	pool *pgxpool.Pool
}

func NewPortRepo(pool *pgxpool.Pool) *PortRepo {
	return &PortRepo{pool: pool}
}

// ── Inventory (DeviceAuth sync + JWT read) ──────────────────────────────────

// SyncPorts upserts the scanned inventory for one machine. Devices absent from
// this batch are marked present=false (removed) rather than deleted, so history
// and last-seen survive. Returns the number of rows accepted.
func (r *PortRepo) SyncPorts(ctx context.Context, employeeID, machineID string, entries []dto.PortEntry) (int, error) {
	if employeeID == "" || len(entries) == 0 {
		return 0, nil
	}
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return 0, err
	}
	defer tx.Rollback(ctx)

	seen := make([]string, 0, len(entries))
	for _, e := range entries {
		if e.InstanceID == "" {
			continue
		}
		cat := e.Category
		if cat < 1 || cat > 3 {
			cat = 1
		}
		_, err := tx.Exec(ctx, `
			INSERT INTO machine_ports
			    (employee_id, machine_id, instance_id, category, pnp_class, class_guid,
			     bus_type, friendly_name, manufacturer, present, status, blockable, last_seen_at)
			VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,NOW())
			ON CONFLICT (employee_id, machine_id, instance_id) DO UPDATE SET
			    category=EXCLUDED.category, pnp_class=EXCLUDED.pnp_class, class_guid=EXCLUDED.class_guid,
			    bus_type=EXCLUDED.bus_type, friendly_name=EXCLUDED.friendly_name,
			    manufacturer=EXCLUDED.manufacturer, present=EXCLUDED.present, status=EXCLUDED.status,
			    blockable=EXCLUDED.blockable, last_seen_at=NOW()`,
			employeeID, machineID, e.InstanceID, cat, e.PnpClass, e.ClassGuid,
			e.BusType, e.FriendlyName, e.Manufacturer, e.Present, e.Status, e.Blockable)
		if err != nil {
			return 0, err
		}
		seen = append(seen, e.InstanceID)
	}

	// NOTE: the client is the source of truth for present/removed — it drains rows
	// incrementally (is_synced), so a partial batch must NOT mark everything else
	// absent. The collector sets present=false on a vanished device locally and that
	// change syncs as a normal row update, upserted above.

	if err := tx.Commit(ctx); err != nil {
		return 0, err
	}
	return len(seen), nil
}

// ListPorts returns the inventory for one employee (optionally one machine).
func (r *PortRepo) ListPorts(ctx context.Context, employeeID, machineID string) ([]dto.PortInventoryRow, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, machine_id, instance_id, category, pnp_class, class_guid, bus_type,
		       friendly_name, manufacturer, present, status, blockable, last_seen_at
		FROM machine_ports
		WHERE employee_id=$1 AND ($2='' OR machine_id=$2)
		ORDER BY category, pnp_class, friendly_name`, employeeID, machineID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make([]dto.PortInventoryRow, 0)
	for rows.Next() {
		var p dto.PortInventoryRow
		if err := rows.Scan(&p.ID, &p.MachineID, &p.InstanceID, &p.Category, &p.PnpClass,
			&p.ClassGuid, &p.BusType, &p.FriendlyName, &p.Manufacturer, &p.Present,
			&p.Status, &p.Blockable, &p.LastSeenAt); err != nil {
			return nil, err
		}
		out = append(out, p)
	}
	return out, rows.Err()
}

// ── Draft device-control rules (JWT) ────────────────────────────────────────

const dcSelectCols = `id, target_kind, target_value, label, pnp_class, action,
	scope, scope_value, enabled, note, created_at, updated_at`

func scanDeviceControlRule(row interface {
	Scan(dest ...any) error
}) (dto.DeviceControlRuleRow, error) {
	var x dto.DeviceControlRuleRow
	err := row.Scan(&x.ID, &x.TargetKind, &x.TargetValue, &x.Label, &x.PnpClass, &x.Action,
		&x.Scope, &x.ScopeValue, &x.Enabled, &x.Note, &x.CreatedAt, &x.UpdatedAt)
	return x, err
}

func (r *PortRepo) ListDeviceControlRules(ctx context.Context) ([]dto.DeviceControlRuleRow, error) {
	rows, err := r.pool.Query(ctx, `SELECT `+dcSelectCols+`
		FROM device_control_rules ORDER BY created_at DESC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make([]dto.DeviceControlRuleRow, 0)
	for rows.Next() {
		x, err := scanDeviceControlRule(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, x)
	}
	return out, rows.Err()
}

func (r *PortRepo) GetDeviceControlRule(ctx context.Context, id int64) (*dto.DeviceControlRuleRow, error) {
	row := r.pool.QueryRow(ctx, `SELECT `+dcSelectCols+` FROM device_control_rules WHERE id=$1`, id)
	x, err := scanDeviceControlRule(row)
	if err != nil {
		return nil, err
	}
	return &x, nil
}

func (r *PortRepo) InsertDeviceControlRule(ctx context.Context, req dto.CreateDeviceControlRuleRequest, userID *string) (*dto.DeviceControlRuleRow, error) {
	row := r.pool.QueryRow(ctx, `
		INSERT INTO device_control_rules
		    (target_kind, target_value, label, pnp_class, action, scope, scope_value, note, created_by, updated_by)
		VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$9)
		ON CONFLICT (target_kind, target_value, scope, scope_value) DO UPDATE SET
		    label=EXCLUDED.label, pnp_class=EXCLUDED.pnp_class, action=EXCLUDED.action,
		    note=EXCLUDED.note, enabled=TRUE, updated_by=EXCLUDED.updated_by, updated_at=NOW()
		RETURNING `+dcSelectCols,
		req.TargetKind, req.TargetValue, req.Label, req.PnpClass, req.Action,
		req.Scope, req.ScopeValue, req.Note, userID)
	x, err := scanDeviceControlRule(row)
	if err != nil {
		return nil, err
	}
	return &x, nil
}

func (r *PortRepo) UpdateDeviceControlRule(ctx context.Context, id int64, req dto.UpdateDeviceControlRuleRequest, userID *string) (*dto.DeviceControlRuleRow, error) {
	row := r.pool.QueryRow(ctx, `
		UPDATE device_control_rules SET
		    action  = COALESCE($2, action),
		    enabled = COALESCE($3, enabled),
		    note    = COALESCE($4, note),
		    updated_by = $5, updated_at = NOW()
		WHERE id=$1
		RETURNING `+dcSelectCols,
		id, req.Action, req.Enabled, req.Note, userID)
	x, err := scanDeviceControlRule(row)
	if err != nil {
		return nil, err
	}
	return &x, nil
}

func (r *PortRepo) DeleteDeviceControlRule(ctx context.Context, id int64) error {
	ct, err := r.pool.Exec(ctx, `DELETE FROM device_control_rules WHERE id=$1`, id)
	if err != nil {
		return err
	}
	if ct.RowsAffected() == 0 {
		return pgx.ErrNoRows
	}
	return nil
}
