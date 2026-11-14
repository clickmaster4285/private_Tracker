package ws

import "testing"

func TestBroadcastPolicyChanged(t *testing.T) {
	h := NewHub(Config{Enabled: true})
	a, cancelA := h.SubscribePolicy(1)
	b, cancelB := h.SubscribePolicy(2)

	if n := h.BroadcastPolicyChanged(); n != 2 {
		t.Fatalf("notified %d sockets, want 2", n)
	}
	// Second publish before the sockets drained collapses (buffer size 1, non-blocking).
	if n := h.BroadcastPolicyChanged(); n != 0 {
		t.Fatalf("second broadcast notified %d, want 0 (already queued)", n)
	}
	<-a
	<-b

	cancelA()
	if n := h.BroadcastPolicyChanged(); n != 1 {
		t.Fatalf("after unsubscribe notified %d, want 1", n)
	}
	<-b
	cancelB()
	if n := h.BroadcastPolicyChanged(); n != 0 {
		t.Fatalf("no subscribers notified %d, want 0", n)
	}
}
