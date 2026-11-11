package services

import (
	"context"
	"errors"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

// ErrInvalidCommand is returned for an unknown command type or a missing required arg.
var ErrInvalidCommand = errors.New("invalid antivirus command")

// commandNeedsArg: command types that require a non-empty arg.
var commandNeedsArg = map[string]bool{"scan_custom": true, "restore_threat": true}

var antivirusCommandTypes = map[string]bool{
	"scan_quick": true, "scan_full": true, "scan_custom": true,
	"update_signatures": true, "remove_threats": true, "restore_threat": true,
}

// AntivirusService owns the 24/7 antivirus guard fleet view. Detections and tamper
// events are DLP alerts (types "malware" / "av_tamper") and live in DlpService.
type AntivirusService struct {
	repo *repository.AntivirusRepo
}

func NewAntivirusService(repo *repository.AntivirusRepo) *AntivirusService {
	return &AntivirusService{repo: repo}
}

func (s *AntivirusService) SyncStatus(ctx context.Context, req *dto.SyncAntivirusStatusRequest) error {
	return s.repo.UpsertStatus(ctx, req)
}

func (s *AntivirusService) ListStatus(ctx context.Context, f repository.AntivirusListFilter) (*dto.AntivirusStatusListResponse, error) {
	rows, total, counts, err := s.repo.ListStatus(ctx, f)
	if err != nil {
		return nil, err
	}
	page, perPage := f.Page, f.PerPage
	if page < 1 {
		page = 1
	}
	if perPage < 1 || perPage > 100 {
		perPage = 30
	}
	totalPages := (total + perPage - 1) / perPage
	if totalPages < 1 {
		totalPages = 1
	}
	return &dto.AntivirusStatusListResponse{
		Data: rows, Total: total, Page: page, PerPage: perPage, TotalPages: totalPages, Counts: counts,
	}, nil
}

// ── Remote commands (admin-side fix) ────────────────────────────────────────

// QueueCommand validates and enqueues a remote action for one machine.
func (s *AntivirusService) QueueCommand(ctx context.Context, req *dto.CreateAntivirusCommandRequest, requestedBy string) (int64, error) {
	t := strings.TrimSpace(req.Type)
	if !antivirusCommandTypes[t] {
		return 0, ErrInvalidCommand
	}
	arg := strings.TrimSpace(req.Arg)
	if commandNeedsArg[t] && arg == "" {
		return 0, ErrInvalidCommand
	}
	if strings.TrimSpace(req.EmployeeID) == "" {
		return 0, ErrInvalidCommand
	}
	return s.repo.CreateCommand(ctx, req.EmployeeID, strings.TrimSpace(req.MachineID), t, arg, requestedBy)
}

func (s *AntivirusService) ListCommands(ctx context.Context, employeeID, machineID string, limit int) (*dto.AntivirusCommandListResponse, error) {
	rows, err := s.repo.ListCommands(ctx, employeeID, machineID, limit)
	if err != nil {
		return nil, err
	}
	return &dto.AntivirusCommandListResponse{Data: rows}, nil
}

func (s *AntivirusService) PullCommands(ctx context.Context, employeeID, machineID string) ([]dto.AntivirusCommandDevice, error) {
	s.repo.ExpireStaleCommands(ctx, employeeID, machineID)
	return s.repo.PullPending(ctx, employeeID, machineID, 10)
}

func (s *AntivirusService) ReportResult(ctx context.Context, employeeID, machineID string, req *dto.AntivirusCommandResultRequest) error {
	st := strings.TrimSpace(req.Status)
	if st != "running" && st != "done" && st != "failed" {
		return ErrInvalidCommand
	}
	return s.repo.SetResult(ctx, employeeID, machineID, req.ID, st, req.Result)
}
