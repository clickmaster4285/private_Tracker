package repository

import (
	"testing"
	"time"
)

func TestSessionStatusForInsert(t *testing.T) {
	if got := sessionStatusForInsert(nil); got != "ACTIVE" {
		t.Fatalf("open session status = %q, want ACTIVE", got)
	}

	endedAt := time.Date(2026, 9, 10, 9, 0, 0, 0, time.UTC)
	if got := sessionStatusForInsert(&endedAt); got != "CLOSED" {
		t.Fatalf("closed session status = %q, want CLOSED", got)
	}
}
