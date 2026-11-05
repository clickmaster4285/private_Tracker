package stream

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"log"
	"sync"
	"time"
)

// ClusterBackend optionally stores watch tickets + publisher instance routing in Redis.
type ClusterBackend interface {
	SetPublisher(ctx context.Context, empID, instanceID string) error
	ClearPublisher(ctx context.Context, empID, instanceID string) error
	LookupPublisher(ctx context.Context, empID string) (instanceID string, ok bool, err error)
	SetInstanceURL(ctx context.Context, instanceID, publicURL string) error
	GetInstanceURL(ctx context.Context, instanceID string) (string, error)
	StoreWatchTicket(ctx context.Context, ticket, userID, employeeID string) error
	ConsumeWatchTicket(ctx context.Context, ticket, employeeID string) (userID string, err error)
}

// FeatureID is the terms_consent feature key for live preview.
const FeatureID = "live_view"

const (
	watchTicketTTL    = 60 * time.Second
	maxPendingTickets = 2000
)

var (
	ErrDisabled        = errors.New("live stream disabled")
	ErrTooManyStreams  = errors.New("too many concurrent streams")
	ErrTooManyWatchers = errors.New("too many watchers for employee")
	ErrInvalidTicket   = errors.New("invalid or expired watch ticket")
	ErrTooManyTickets  = errors.New("too many pending watch tickets")
	ErrRemotePublisher = errors.New("publisher on another instance; configure INSTANCE_PUBLIC_URL on peers")
)

type watchTicket struct {
	EmployeeID string
	UserID     string
	ExpiresAt  time.Time
}

// Config holds runtime caps for the live-stream hub + SFU.
type Config struct {
	Enabled                bool
	MaxStreams             int
	MaxWatchersPerEmployee int
	IdleSec                int
	MaxBitrateKbps         int
	ICEServers             []ICEServerConfig
	InstanceID             string
	InstancePublicURL      string
}

// DefaultConfig returns defaults for WebRTC live stream.
func DefaultConfig() Config {
	return Config{
		Enabled:                true,
		MaxStreams:             25,
		MaxWatchersPerEmployee: 10,
		IdleSec:                90,
		MaxBitrateKbps:         8000,
		ICEServers: []ICEServerConfig{
			{URLs: []string{"stun:stun.l.google.com:19302"}},
		},
	}
}

// Capability is advertised by the desktop client in its hello message.
type Capability struct {
	Platform        string
	StreamAvailable bool
	Version         string
	Monitors        []MonitorInfo
	SelectedMonitor int
}

// MonitorInfo is one display the client can capture.
type MonitorInfo struct {
	Index     int    `json:"index"`
	Name      string `json:"name"`
	Width     int    `json:"width"`
	Height    int    `json:"height"`
	IsPrimary bool   `json:"isPrimary"`
}

// ControlEvent is sent to the push-socket handler (start/stop/select_monitor/force_keyframe).
type ControlEvent struct {
	Type         string // "start" | "stop" | "select_monitor" | "force_keyframe"
	MonitorIndex int
}

// EmployeeSnapshot is hub-side state for the employees REST list / watch status.
type EmployeeSnapshot struct {
	Wanted          bool
	Streaming       bool
	StreamAvailable bool
	WatcherCount    int
	ClientConnected bool
	Monitors        []MonitorInfo
	SelectedMonitor int
}

type mailbox struct {
	lastActivity    time.Time
	wanted          bool
	clientConnected bool
	clientGen       uint64
	cap             Capability
	watchers        map[uint64]struct{}
	nextWatcherID   uint64
	ctrl            chan ControlEvent
	countsAsStream  bool
}

// Hub tracks watchers/control + owns the Pion SFU. No JPEG frames.
type Hub struct {
	cfg Config
	sfu *SFU

	mu             sync.RWMutex
	boxes          map[string]*mailbox
	streamingCount int
	tickets        map[string]watchTicket
	cluster        ClusterBackend
	// empID → last force_keyframe time (throttles PLI/ctrl spam).
	lastKeyframe sync.Map

	stopIdle chan struct{}
	wg       sync.WaitGroup
}

