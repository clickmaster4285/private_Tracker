package services

import (
	"context"
	"fmt"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

// PortService owns hardware/port inventory + device-control rules. Rule mutations
// auto-publish a new DLP policy bundle (same UX as the Allowlists module).
type PortService struct {
	repo *repository.PortRepo
	dlp  *DlpService
}

func NewPortService(repo *repository.PortRepo, dlp *DlpService) *PortService {
	return &PortService{repo: repo, dlp: dlp}
}

func (s *PortService) SyncPorts(ctx context.Context, req *dto.SyncPortsRequest) (*dto.SyncPortsResponse, error) {
	n, err := s.repo.SyncPorts(ctx, req.EmployeeID, req.MachineID, req.Entries)
	if err != nil {
		return nil, err
	}
	return &dto.SyncPortsResponse{Accepted: n}, nil
}

func (s *PortService) ListPorts(ctx context.Context, employeeID, machineID string) (*dto.PortInventoryResponse, error) {
	rows, err := s.repo.ListPorts(ctx, employeeID, machineID)
	if err != nil {
		return nil, err
	}
	return &dto.PortInventoryResponse{Ports: rows}, nil
}

func (s *PortService) ListRules(ctx context.Context) (*dto.DeviceControlListResponse, error) {
	rows, err := s.repo.ListDeviceControlRules(ctx)
	if err != nil {
		return nil, err
	}
	return &dto.DeviceControlListResponse{Rules: rows}, nil
}

func (s *PortService) CreateRule(ctx context.Context, req dto.CreateDeviceControlRuleRequest, userID string) (*dto.DeviceControlRuleRow, error) {
	req.TargetKind = strings.TrimSpace(strings.ToLower(req.TargetKind))
	req.TargetValue = strings.TrimSpace(req.TargetValue)
	req.Action = strings.TrimSpace(strings.ToLower(req.Action))
	req.Scope = strings.TrimSpace(strings.ToLower(req.Scope))
	req.ScopeValue = strings.TrimSpace(req.ScopeValue)
	if req.TargetKind != "class" && req.TargetKind != "instance" {
		return nil, fmt.Errorf("targetKind must be 'class' or 'instance'")
	}
	if req.TargetValue == "" {
		return nil, fmt.Errorf("targetValue is required")
	}
	if req.Action != "block" && req.Action != "allow" && req.Action != "alert" {
		req.Action = "block"
	}
	if req.Scope == "" {
		req.Scope = "global"
	}
	switch req.Scope {
	case "global":
		req.ScopeValue = ""
	case "department", "employee", "machine":
		if req.ScopeValue == "" {
			return nil, fmt.Errorf("scopeValue is required for a %s rule", req.Scope)
		}
	default:
		return nil, fmt.Errorf("scope must be global, department, employee or machine")
	}
	row, err := s.repo.InsertDeviceControlRule(ctx, req, nilIfEmpty(userID))
	if err != nil {
		return nil, err
	}
	s.autoPublish(ctx, userID)
	return row, nil
}

func (s *PortService) UpdateRule(ctx context.Context, id int64, req dto.UpdateDeviceControlRuleRequest, userID string) (*dto.DeviceControlRuleRow, error) {
	if req.Action != nil {
		a := strings.TrimSpace(strings.ToLower(*req.Action))
		if a != "block" && a != "allow" && a != "alert" {
			return nil, fmt.Errorf("action must be block, allow or alert")
		}
		req.Action = &a
	}
	row, err := s.repo.UpdateDeviceControlRule(ctx, id, req, nilIfEmpty(userID))
	if err != nil {
		return nil, err
	}
	s.autoPublish(ctx, userID)
	return row, nil
}

func (s *PortService) DeleteRule(ctx context.Context, id int64, userID string) error {
	if err := s.repo.DeleteDeviceControlRule(ctx, id); err != nil {
		return err
	}
	s.autoPublish(ctx, userID)
	return nil
}

// autoPublish compiles a fresh bundle so rule changes reach the fleet immediately.
func (s *PortService) autoPublish(ctx context.Context, userID string) {
	if s.dlp == nil {
		return
	}
	if _, err := s.dlp.Publish(ctx, nilIfEmpty(userID)); err != nil {
		// Rule is already persisted; surface publish failure so ops can re-publish.
		fmt.Printf("port-control auto-publish failed: %v\n", err)
	}
}

func nilIfEmpty(s string) *string {
	if strings.TrimSpace(s) == "" {
		return nil
	}
	return &s
}
