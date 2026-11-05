package services

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"sync/atomic"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/google/uuid"
)

// TrackingControlService resolves Fixed/Roaming control policy for devices.
type TrackingControlService struct {
	repo          *repository.TrackingControlRepo
	cachedEpoch   atomic.Int64
	epochLoadedAt atomic.Int64 // unix nano
}

func NewTrackingControlService(repo *repository.TrackingControlRepo) *TrackingControlService {
	return &TrackingControlService{repo: repo}
}

// ResolveEffective returns the projected Fixed/Roaming policy for one machine.
func (s *TrackingControlService) ResolveEffective(ctx context.Context, employeeID, machineID string) (*models.EffectiveTrackingControl, error) {
	return s.repo.ResolveEffectiveControl(ctx, employeeID, machineID)
}

// CachedGlobalEpoch returns a short-TTL cached global epoch for sync piggyback (0 extra DB on hot path when warm).
func (s *TrackingControlService) CachedGlobalEpoch(ctx context.Context) int64 {
	const ttl = 30 * time.Second
	loaded := s.epochLoadedAt.Load()
	if loaded > 0 && time.Since(time.Unix(0, loaded)) < ttl {
		return s.cachedEpoch.Load()
	}
	epoch, err := s.repo.GlobalEpoch(ctx)
	if err != nil {
		return s.cachedEpoch.Load()
	}
	s.cachedEpoch.Store(epoch)
	s.epochLoadedAt.Store(time.Now().UnixNano())
	return epoch
}

// GetControl handles GET /devices/control (?sinceEpoch= optional short-circuit).
func (s *TrackingControlService) GetControl(ctx context.Context, employeeID, machineID string, sinceEpoch int64) (*dto.DeviceControlResponse, error) {
	if machineID != "" {
		_ = s.repo.EnsureMachineRow(ctx, employeeID, machineID)
	}
	eff, err := s.repo.ResolveEffectiveControl(ctx, employeeID, machineID)
	if err != nil {
		return nil, err
	}
	s.cachedEpoch.Store(eff.ControlEpoch)
	s.epochLoadedAt.Store(time.Now().UnixNano())

	_ = s.repo.TouchControlSeen(ctx, employeeID, machineID)

	if sinceEpoch > 0 && sinceEpoch >= eff.ControlEpoch {
		return &dto.DeviceControlResponse{
			ControlEpoch: eff.ControlEpoch,
			ServerTime:   time.Now().UTC(),
			Unchanged:    true,
			// Still echo mode so a buggy client that ignores unchanged cannot invent roaming.
			TrackingMode:        eff.TrackingMode,
			EffectiveModeSource: eff.EffectiveModeSource,
			StopWindow:          eff.StopWindow,
			RequireStopReason:   eff.RequireStopReason,
			KeepDlpWhenStopped:  eff.KeepDlpWhenStopped,
			MaxStoppedHours:     eff.MaxStoppedHours,
			DesiredState:        eff.DesiredState,
			Phase:               eff.Phase,
		}, nil
	}

	return &dto.DeviceControlResponse{
		TrackingMode:        eff.TrackingMode,
		EffectiveModeSource: eff.EffectiveModeSource,
		StopWindow:          eff.StopWindow,
		RequireStopReason:   eff.RequireStopReason,
		KeepDlpWhenStopped:  eff.KeepDlpWhenStopped,
		MaxStoppedHours:     eff.MaxStoppedHours,
		DesiredState:        eff.DesiredState,
		Phase:               eff.Phase,
		ControlEpoch:        eff.ControlEpoch,
		ServerTime:          time.Now().UTC(),
	}, nil
}