// NewHub creates a hub + SFU and starts the idle reaper.
func NewHub(cfg Config) *Hub {
	if cfg.MaxStreams <= 0 {
		cfg.MaxStreams = 25
	}
	if cfg.MaxWatchersPerEmployee <= 0 {
		cfg.MaxWatchersPerEmployee = 10
	}
	if cfg.IdleSec <= 0 {
		cfg.IdleSec = 90
	}
	if cfg.MaxBitrateKbps <= 0 {
		cfg.MaxBitrateKbps = 8000
	}

	h := &Hub{
		cfg:      cfg,
		sfu:      NewSFU(cfg.ICEServers, cfg.MaxBitrateKbps),
		boxes:    make(map[string]*mailbox),
		tickets:  make(map[string]watchTicket),
		stopIdle: make(chan struct{}),
	}
	// IDR when the publisher track first becomes ready, and again when a watcher
	// attaches with media (late joiner needs a fresh keyframe or the tile stays
	// LIVE with — FPS / black until the next natural I-frame — which some Win11
	// sessions never decode without an explicit ForceKeyFrame).
	h.sfu.SetPublisherPLIHook(func(empID string) {
		h.NudgePublisherKeyframe(empID)
	})
	h.wg.Add(1)
	go h.idleLoop()
	return h
}

// NudgePublisherKeyframe asks the desktop client to ForceKeyFrame (2s throttle).
func (h *Hub) NudgePublisherKeyframe(empID string) {
	if v, ok := h.lastKeyframe.Load(empID); ok {
		if t, _ := v.(time.Time); time.Since(t) < 2*time.Second {
			return
		}
	}
	h.lastKeyframe.Store(empID, time.Now())
	h.mu.Lock()
	defer h.mu.Unlock()
	m, ok := h.boxes[empID]
	if !ok || !m.clientConnected {
		return
	}
	h.sendCtrlLocked(m, "force_keyframe")
}

// SetCluster attaches Redis-backed tickets + publisher routing (nil = local-only).
func (h *Hub) SetCluster(b ClusterBackend) {
	h.cluster = b
	if b == nil || h.cfg.InstanceID == "" || h.cfg.InstancePublicURL == "" {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if err := b.SetInstanceURL(ctx, h.cfg.InstanceID, h.cfg.InstancePublicURL); err != nil {
		log.Printf("[live-stream] SetInstanceURL: %v", err)
	}
}

// InstanceID returns this process's cluster identity.
func (h *Hub) InstanceID() string { return h.cfg.InstanceID }

// SFU returns the embedded Pion SFU.
func (h *Hub) SFU() *SFU { return h.sfu }

// Config returns a copy of the hub config.
func (h *Hub) Config() Config { return h.cfg }

// ICEServers returns ICE config for clients.
func (h *Hub) ICEServers() []ICEServerConfig {
	return h.sfu.ICEServersJSON()
}

// Close stops the idle loop and SFU.
func (h *Hub) Close() {
	select {
	case <-h.stopIdle:
	default:
		close(h.stopIdle)
	}
	h.wg.Wait()
	h.sfu.Close()

	h.mu.Lock()
	defer h.mu.Unlock()
	for _, m := range h.boxes {
		h.clearMailboxLocked(m)
	}
	h.boxes = make(map[string]*mailbox)
	h.tickets = make(map[string]watchTicket)
	h.streamingCount = 0
}

func (h *Hub) idleLoop() {
	defer h.wg.Done()
	ticker := time.NewTicker(10 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-h.stopIdle:
			return
		case <-ticker.C:
			h.reapIdle()
		}
	}
}

