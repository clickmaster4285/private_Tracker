package services

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

// ShiftListQuery is the public query shape for the shift list (mirrors
// repository.ShiftListParams so the handler can build it without importing
// the repository package).
type ShiftListQuery = repository.ShiftListParams

// ShiftService is the business-logic layer for the shift catalog.
type ShiftService struct {
	repo                 *repository.ShiftRepo
	defaultShiftTimezone string
}

// NewShiftService creates a new ShiftService.
func NewShiftService(repo *repository.ShiftRepo, defaultShiftTimezone string) *ShiftService {
	return &ShiftService{
		repo:                 repo,
		defaultShiftTimezone: strings.TrimSpace(defaultShiftTimezone),
	}
}

// ApplyDefaultTimezone migrates legacy UTC shift rows to the configured default.
func (s *ShiftService) ApplyDefaultTimezone(ctx context.Context) (int64, error) {
	if s.defaultShiftTimezone == "" {
		return 0, nil
	}
	if _, err := time.LoadLocation(s.defaultShiftTimezone); err != nil {
		return 0, fmt.Errorf("invalid DEFAULT_SHIFT_TIMEZONE %q: %w", s.defaultShiftTimezone, err)
	}
	return s.repo.ApplyDefaultTimezone(ctx, s.defaultShiftTimezone)
}

// List returns a paginated, searchable list of shifts.
func (s *ShiftService) List(ctx context.Context, params repository.ShiftListParams) (*repository.ShiftListResult, error) {
	result, err := s.repo.List(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list shifts: %w", err)
	}
	return result, nil
}

// ListAll returns every non-deleted shift (used to populate dropdowns).
func (s *ShiftService) ListAll(ctx context.Context) ([]models.Shift, error) {
	return s.repo.ListAll(ctx)
}

// Create adds a new shift. Name uniqueness is enforced by the DB UNIQUE
// constraint; we pre-validate trimmed input to return a friendly 400.
func (s *ShiftService) Create(ctx context.Context, req *ShiftInput) (*models.Shift, error) {
	if err := s.validateShiftInput(req); err != nil {
		return nil, err
	}
	shift := &models.Shift{
		Name:          strings.TrimSpace(req.Name),
		StartTime:     req.StartTime,
		EndTime:       req.EndTime,
		WorkingDays:   strings.TrimSpace(req.WorkingDays),
		Timezone:      normalizedTimezone(req.Timezone, s.defaultShiftTimezone),
		GraceMinutes:  req.GraceMinutes,
		OvertimeHours: req.OvertimeHours,
		Description:   req.Description,
	}
	created, err := s.repo.Create(ctx, shift)
	if err != nil {
		return nil, fmt.Errorf("create shift: %w", err)
	}
	return created, nil
}

// Update modifies an existing shift.
func (s *ShiftService) Update(ctx context.Context, id int, req *ShiftInput) (*models.Shift, error) {
	if err := s.validateShiftInput(req); err != nil {
		return nil, err
	}
	existing, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return nil, err
	}
	if existing == nil {
		return nil, nil
	}
	shift := &models.Shift{
		Name:          strings.TrimSpace(req.Name),
		StartTime:     req.StartTime,
		EndTime:       req.EndTime,
		WorkingDays:   strings.TrimSpace(req.WorkingDays),
		Timezone:      normalizedTimezone(req.Timezone, existing.Timezone),
		GraceMinutes:  req.GraceMinutes,
		OvertimeHours: req.OvertimeHours,
		Description:   req.Description,
	}
	updated, err := s.repo.Update(ctx, id, shift)
	if err != nil {
		return nil, fmt.Errorf("update shift: %w", err)
	}
	return updated, nil
}

// Delete soft-deletes a shift. Refuses when any non-deleted employee still
// references it (mirrors the monitoring-types rule from migration 023).
func (s *ShiftService) Delete(ctx context.Context, id int) error {
	existing, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return err
	}
	if existing == nil {
		return fmt.Errorf("shift not found")
	}
	usage, err := s.repo.CountShiftUsage(ctx, id)
	if err != nil {
		return err
	}
	if usage > 0 {
		return fmt.Errorf("shift is assigned to %d employee(s) and cannot be deleted", usage)
	}
	return s.repo.Delete(ctx, id)
}

// validateShiftInput enforces the minimal field rules. Working-days format
// is a comma-separated weekday short-name list (Mon,Tue,Wed,Thu,Fri,Sat,Sun);
// an empty string is accepted (the service stores it as-is; the web renders
// a "no days" chip strip).
func (s *ShiftService) validateShiftInput(req *ShiftInput) error {
	if req == nil {
		return fmt.Errorf("shift payload is required")
	}
	if strings.TrimSpace(req.Name) == "" {
		return fmt.Errorf("shift name is required")
	}
	if strings.TrimSpace(req.StartTime) == "" || strings.TrimSpace(req.EndTime) == "" {
		return fmt.Errorf("start and end time are required")
	}
	if req.GraceMinutes < 0 || req.GraceMinutes > 120 {
		return fmt.Errorf("grace minutes must be between 0 and 120")
	}
	if req.OvertimeHours < 0 || req.OvertimeHours > 24 {
		return fmt.Errorf("overtime hours must be between 0 and 24")
	}
	timezone := normalizedTimezone(req.Timezone, s.defaultShiftTimezone)
	if _, err := time.LoadLocation(timezone); err != nil {
		return fmt.Errorf("timezone must be a valid IANA timezone")
	}
	return nil
}

func normalizedTimezone(value, fallback string) string {
	value = strings.TrimSpace(value)
	if value != "" {
		return value
	}
	if strings.TrimSpace(fallback) != "" {
		return fallback
	}
	return "UTC"
}

// ShiftInput is the writable shape of a shift (used for create + update).
// All fields are required; an empty WorkingDays means "no scheduled days".
type ShiftInput struct {
	Name          string `json:"name"`
	StartTime     string `json:"startTime"`
	EndTime       string `json:"endTime"`
	WorkingDays   string `json:"workingDays"`
	Timezone      string `json:"timezone"`
	GraceMinutes  int    `json:"graceMinutes"`
	OvertimeHours int    `json:"overtimeHours"`
	Description   string `json:"description"`
}
