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

func TestWorkWindowNightShiftIsOneRecord(t *testing.T) {
	loc := time.UTC
	night := &repository.ScheduleRecord{StartTime: "22:00:00", EndTime: "06:00:00", WorkingDays: "Mon,Tue,Wed,Thu,Fri"}
	day := time.Date(2026, 11, 19, 0, 0, 0, 0, loc)

	start, end := workWindow(day, loc, night)
	// 16h off-time split in half: 14:00 on the shift date to 14:00 next day.
	if want := time.Date(2026, 11, 19, 14, 0, 0, 0, loc); !start.Equal(want) {
		t.Fatalf("window start = %v, want %v", start, want)
	}
	if want := time.Date(2026, 11, 20, 14, 0, 0, 0, loc); !end.Equal(want) {
		t.Fatalf("window end = %v, want %v", end, want)
	}

	// Consecutive dates tile with no gap and no overlap.
	nextStart, _ := workWindow(day.AddDate(0, 0, 1), loc, night)
	if !nextStart.Equal(end) {
		t.Fatalf("next window starts %v, want %v", nextStart, end)
	}
}

func TestWorkWindowDayShiftStaysCalendarDay(t *testing.T) {
	loc := time.UTC
	dayShift := &repository.ScheduleRecord{StartTime: "09:00:00", EndTime: "17:00:00", WorkingDays: "Mon"}
	day := time.Date(2026, 11, 19, 0, 0, 0, 0, loc)

	start, end := workWindow(day, loc, dayShift)
	if !start.Equal(day) || !end.Equal(day.AddDate(0, 0, 1)) {
		t.Fatalf("window = %v..%v, want calendar day", start, end)
	}
	if s, e := workWindow(day, loc, nil); !s.Equal(day) || !e.Equal(day.AddDate(0, 0, 1)) {
		t.Fatalf("no schedule window = %v..%v, want calendar day", s, e)
	}
}

func TestWorkDateForNightShiftAfterMidnight(t *testing.T) {
	loc := time.UTC
	night := &repository.ScheduleRecord{StartTime: "22:00:00", EndTime: "06:00:00", WorkingDays: "Mon,Tue,Wed,Thu,Fri"}

	cases := []struct {
		now  time.Time
		want time.Time
	}{
		{time.Date(2026, 11, 20, 3, 0, 0, 0, loc), time.Date(2026, 11, 19, 0, 0, 0, 0, loc)},  // 03:00 → last night's shift
		{time.Date(2026, 11, 20, 13, 59, 0, 0, loc), time.Date(2026, 11, 19, 0, 0, 0, 0, loc)}, // still last night's window
		{time.Date(2026, 11, 20, 14, 0, 0, 0, loc), time.Date(2026, 11, 20, 0, 0, 0, 0, loc)},  // tonight's window begins
		{time.Date(2026, 11, 20, 22, 30, 0, 0, loc), time.Date(2026, 11, 20, 0, 0, 0, 0, loc)}, // tonight's shift
	}
	for _, c := range cases {
		if got := workDateFor(c.now, loc, night); !got.Equal(c.want) {
			t.Fatalf("workDateFor(%v) = %v, want %v", c.now, got, c.want)
		}
	}

	dayShift := &repository.ScheduleRecord{StartTime: "09:00:00", EndTime: "17:00:00", WorkingDays: "Mon"}
	if got := workDateFor(time.Date(2026, 11, 20, 3, 0, 0, 0, loc), loc, dayShift); !got.Equal(time.Date(2026, 11, 20, 0, 0, 0, 0, loc)) {
		t.Fatalf("day shift work date = %v, want same calendar day", got)
	}
}