// IngestTrackingEvents handles POST /devices/tracking-events (idempotent).
func (s *TrackingControlService) IngestTrackingEvents(
	ctx context.Context,
	employeeID, machineID string,
	req dto.SyncTrackingEventsRequest,
) (*dto.SyncTrackingEventsResponse, error) {
	accepted := make([]string, 0, len(req.Events))
	rejected := make([]string, 0)

	if employeeID == "" || machineID == "" {
		for _, e := range req.Events {
			rejected = append(rejected, e.ClientEventID)
		}
		return &dto.SyncTrackingEventsResponse{
			AcceptedIDs: accepted,
			RejectedIDs: rejected,
			Message:     "missing employee or machine context",
		}, nil
	}

	_ = s.repo.EnsureMachineRow(ctx, employeeID, machineID)
	eff, err := s.repo.ResolveEffectiveControl(ctx, employeeID, machineID)
	if err != nil {
		return nil, err
	}

	now := time.Now().UTC()
	for _, entry := range req.Events {
		id := strings.TrimSpace(entry.ClientEventID)
		if id == "" {
			rejected = append(rejected, entry.ClientEventID)
			continue
		}
		if _, err := uuid.Parse(id); err != nil {
			rejected = append(rejected, id)
			continue
		}
		et := strings.ToLower(strings.TrimSpace(entry.EventType))
		if et != models.TrackingEventStart && et != models.TrackingEventStop {
			rejected = append(rejected, id)
			continue
		}
		if et == models.TrackingEventStop && eff.TrackingMode == models.TrackingModeFixed {
			rejected = append(rejected, id)
			continue
		}

		occurred := now
		if t, err := time.Parse(time.RFC3339Nano, entry.OccurredAt); err == nil {
			occurred = repository.ClampOccurredAt(t.UTC(), now)
		} else if t, err := time.Parse(time.RFC3339, entry.OccurredAt); err == nil {
			occurred = repository.ClampOccurredAt(t.UTC(), now)
		}

		flush := normalizeFlushResult(entry.FlushResult)
		ev := &models.DeviceTrackingEvent{
			ID:                repository.NewEventID(),
			EmployeeID:        employeeID,
			MachineID:         machineID,
			EventType:         et,
			OccurredAt:        occurred,
			ClientEventID:     id,
			Reason:            entry.Reason,
			WorkLocation:      entry.WorkLocation,
			LocationVerified:  entry.LocationVerified,
			FlushResult:       flush,
			UnsyncedRemaining: entry.UnsyncedRemaining,
			ClientVersion:     entry.ClientVersion,
		}
		dup, err := s.repo.InsertTrackingEvent(ctx, ev)
		if err != nil {
			return nil, fmt.Errorf("insert tracking event: %w", err)
		}
		if !dup {
			desired := models.TrackingDesiredRunning
			phase := models.TrackingPhaseCollecting
			if et == models.TrackingEventStop {
				desired = models.TrackingDesiredStopped
				phase = models.TrackingPhaseDraining
				if flush != nil && *flush == "complete" {
					phase = models.TrackingPhaseIdle
				}
			}
			_ = s.repo.UpdateMachineTrackingState(ctx, employeeID, machineID, desired, phase)
		}
		accepted = append(accepted, id)
	}

	resp := &dto.SyncTrackingEventsResponse{AcceptedIDs: accepted, Message: fmt.Sprintf("accepted %d", len(accepted))}
	if len(rejected) > 0 {
		resp.RejectedIDs = rejected
	}
	return resp, nil
}

// normalizeFlushResult maps client drain wording onto the CHECK vocabulary
// (complete|partial|pending). Unknown values become nil (column nullable).
func normalizeFlushResult(in *string) *string {
	if in == nil {
		return nil
	}
	v := strings.ToLower(strings.TrimSpace(*in))
	switch v {
	case "ok", "complete", "completed":
		s := "complete"
		return &s
	case "timeout", "partial", "skipped":
		s := "partial"
		return &s
	case "pending":
		s := "pending"
		return &s
	default:
		return nil
	}
}