func (h *Hub) reapIdle() {
	idleFor := time.Duration(h.cfg.IdleSec) * time.Second
	now := time.Now()

	h.mu.Lock()
	defer h.mu.Unlock()

	for empID, m := range h.boxes {
		if len(m.watchers) > 0 {
			continue
		}
		if m.wanted {
			m.wanted = false
			h.sendCtrlLocked(m, "stop")
			h.unmarkStreamLocked(m)
			h.sfu.ClosePublisher(empID)
			continue
		}
		if !m.clientConnected && now.Sub(m.lastActivity) > idleFor {
			h.sfu.CloseRoom(empID)
			delete(h.boxes, empID)
		}
	}

	for id, t := range h.tickets {
		if now.After(t.ExpiresAt) {
			delete(h.tickets, id)
		}
	}
}

func (h *Hub) getOrCreateLocked(empID string) *mailbox {
	m, ok := h.boxes[empID]
	if ok {
		return m
	}
	m = &mailbox{
		watchers:     make(map[uint64]struct{}),
		ctrl:         make(chan ControlEvent, 4),
		lastActivity: time.Now(),
		cap:          Capability{StreamAvailable: false},
	}
	h.boxes[empID] = m
	return m
}

func (h *Hub) sendCtrlLocked(m *mailbox, typ string) {
	h.sendCtrlEventLocked(m, ControlEvent{Type: typ})
}

func (h *Hub) sendCtrlEventLocked(m *mailbox, ev ControlEvent) {
	if m.ctrl == nil {
		return
	}
	select {
	case m.ctrl <- ev:
		return
	default:
	}
	// Channel full. Never displace start/stop with force_keyframe — DropOldest
	// used to swallow start while PLI-driven keyframes flooded the buffer.
	if ev.Type == "force_keyframe" {
		return
	}
	select {
	case <-m.ctrl:
	default:
	}
	select {
	case m.ctrl <- ev:
	default:
	}
}

// SelectMonitor asks the push client to switch capture target.
func (h *Hub) SelectMonitor(empID string, index int) {
	h.mu.Lock()
	defer h.mu.Unlock()
	m, ok := h.boxes[empID]
	if !ok || !m.clientConnected {
		return
	}
	if index < 0 {
		index = 0
	}
	if n := len(m.cap.Monitors); n > 0 && index >= n {
		index = n - 1
	}
	m.cap.SelectedMonitor = index
	m.lastActivity = time.Now()
	h.sendCtrlEventLocked(m, ControlEvent{Type: "select_monitor", MonitorIndex: index})
}

func (h *Hub) markStreamLocked(m *mailbox) error {
	if m.countsAsStream {
		return nil
	}
	if h.streamingCount >= h.cfg.MaxStreams {
		return ErrTooManyStreams
	}
	m.countsAsStream = true
	h.streamingCount++
	return nil
}

func (h *Hub) unmarkStreamLocked(m *mailbox) {
	if !m.countsAsStream {
		return
	}
	m.countsAsStream = false
	if h.streamingCount > 0 {
		h.streamingCount--
	}
}

func (h *Hub) clearMailboxLocked(m *mailbox) {
	m.watchers = make(map[uint64]struct{})
	m.wanted = false
	h.unmarkStreamLocked(m)
}

// RegisterClient attaches the push-socket control channel for an employee.
// Replaces any prior ctrl channel so a stale Push handler cannot steal start/stop.
// Returns a generation token that UnregisterClient must pass.
func (h *Hub) RegisterClient(empID string) (ctrl <-chan ControlEvent, gen uint64, ok bool) {
	if !h.cfg.Enabled {
		return nil, 0, false
	}
	h.mu.Lock()
	m := h.getOrCreateLocked(empID)
	if m.ctrl != nil {
		close(m.ctrl)
	}
	m.ctrl = make(chan ControlEvent, 4)
	m.clientGen++
	gen = m.clientGen
	m.clientConnected = true
	m.lastActivity = time.Now()
	if m.wanted {
		h.sendCtrlLocked(m, "start")
	}
	ctrl = m.ctrl
	cluster := h.cluster
	instanceID := h.cfg.InstanceID
	h.mu.Unlock()

	if cluster != nil && instanceID != "" {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		if err := cluster.SetPublisher(ctx, empID, instanceID); err != nil {
			log.Printf("[live-stream] SetPublisher employee=%s: %v", empID, err)
		}
		cancel()
	}
	return ctrl, gen, true
}

