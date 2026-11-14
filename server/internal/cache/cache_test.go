package cache

import (
	"context"
	"testing"
	"time"
)

func TestGetOrLoadPassthroughWithoutRedis(t *testing.T) {
	c := New(nil)
	var got map[string]int
	calls := 0
	err := c.GetOrLoad(context.Background(), "k", time.Second, &got, func(ctx context.Context) (any, error) {
		calls++
		return map[string]int{"n": 7}, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if got["n"] != 7 || calls != 1 {
		t.Fatalf("got=%v calls=%d", got, calls)
	}
}

func TestDashboardKeyStable(t *testing.T) {
	from := time.Unix(1700000000, 0).UTC()
	to := from.Add(24 * time.Hour)
	a := DashboardKey(from, to, nil, 8, 10)
	b := DashboardKey(from, to, nil, 8, 10)
	if a != b {
		t.Fatalf("unstable key %q vs %q", a, b)
	}
}
