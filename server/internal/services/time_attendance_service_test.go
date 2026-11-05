package services

import (
	"testing"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

func TestWeeklyPatternUsesLowercaseContractKeys(t *testing.T) {
	pattern := weeklyPattern(&repository.ScheduleRecord{
		StartTime:   "09:00:00",
		EndTime:     "18:00:00",
		WorkingDays: "Mon,Tue,FRI",
	})

	if got := pattern["mon"]; got != "09:00-18:00" {
		t.Fatalf("mon = %q, want 09:00-18:00", got)
	}
	if got := pattern["fri"]; got != "09:00-18:00" {
		t.Fatalf("fri = %q, want 09:00-18:00", got)
	}
	if _, exists := pattern["wed"]; exists {
		t.Fatal("unexpected schedule for wed")
	}
}

func TestInactiveSecondsUnionsIdleAndLockIntervals(t *testing.T) {
	start := time.Date(2026, 8, 31, 9, 0, 0, 0, time.UTC)
	end := start.Add(2 * time.Hour)
	events := []models.SessionEvent{
		{EventType: "idle_start", EventAt: start.Add(10 * time.Minute)},
		{EventType: "screen_lock", EventAt: start.Add(20 * time.Minute)},
		{EventType: "idle_end", EventAt: start.Add(30 * time.Minute)},
		{EventType: "screen_unlock", EventAt: start.Add(40 * time.Minute)},
	}

	got := inactiveSeconds(events, start, end)
	want := (30 * time.Minute).Seconds()
	if got != want {
		t.Fatalf("inactive seconds = %.0f, want %.0f", got, want)
	}
}

func TestInactiveSecondsCarriesPreDayLockState(t *testing.T) {
	start := time.Date(2026, 8, 31, 0, 0, 0, 0, time.UTC)
	end := start.Add(2 * time.Hour)
	events := []models.SessionEvent{
		{EventType: "screen_lock", EventAt: start.Add(-time.Hour)},
		{EventType: "screen_unlock", EventAt: start.Add(30 * time.Minute)},
	}

	got := inactiveSeconds(events, start, end)
	want := (30 * time.Minute).Seconds()
	if got != want {
		t.Fatalf("inactive seconds = %.0f, want %.0f", got, want)
	}
}
