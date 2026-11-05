package redis

import (
	"context"
	"testing"
	"time"
)

// Integration self-test against local Redis (skips if Redis is down).
func TestClusterPresenceAndTickets(t *testing.T) {
	c, err := NewClient("localhost:6379", "", 0)
	if err != nil {
		t.Skipf("redis unavailable: %v", err)
	}
	defer c.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	emp := "TEST-EMP-PHASE2"
	inst := "test-instance-a"
	ticket := "deadbeefdeadbeefdeadbeefdeadbeef"

	if err := c.SetPresence(ctx, emp, inst); err != nil {
		t.Fatalf("SetPresence: %v", err)
	}
	ok, err := c.IsPresent(ctx, emp)
	if err != nil || !ok {
		t.Fatalf("IsPresent want true, got ok=%v err=%v", ok, err)
	}
	if err := c.TouchPresence(ctx, emp, inst); err != nil {
		t.Fatalf("TouchPresence: %v", err)
	}

	if err := c.SetPublisher(ctx, emp, inst); err != nil {
		t.Fatalf("SetPublisher: %v", err)
	}
	got, found, err := c.LookupPublisher(ctx, emp)
	if err != nil || !found || got != inst {
		t.Fatalf("LookupPublisher got=%q found=%v err=%v", got, found, err)
	}

	if err := c.SetInstanceURL(ctx, inst, "http://127.0.0.1:8000"); err != nil {
		t.Fatalf("SetInstanceURL: %v", err)
	}
	url, err := c.GetInstanceURL(ctx, inst)
	if err != nil || url != "http://127.0.0.1:8000" {
		t.Fatalf("GetInstanceURL got=%q err=%v", url, err)
	}

	if err := c.StoreWatchTicket(ctx, ticket, "user-1", emp); err != nil {
		t.Fatalf("StoreWatchTicket: %v", err)
	}
	uid, err := c.ConsumeWatchTicket(ctx, ticket, emp)
	if err != nil || uid != "user-1" {
		t.Fatalf("ConsumeWatchTicket uid=%q err=%v", uid, err)
	}
	if _, err := c.ConsumeWatchTicket(ctx, ticket, emp); err == nil {
		t.Fatal("second ConsumeWatchTicket should fail (one-shot)")
	}

	_ = c.ClearPublisher(ctx, emp, inst)
	_ = c.ClearPresence(ctx, emp, inst)
}

func TestClusterFallbackNilClient(t *testing.T) {
	var c *Client
	ctx := context.Background()
	if err := c.SetPresence(ctx, "x", "y"); err != nil {
		t.Fatalf("nil SetPresence: %v", err)
	}
	ok, err := c.IsPresent(ctx, "x")
	if err != nil || ok {
		t.Fatalf("nil IsPresent ok=%v err=%v", ok, err)
	}
}