// SetMachineTrackingMode is the HR admin override (Employees module).
// mode nil = inherit (clear override). mode must be fixed|roaming when set.
func (s *TrackingControlService) SetMachineTrackingMode(
	ctx context.Context,
	employeeCode, machineID string,
	mode *string,
) (*dto.SetMachineTrackingModeResponse, error) {
	if employeeCode == "" || machineID == "" {
		return nil, fmt.Errorf("employee and machine required")
	}
	if mode != nil {
		v := strings.ToLower(strings.TrimSpace(*mode))
		if v != models.TrackingModeFixed && v != models.TrackingModeRoaming {
			return nil, fmt.Errorf("invalid mode")
		}
		mode = &v
	}

	before, _ := s.repo.ResolveEffectiveControl(ctx, employeeCode, machineID)
	if _, err := s.repo.SetMachineTrackingModeOverride(ctx, employeeCode, machineID, mode); err != nil {
		return nil, err
	}
	epoch, _ := s.repo.BumpGlobalEpoch(ctx)
	s.cachedEpoch.Store(epoch)
	s.epochLoadedAt.Store(time.Now().UnixNano())

	after, err := s.repo.ResolveEffectiveControl(ctx, employeeCode, machineID)
	if err != nil {
		return nil, err
	}

	oldJSON, _ := json.Marshal(map[string]any{
		"effectiveMode": before.TrackingMode,
		"source":        before.EffectiveModeSource,
	})
	newJSON, _ := json.Marshal(map[string]any{
		"override":      mode,
		"effectiveMode": after.TrackingMode,
		"source":        after.EffectiveModeSource,
	})
	_ = s.repo.InsertPolicyAudit(ctx, "machine_tracking_mode", "machine", machineID, machineID, employeeCode, string(oldJSON), string(newJSON))

	return &dto.SetMachineTrackingModeResponse{
		MachineID:            machineID,
		TrackingModeOverride: mode,
		EffectiveMode:        after.TrackingMode,
		EffectiveModeSource:  after.EffectiveModeSource,
		ControlEpoch:         after.ControlEpoch,
	}, nil
}

func totalPages(total, perPage int) int {
	if perPage < 1 {
		return 0
	}
	p := total / perPage
	if total%perPage != 0 {
		p++
	}
	return p
}

func isoPtr(t *time.Time) *string {
	if t == nil {
		return nil
	}
	s := t.UTC().Format(time.RFC3339Nano)
	return &s
}

func toRoamingMachineDetail(r repository.RoamingMachineScan) dto.RoamingMachineDetail {
	return dto.RoamingMachineDetail{
		MachineID:           r.MachineID,
		Hostname:            r.Hostname,
		Status:              r.Status,
		EffectiveMode:       r.EffectiveMode,
		EffectiveModeSource: r.EffectiveModeSource,
		DesiredState:        r.DesiredState,
		Phase:               r.Phase,
		TrackingStateSince:  isoPtr(r.TrackingStateSince),
		LastTrackAt:         isoPtr(r.LastTrackAt),
		LastControlSeenAt:   isoPtr(r.LastControlSeenAt),
	}
}

// ListRoamingMachines is the Device Configuration → Roaming PC List tab.
// One employee row; prior roaming machines ride in History for the expand UI.
func (s *TrackingControlService) ListRoamingMachines(
	ctx context.Context,
	search string,
	page, perPage int,
) (*dto.RoamingMachineListResponse, error) {
	if page < 1 {
		page = 1
	}
	if perPage < 1 {
		perPage = 20
	}
	rows, total, err := s.repo.ListRoamingMachines(ctx, repository.ListRoamingMachinesFilter{
		Search: search, Page: page, PerPage: perPage,
	})
	if err != nil {
		return nil, err
	}

	order := make([]string, 0)
	byEmp := make(map[string][]repository.RoamingMachineScan)
	for _, r := range rows {
		if _, ok := byEmp[r.EmployeeID]; !ok {
			order = append(order, r.EmployeeID)
		}
		byEmp[r.EmployeeID] = append(byEmp[r.EmployeeID], r)
	}

	out := make([]dto.RoamingEmployeeRow, 0, len(order))
	for _, empID := range order {
		group := byEmp[empID]
		if len(group) == 0 {
			continue
		}
		// Repo already orders active-first then last_track_at — index 0 is current.
		cur := group[0]
		hist := make([]dto.RoamingMachineDetail, 0, len(group)-1)
		for i := 1; i < len(group); i++ {
			hist = append(hist, toRoamingMachineDetail(group[i]))
		}
		out = append(out, dto.RoamingEmployeeRow{
			EmployeeUUID: cur.EmployeeUUID,
			EmployeeID:   cur.EmployeeID,
			EmployeeName: cur.EmployeeName,
			Department:   cur.Department,
			Current:      toRoamingMachineDetail(cur),
			History:      hist,
		})
	}

	return &dto.RoamingMachineListResponse{
		Employees: out, Total: total, Page: page, PerPage: perPage, TotalPages: totalPages(total, perPage),
	}, nil
}