// UnregisterClient marks the push client gone (generation-scoped).
func (h *Hub) UnregisterClient(empID string, gen uint64) {
	h.mu.Lock()
	m, ok := h.boxes[empID]
	if !ok || m.clientGen != gen {
		h.mu.Unlock()
		return
	}
	m.clientConnected = false
	m.lastActivity = time.Now()
	h.sfu.ClosePublisher(empID)
	if !m.wanted {
		h.unmarkStreamLocked(m)
	}
	cluster := h.cluster
	instanceID := h.cfg.InstanceID
	h.mu.Unlock()

	if cluster != nil && instanceID != "" {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		defer cancel()
		if err := cluster.ClearPublisher(ctx, empID, instanceID); err != nil {
			log.Printf("[live-stream] ClearPublisher employee=%s: %v", empID, err)
		}
	}
}

// SetCapability stores the client's hello advertisement.
func (h *Hub) SetCapability(empID string, cap Capability) {
	h.mu.Lock()
	defer h.mu.Unlock()
	m := h.getOrCreateLocked(empID)
	m.cap = cap
	m.lastActivity = time.Now()
}

// CanSubscribe reports whether Subscribe would succeed without mutating state.
// Used to return HTTP 429 before WebSocket Upgrade.
func (h *Hub) CanSubscribe(empID string) error {
	if !h.cfg.Enabled {
		return ErrDisabled
	}
	h.mu.Lock()
	defer h.mu.Unlock()

	m := h.boxes[empID]
	if m != nil && len(m.watchers) >= h.cfg.MaxWatchersPerEmployee {
		return ErrTooManyWatchers
	}
	// First watcher would call markStreamLocked
	if m == nil || !m.wanted {
		if m == nil || !m.countsAsStream {
			if h.streamingCount >= h.cfg.MaxStreams {
				return ErrTooManyStreams
			}
		}
	}
	return nil
}

// Subscribe attaches a watcher. First watcher marks the stream wanted and may send start.
func (h *Hub) Subscribe(empID string) (watcherID uint64, err error) {
	if !h.cfg.Enabled {
		return 0, ErrDisabled
	}

	h.mu.Lock()
	defer h.mu.Unlock()

	m := h.getOrCreateLocked(empID)
	if len(m.watchers) >= h.cfg.MaxWatchersPerEmployee {
		return 0, ErrTooManyWatchers
	}

	wasWanted := m.wanted
	if !wasWanted {
		if err := h.markStreamLocked(m); err != nil {
			return 0, err
		}
		m.wanted = true
		if m.clientConnected {
			h.sendCtrlLocked(m, "start")
		}
	}

	m.nextWatcherID++
	id := m.nextWatcherID
	m.watchers[id] = struct{}{}
	m.lastActivity = time.Now()
	return id, nil
}

// Unsubscribe detaches a watcher. Last watcher sends stop.
func (h *Hub) Unsubscribe(empID string, watcherID uint64) {
	h.sfu.RemoveSubscriber(empID, watcherID)

	h.mu.Lock()
	defer h.mu.Unlock()

	m, ok := h.boxes[empID]
	if !ok {
		return
	}
	delete(m.watchers, watcherID)
	m.lastActivity = time.Now()

	if len(m.watchers) == 0 && m.wanted {
		m.wanted = false
		h.sendCtrlLocked(m, "stop")
		h.unmarkStreamLocked(m)
		h.sfu.ClosePublisher(empID)
	}
}

