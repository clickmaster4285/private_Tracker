package services

import (
	"testing"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

func TestTrackingModeConstants(t *testing.T) {
	if models.TrackingModeFixed != "fixed" || models.TrackingModeRoaming != "roaming" {
		t.Fatalf("mode vocabulary drift")
	}
	if models.TrackingDesiredRunning != "running" || models.TrackingDesiredStopped != "stopped" {
		t.Fatalf("desired vocabulary drift")
	}
}

func TestClampOccurredAt(t *testing.T) {
	recv := time.Date(2026, 10, 28, 12, 0, 0, 0, time.UTC)
	future := recv.Add(48 * time.Hour)
	if got := repository.ClampOccurredAt(future, recv); !got.Equal(recv) {
		t.Fatalf("future clamp: got %v want %v", got, recv)
	}
	old := recv.Add(-48 * time.Hour)
	wantFloor := recv.Add(-24 * time.Hour)
	if got := repository.ClampOccurredAt(old, recv); !got.Equal(wantFloor) {
		t.Fatalf("past clamp: got %v want %v", got, wantFloor)
	}
}
