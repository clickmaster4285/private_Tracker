package stream

import (
	"testing"
	"time"
)

func testHub() *Hub {
	cfg := DefaultConfig()
	cfg.IdleSec = 2
	return NewHub(cfg)
}

func TestSubscribeSendsStartOnRegister(t *testing.T) {
	h := testHub()
	defer h.Close()

	id, err := h.Subscribe("EMP-1")
	if err != nil {
		t.Fatal(err)
	}
	defer h.Unsubscribe("EMP-1", id)

	ctrl, _, ok := h.RegisterClient("EMP-1")
	if !ok {
		t.Fatal("register failed")
	}
	select {
	case ev := <-ctrl:
		if ev.Type != "start" {
			t.Fatalf("want start, got %s", ev.Type)
		}
	case <-time.After(time.Second):
		t.Fatal("expected start after register with existing watcher")
	}
}

func TestStopOnLastUnsubscribe(t *testing.T) {
	h := testHub()
	defer h.Close()

	id, err := h.Subscribe("EMP-2")
	if err != nil {
		t.Fatal(err)
	}
	ctrl, _, _ := h.RegisterClient("EMP-2")
	<-ctrl // start

	h.Unsubscribe("EMP-2", id)
	select {
	case ev := <-ctrl:
		if ev.Type != "stop" {
			t.Fatalf("want stop, got %s", ev.Type)
		}
	case <-time.After(time.Second):
		t.Fatal("expected stop")
	}

	snap := h.Snapshot("EMP-2")
	if snap.Wanted || snap.Streaming {
		t.Fatalf("expected not wanted after last unsubscribe, got %+v", snap)
	}
}

func TestMaxWatchers(t *testing.T) {
	cfg := DefaultConfig()
	cfg.MaxWatchersPerEmployee = 2
	h := NewHub(cfg)
	defer h.Close()

	id1, err := h.Subscribe("EMP-3")
	if err != nil {
		t.Fatal(err)
	}
	id2, err := h.Subscribe("EMP-3")
	if err != nil {
		t.Fatal(err)
	}
	_, err = h.Subscribe("EMP-3")
	if err != ErrTooManyWatchers {
		t.Fatalf("want ErrTooManyWatchers, got %v", err)
	}
	h.Unsubscribe("EMP-3", id1)
	h.Unsubscribe("EMP-3", id2)
}

func TestWatchTicketRoundTrip(t *testing.T) {
	h := testHub()
	defer h.Close()

	ticket, exp, err := h.IssueWatchTicket("user-1", "EMP-9")
	if err != nil || ticket == "" || exp <= 0 {
		t.Fatalf("issue: ticket=%q exp=%d err=%v", ticket, exp, err)
	}
	uid, err := h.ConsumeWatchTicket(ticket, "EMP-9")
	if err != nil || uid != "user-1" {
		t.Fatalf("consume: uid=%q err=%v", uid, err)
	}
	if _, err := h.ConsumeWatchTicket(ticket, "EMP-9"); err != ErrInvalidTicket {
		t.Fatalf("replay should fail, got %v", err)
	}
}

func TestConcurrentSubscribe(t *testing.T) {
	h := testHub()
	defer h.Close()

	const n = 8
	ids := make([]uint64, n)
	errs := make([]error, n)
	done := make(chan struct{})
	for i := 0; i < n; i++ {
		go func(i int) {
			ids[i], errs[i] = h.Subscribe("EMP-C")
			done <- struct{}{}
		}(i)
	}
	for i := 0; i < n; i++ {
		<-done
	}
	ok := 0
	for i := 0; i < n; i++ {
		if errs[i] == nil {
			ok++
			h.Unsubscribe("EMP-C", ids[i])
		}
	}
	if ok == 0 {
		t.Fatal("expected some successful subscribes")
	}
}
