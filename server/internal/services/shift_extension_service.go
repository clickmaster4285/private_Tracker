package services

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

const (
	maxExtensionMinutes   = 720 // 12 h either side
	maxExtensionRangeDays = 31
)

// SetChangeNotifier attaches the presence-socket push sent after an extension is granted or
// removed, so the employee's PC re-pulls its shift (and Windows sign-in hours) right away.
func (s *TimeAttendanceService) SetChangeNotifier(fn func() int) { s.changeNotifier = fn }

func (s *TimeAttendanceService) notifyChanged() {
	if s.changeNotifier != nil {
		s.changeNotifier()
	}
}

// ListShiftExtensions returns an employee's grants from yesterday onwards (current + upcoming).
func (s *TimeAttendanceService) ListShiftExtensions(ctx context.Context, employeeID string) (*dto.ShiftExtensionListResponse, error) {
	employeeID = strings.TrimSpace(employeeID)
	if employeeID == "" {
		return nil, fmt.Errorf("employeeId is required")
	}
	from := time.Now().UTC().AddDate(0, 0, -1)
	rows, err := s.repo.ListShiftExtensions(ctx, employeeID, from, from.AddDate(5, 0, 0))
	if err != nil {
		return nil, err
	}
	out := &dto.ShiftExtensionListResponse{Data: make([]dto.ShiftExtensionResponse, 0, len(rows))}
	for _, r := range rows {
		out.Data = append(out.Data, shiftExtensionResponse(r))
	}
	return out, nil
}

// CreateShiftExtension validates and stores a grant, then pokes the PCs.
func (s *TimeAttendanceService) CreateShiftExtension(ctx context.Context, in dto.ShiftExtensionInput, createdBy string) (*dto.ShiftExtensionResponse, error) {
	rec, err := validateShiftExtension(in)
	if err != nil {
		return nil, err
	}
	rec.CreatedBy = createdBy
	created, err := s.repo.CreateShiftExtension(ctx, *rec)
	if err != nil {
		return nil, err
	}
	s.notifyChanged()
	resp := shiftExtensionResponse(*created)
	return &resp, nil
}

// DeleteShiftExtension removes a grant; false when it did not exist.
func (s *TimeAttendanceService) DeleteShiftExtension(ctx context.Context, id int64) (bool, error) {
	employeeID, err := s.repo.DeleteShiftExtension(ctx, id)
	if err != nil || employeeID == "" {
		return false, err
	}
	s.notifyChanged()
	return true, nil
}

func validateShiftExtension(in dto.ShiftExtensionInput) (*repository.ShiftExtensionRecord, error) {
	employeeID := strings.TrimSpace(in.EmployeeID)
	if employeeID == "" {
		return nil, fmt.Errorf("employeeId is required")
	}
	start, err := time.Parse("2006-01-02", strings.TrimSpace(in.StartDate))
	if err != nil {
		return nil, fmt.Errorf("startDate must be YYYY-MM-DD")
	}
	end := start
	if strings.TrimSpace(in.EndDate) != "" {
		if end, err = time.Parse("2006-01-02", strings.TrimSpace(in.EndDate)); err != nil {
			return nil, fmt.Errorf("endDate must be YYYY-MM-DD")
		}
	}
	if end.Before(start) {
		return nil, fmt.Errorf("endDate must not be before startDate")
	}
	if end.Sub(start) > time.Duration(maxExtensionRangeDays-1)*24*time.Hour {
		return nil, fmt.Errorf("extra time can cover at most %d days", maxExtensionRangeDays)
	}
	if in.BeforeMinutes < 0 || in.AfterMinutes < 0 ||
		in.BeforeMinutes > maxExtensionMinutes || in.AfterMinutes > maxExtensionMinutes {
		return nil, fmt.Errorf("minutes must be between 0 and %d", maxExtensionMinutes)
	}
	if in.BeforeMinutes+in.AfterMinutes == 0 {
		return nil, fmt.Errorf("give at least 1 minute before the start or after the end")
	}
	return &repository.ShiftExtensionRecord{
		EmployeeID:    employeeID,
		StartDate:     start,
		EndDate:       end,
		BeforeMinutes: in.BeforeMinutes,
		AfterMinutes:  in.AfterMinutes,
		Reason:        strings.TrimSpace(in.Reason),
	}, nil
}

// scheduleExtensionDays expands the employee's grants into one entry per work date in the
// window the PC plans for (yesterday … +8 days, shift timezone). Overlapping grants on the
// same date take the larger value on each side.
func (s *TimeAttendanceService) scheduleExtensionDays(ctx context.Context, employeeID string, location *time.Location) ([]dto.ShiftExtensionDay, error) {
	now := time.Now().In(location)
	today := time.Date(now.Year(), now.Month(), now.Day(), 0, 0, 0, 0, time.UTC)
	from, to := today.AddDate(0, 0, -1), today.AddDate(0, 0, 8)
	rows, err := s.repo.ListShiftExtensions(ctx, employeeID, from, to)
	if err != nil {
		return nil, err
	}
	return expandShiftExtensions(rows, from, to), nil
}

func expandShiftExtensions(rows []repository.ShiftExtensionRecord, from, to time.Time) []dto.ShiftExtensionDay {
	byDate := map[string]*dto.ShiftExtensionDay{}
	order := make([]string, 0)
	for _, r := range rows {
		start := dateOnly(r.StartDate)
		end := dateOnly(r.EndDate)
		for d := maxTime(start, from); !d.After(minTime(end, to)); d = d.AddDate(0, 0, 1) {
			key := d.Format("2006-01-02")
			day, ok := byDate[key]
			if !ok {
				day = &dto.ShiftExtensionDay{Date: key}
				byDate[key] = day
				order = append(order, key)
			}
			if r.BeforeMinutes > day.BeforeMinutes {
				day.BeforeMinutes = r.BeforeMinutes
			}
			if r.AfterMinutes > day.AfterMinutes {
				day.AfterMinutes = r.AfterMinutes
			}
		}
	}
	out := make([]dto.ShiftExtensionDay, 0, len(order))
	for _, k := range order {
		out = append(out, *byDate[k])
	}
	return out
}

func dateOnly(t time.Time) time.Time {
	return time.Date(t.Year(), t.Month(), t.Day(), 0, 0, 0, 0, time.UTC)
}

func shiftExtensionResponse(r repository.ShiftExtensionRecord) dto.ShiftExtensionResponse {
	return dto.ShiftExtensionResponse{
		ID:            r.ID,
		EmployeeID:    r.EmployeeID,
		StartDate:     r.StartDate.Format("2006-01-02"),
		EndDate:       r.EndDate.Format("2006-01-02"),
		BeforeMinutes: r.BeforeMinutes,
		AfterMinutes:  r.AfterMinutes,
		Reason:        r.Reason,
		CreatedBy:     r.CreatedBy,
		CreatedAt:     r.CreatedAt,
	}
}