// Snapshot returns hub state for one employee.
func (h *Hub) Snapshot(empID string) EmployeeSnapshot {
	h.mu.RLock()
	defer h.mu.RUnlock()
	m, ok := h.boxes[empID]
	if !ok {
		return EmployeeSnapshot{}
	}
	streaming := m.wanted && h.sfu.PublisherActive(empID)
	return EmployeeSnapshot{
		Wanted:          m.wanted,
		Streaming:       streaming,
		StreamAvailable: m.cap.StreamAvailable,
		WatcherCount:    len(m.watchers),
		ClientConnected: m.clientConnected,
		Monitors:        append([]MonitorInfo(nil), m.cap.Monitors...),
		SelectedMonitor: m.cap.SelectedMonitor,
	}
}

// CapabilityOf returns the last advertised capability.
func (h *Hub) CapabilityOf(empID string) (Capability, bool) {
	h.mu.RLock()
	defer h.mu.RUnlock()
	m, ok := h.boxes[empID]
	if !ok {
		return Capability{}, false
	}
	return m.cap, m.clientConnected || m.cap.Platform != ""
}

// IsWanted reports whether any admin is watching.
func (h *Hub) IsWanted(empID string) bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	m, ok := h.boxes[empID]
	return ok && m.wanted
}

// IssueWatchTicket mints a one-time ticket for the admin watch WebSocket.
func (h *Hub) IssueWatchTicket(userID, employeeID string) (ticket string, expiresInSec int, err error) {
	if !h.cfg.Enabled {
		return "", 0, ErrDisabled
	}
	var raw [16]byte
	if _, err := rand.Read(raw[:]); err != nil {
		return "", 0, err
	}
	ticket = hex.EncodeToString(raw[:])
	expiresInSec = int(watchTicketTTL.Seconds())

	if h.cluster != nil {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		defer cancel()
		if err := h.cluster.StoreWatchTicket(ctx, ticket, userID, employeeID); err != nil {
			if err.Error() == ErrTooManyTickets.Error() {
				return "", 0, ErrTooManyTickets
			}
			return "", 0, err
		}
		return ticket, expiresInSec, nil
	}

	h.mu.Lock()
	defer h.mu.Unlock()
	if len(h.tickets) >= maxPendingTickets {
		return "", 0, ErrTooManyTickets
	}
	h.tickets[ticket] = watchTicket{
		EmployeeID: employeeID,
		UserID:     userID,
		ExpiresAt:  time.Now().Add(watchTicketTTL),
	}
	return ticket, expiresInSec, nil
}

// ConsumeWatchTicket validates and burns a ticket.
func (h *Hub) ConsumeWatchTicket(ticket, employeeID string) (userID string, err error) {
	if h.cluster != nil {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		defer cancel()
		uid, err := h.cluster.ConsumeWatchTicket(ctx, ticket, employeeID)
		if err != nil {
			return "", ErrInvalidTicket
		}
		return uid, nil
	}

	h.mu.Lock()
	defer h.mu.Unlock()

	t, ok := h.tickets[ticket]
	if !ok || time.Now().After(t.ExpiresAt) {
		delete(h.tickets, ticket)
		return "", ErrInvalidTicket
	}
	if t.EmployeeID != employeeID {
		return "", ErrInvalidTicket
	}
	delete(h.tickets, ticket)
	return t.UserID, nil
}

// ResolveWatchTarget returns a redirect URL when the publisher lives on another
// instance. Empty string means serve locally. Ticket must NOT be consumed yet.
func (h *Hub) ResolveWatchTarget(empID string) (redirectURL string, err error) {
	if h.cluster == nil || h.cfg.InstanceID == "" {
		return "", nil
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	owner, ok, err := h.cluster.LookupPublisher(ctx, empID)
	if err != nil || !ok || owner == "" || owner == h.cfg.InstanceID {
		return "", err
	}
	base, err := h.cluster.GetInstanceURL(ctx, owner)
	if err != nil {
		return "", err
	}
	if base == "" {
		return "", ErrRemotePublisher
	}
	return base, nil
}
