package ws

import (
	"context"
	"log"
	"sync"
	"sync/atomic"
	"time"
)

// PresenceBackend optionally mirrors online state to Redis for multi-instance reads.
type PresenceBackend interface {
	SetPresence(ctx context.Context, empID, instanceID string) error
	TouchPresence(ctx context.Context, empID, instanceID string) error
	ClearPresence(ctx context.Context, empID, instanceID string) error
	IsPresent(ctx context.Context, empID string) (bool, error)
}

// Config holds caps for the in-memory presence / control-channel hub.
type Config struct {
	Enabled        bool
	MaxConnections int
	InstanceID     string
}

// ConnMeta is the lightweight per-employee presence record (no DB).
type ConnMeta struct {
	Gen         uint64
	EmployeeID  string
	ConnectedAt time.Time
	Platform    string
	Version     string
}

// Hub tracks long-lived control WebSocket connections (one per employee).
// Independent of the live-stream frame hub — no watchers, no JPEG state.
// When a PresenceBackend is attached, IsConnected also consults Redis so
// another instance's /live-stream/employees sees Online correctly.
type Hub struct {
	cfg     Config
	mu      sync.RWMutex
	conns   map[string]*ConnMeta
	nextGen atomic.Uint64
	backend PresenceBackend
}

// NewHub constructs a presence hub. MaxConnections <= 0 defaults to 10_000.
func NewHub(cfg Config) *Hub {
	if cfg.MaxConnections <= 0 {
		cfg.MaxConnections = 10_000
	}
	return &Hub{
		cfg:   cfg,
		conns: make(map[string]*ConnMeta),
	}
}

// SetBackend attaches an optional Redis presence mirror (nil = local-only).
func (h *Hub) SetBackend(b PresenceBackend) {
	h.backend = b
}

// InstanceID returns this process's cluster identity.
func (h *Hub) InstanceID() string { return h.cfg.InstanceID }

// Config returns a copy of the hub config.
func (h *Hub) Config() Config { return h.cfg }

// Register inserts or replaces the connection for empID and returns a generation
// token. Unregister must pass the same token so a stale reconnect cannot wipe a
// newer socket. Returns ok=false when disabled or at capacity (and empID is new).
func (h *Hub) Register(empID string) (gen uint64, ok bool) {
	if !h.cfg.Enabled || empID == "" {
		return 0, false
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	if _, exists := h.conns[empID]; !exists && len(h.conns) >= h.cfg.MaxConnections {
		return 0, false
	}
	gen = h.nextGen.Add(1)
	h.conns[empID] = &ConnMeta{
		Gen:         gen,
		EmployeeID:  empID,
		ConnectedAt: time.Now().UTC(),
	}
	if h.backend != nil && h.cfg.InstanceID != "" {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		defer cancel()
		if err := h.backend.SetPresence(ctx, empID, h.cfg.InstanceID); err != nil {
			log.Printf("[ws] SetPresence employee=%s: %v", empID, err)
		}
	}
	return gen, true
}

// Touch refreshes Redis presence TTL for a live socket (call on ping/pong).
func (h *Hub) Touch(empID string, gen uint64) {
	h.mu.RLock()
	meta, ok := h.conns[empID]
	match := ok && meta.Gen == gen
	h.mu.RUnlock()
	if !match || h.backend == nil || h.cfg.InstanceID == "" {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if err := h.backend.TouchPresence(ctx, empID, h.cfg.InstanceID); err != nil {
		log.Printf("[ws] TouchPresence employee=%s: %v", empID, err)
	}
}

// SetHello stores optional hello metadata from the client (generation-scoped).
func (h *Hub) SetHello(empID string, gen uint64, platform, version string) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if meta, ok := h.conns[empID]; ok && meta.Gen == gen {
		meta.Platform = platform
		meta.Version = version
	}
}

// Unregister removes the connection for empID only when gen still matches.
func (h *Hub) Unregister(empID string, gen uint64) {
	h.mu.Lock()
	meta, ok := h.conns[empID]
	if ok && meta.Gen == gen {
		delete(h.conns, empID)
	} else {
		ok = false
	}
	h.mu.Unlock()
	if !ok || h.backend == nil || h.cfg.InstanceID == "" {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if err := h.backend.ClearPresence(ctx, empID, h.cfg.InstanceID); err != nil {
		log.Printf("[ws] ClearPresence employee=%s: %v", empID, err)
	}
}

// IsConnected reports whether empID currently holds a control socket on this
// instance, or (when a backend is set) on any instance via Redis.
func (h *Hub) IsConnected(empID string) bool {
	h.mu.RLock()
	_, local := h.conns[empID]
	h.mu.RUnlock()
	if local {
		return true
	}
	if h.backend == nil {
		return false
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	ok, err := h.backend.IsPresent(ctx, empID)
	if err != nil {
		log.Printf("[ws] IsPresent employee=%s: %v", empID, err)
		return false
	}
	return ok
}

// Count returns the number of live control connections on THIS instance.
func (h *Hub) Count() int {
	h.mu.RLock()
	defer h.mu.RUnlock()
	return len(h.conns)
}

// Close clears the registry (server shutdown).
func (h *Hub) Close() {
	h.mu.Lock()
	ids := make([]string, 0, len(h.conns))
	for id := range h.conns {
		ids = append(ids, id)
	}
	h.conns = make(map[string]*ConnMeta)
	instanceID := h.cfg.InstanceID
	backend := h.backend
	h.mu.Unlock()

	if backend != nil && instanceID != "" {
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()
		for _, id := range ids {
			_ = backend.ClearPresence(ctx, id, instanceID)
		}
	}
}