// ListTrackingEventLogs is the Device Configuration → Logs tab.
func (s *TrackingControlService) ListTrackingEventLogs(
	ctx context.Context,
	search, employeeID, machineID, eventType string,
	page, perPage int,
) (*dto.TrackingEventLogListResponse, error) {
	if page < 1 {
		page = 1
	}
	if perPage < 1 {
		perPage = 30
	}
	rows, total, err := s.repo.ListTrackingEvents(ctx, repository.ListTrackingEventsFilter{
		Search: search, EmployeeID: employeeID, MachineID: machineID, EventType: eventType,
		Page: page, PerPage: perPage,
	})
	if err != nil {
		return nil, err
	}
	out := make([]dto.TrackingEventLogRow, 0, len(rows))
	for _, r := range rows {
		out = append(out, dto.TrackingEventLogRow{
			ID:            r.ID,
			EmployeeUUID:  r.EmployeeUUID,
			EmployeeID:    r.EmployeeID,
			EmployeeName:  r.EmployeeName,
			MachineID:     r.MachineID,
			Hostname:      r.Hostname,
			EventType:     r.EventType,
			OccurredAt:    r.OccurredAt.UTC().Format(time.RFC3339Nano),
			ReceivedAt:    r.ReceivedAt.UTC().Format(time.RFC3339Nano),
			Reason:        r.Reason,
			FlushResult:   r.FlushResult,
			ClientVersion: r.ClientVersion,
			WorkLocation:  r.WorkLocation,
		})
	}
	return &dto.TrackingEventLogListResponse{
		Events: out, Total: total, Page: page, PerPage: perPage, TotalPages: totalPages(total, perPage),
	}, nil
}

// GetTrackerModeTypes returns Fixed/Roaming descriptions + org defaults.
func (s *TrackingControlService) GetTrackerModeTypes(ctx context.Context) (*dto.TrackerModeTypesResponse, error) {
	orgMode, stopWindow, keepDLP, err := s.repo.OrgDefaultPolicy(ctx)
	if err != nil {
		return nil, err
	}
	return &dto.TrackerModeTypesResponse{
		OrgDefaultMode:       orgMode,
		OrgStopWindow:        stopWindow,
		KeepDlpWhenStopped:   keepDLP,
		Modes: []dto.TrackerModeTypeInfo{
			{
				Mode:       models.TrackingModeFixed,
				Label:      "Fixed",
				Summary:    "Always-on office PC — the employee cannot Stop tracking.",
				CanStop:    false,
				Details:    "Use Fixed for machines that must stay monitored for the whole shift. Start/Stop controls are hidden; RequestStop is rejected as mode_fixed. DLP enforcement continues regardless. Org default is Fixed until an admin opts a laptop into Roaming.",
				OrgDefault: orgMode == models.TrackingModeFixed,
			},
			{
				Mode:       models.TrackingModeRoaming,
				Label:      "Roaming",
				Summary:    "Home / laptop PC — employee can Start and Stop tracking.",
				CanStop:    true,
				Details:    "Use Roaming for devices that leave the office. The employee uses Tracker control (Start/Stop). Stop pauses collectors and presence/live stream while DLP stays on when keep_dlp_when_stopped is true. Each Start/Stop click is recorded in tracking logs.",
				OrgDefault: orgMode == models.TrackingModeRoaming,
			},
		},
	}, nil
}
