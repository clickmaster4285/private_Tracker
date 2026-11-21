package services

import (
	"testing"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

func d(s string) time.Time { t, _ := time.Parse("2006-01-02", s); return t }

func TestExpandShiftExtensions_PerDateAndLargerWins(t *testing.T) {
	rows := []repository.ShiftExtensionRecord{
		{StartDate: d("2026-11-21"), EndDate: d("2026-11-23"), AfterMinutes: 15},
		{StartDate: d("2026-11-22"), EndDate: d("2026-11-22"), BeforeMinutes: 10, AfterMinutes: 5},
		{StartDate: d("2026-12-30"), EndDate: d("2026-12-31"), AfterMinutes: 30}, // outside window
	}
	got := expandShiftExtensions(rows, d("2026-11-20"), d("2026-11-29"))
	if len(got) != 3 {
		t.Fatalf("days = %d, want 3: %+v", len(got), got)
	}
	if got[1] != (dto.ShiftExtensionDay{Date: "2026-11-22", BeforeMinutes: 10, AfterMinutes: 15}) {
		t.Fatalf("overlap day = %+v", got[1])
	}
}

func TestValidateShiftExtension(t *testing.T) {
	ok := dto.ShiftExtensionInput{EmployeeID: "EMP-1", StartDate: "2026-11-21", AfterMinutes: 15}
	if _, err := validateShiftExtension(ok); err != nil {
		t.Fatalf("valid input rejected: %v", err)
	}
	bad := []dto.ShiftExtensionInput{
		{EmployeeID: "", StartDate: "2026-11-21", AfterMinutes: 15},
		{EmployeeID: "EMP-1", StartDate: "21-11-2026", AfterMinutes: 15},
		{EmployeeID: "EMP-1", StartDate: "2026-11-21"},                                         // 0 minutes
		{EmployeeID: "EMP-1", StartDate: "2026-11-21", AfterMinutes: 800},                      // too long
		{EmployeeID: "EMP-1", StartDate: "2026-11-21", EndDate: "2026-11-20", AfterMinutes: 5}, // backwards
		{EmployeeID: "EMP-1", StartDate: "2026-11-01", EndDate: "2026-12-15", AfterMinutes: 5}, // > 31 days
	}
	for i, in := range bad {
		if _, err := validateShiftExtension(in); err == nil {
			t.Fatalf("case %d accepted: %+v", i, in)
		}
	}
}
