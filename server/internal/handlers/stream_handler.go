package handlers

import (
	"context"
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/stream"
	"github.com/alpha-ai-tracker/server/internal/ws"
	"github.com/gorilla/websocket"
	"github.com/labstack/echo/v4"
	"github.com/pion/webrtc/v4"
)

const (
	// Heartbeat fallback only — primary online is the presence WS (/api/v1/ws).
	liveStreamOnlineWindow = 3 * time.Minute
	wsWriteWait            = 10 * time.Second
	wsPongWait             = 90 * time.Second
	wsPingPeriod           = 30 * time.Second
	wsMaxSocketLife        = 4 * time.Hour
	wsTokenRecheck         = 5 * time.Minute
	maxConcurrentOffers    = 32
	uplinkProbeMaxBytes    = 512 * 1024
)

// StreamHandler serves live-stream WebRTC signaling + REST endpoints.
type StreamHandler struct {
	hub              *stream.Hub
	presence         *ws.Hub // optional — instant online via GET /api/v1/ws
	employeeRepo     *repository.EmployeeRepo
	termsConsentRepo *repository.TermsConsentRepo
	taRepo           *repository.TimeAttendanceRepo
	deviceRepo       *repository.DeviceRepo // optional — DeviceAuth re-check on push sockets
	allowedOrigins   map[string]bool
	upgrader         websocket.Upgrader
	offerSem         chan struct{}
}

// NewStreamHandler constructs the handler. presence may be nil (falls back to heartbeat online).
// deviceRepo may be nil (skips post-upgrade device re-validation on push sockets).
func NewStreamHandler(
	hub *stream.Hub,
	presence *ws.Hub,
	employeeRepo *repository.EmployeeRepo,
	termsConsentRepo *repository.TermsConsentRepo,
	taRepo *repository.TimeAttendanceRepo,
	deviceRepo *repository.DeviceRepo,
	allowedOrigins []string,
) *StreamHandler {
	originSet := make(map[string]bool, len(allowedOrigins))
	for _, o := range allowedOrigins {
		originSet[strings.TrimSpace(o)] = true
	}
	h := &StreamHandler{
		hub:              hub,
		presence:         presence,
		employeeRepo:     employeeRepo,
		termsConsentRepo: termsConsentRepo,
		taRepo:           taRepo,
		deviceRepo:       deviceRepo,
		allowedOrigins:   originSet,
		offerSem:         make(chan struct{}, maxConcurrentOffers),
	}
	h.upgrader = websocket.Upgrader{
		ReadBufferSize:  4096,
		WriteBufferSize: 4096,
		CheckOrigin: func(r *http.Request) bool {
			origin := r.Header.Get("Origin")
			if origin == "" {
				// Non-browser clients (desktop) omit Origin.
				return true
			}
			// Fail closed: empty allow-list must not accept arbitrary browser Origins (CSWSH).
			if len(h.allowedOrigins) == 0 {
				log.Printf("[live-stream] CheckOrigin rejected origin=%q (no CORS_ALLOWED_ORIGINS configured)", origin)
				return false
			}
			if h.allowedOrigins[origin] {
				return true
			}
			log.Printf("[live-stream] CheckOrigin rejected origin=%q", origin)
			return false
		},
	}
	return h
}

// LiveStreamEmployee is the rail list DTO.
type LiveStreamEmployee struct {
	EmployeeID      string `json:"employeeId"`
	Name            string `json:"name"`
	Department      string `json:"department"`
	Online          bool   `json:"online"`
	WsConnected     bool   `json:"wsConnected"` // presence socket GET /api/v1/ws
	Streaming       bool   `json:"streaming"`
	StreamAvailable bool   `json:"streamAvailable"`
	ClientConnected bool   `json:"clientConnected"` // live-stream push signaling socket
	ConsentMissing  bool   `json:"consentMissing"`
}

// ListEmployees handles GET /api/v1/live-stream/employees (JWTAuth).
// Optional query params (backward compatible when omitted):
//   - onlineOnly=true — return only online employees
//   - limit=N — cap returned rows (default unlimited; max 50 when set)
//   - departmentId=N — filter by employees.department_id
// When onlineOnly or limit is used, `total` is the full matching online count
// (not the truncated page length) so dashboards can show "Online · N".
func (h *StreamHandler) ListEmployees(c echo.Context) error {
	if !h.hub.Config().Enabled {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "Live stream is disabled",
		})
	}

	onlineOnly := strings.EqualFold(c.QueryParam("onlineOnly"), "true") || c.QueryParam("onlineOnly") == "1"
	limit := 0
	if raw := c.QueryParam("limit"); raw != "" {
		n, err := strconv.Atoi(raw)
		if err != nil || n < 1 {
			return c.JSON(http.StatusBadRequest, dto.APIError{
				Code: http.StatusBadRequest, Message: "invalid limit",
			})
		}
		if n > 50 {
			n = 50
		}
		limit = n
	}
	var departmentID *int
	if raw := c.QueryParam("departmentId"); raw != "" {
		id, err := strconv.Atoi(raw)
		if err != nil || id < 1 {
			return c.JSON(http.StatusBadRequest, dto.APIError{
				Code: http.StatusBadRequest, Message: "invalid departmentId",
			})
		}
		departmentID = &id
	}

	employees, err := h.employeeRepo.ListAll(c.Request().Context())
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to list employees", Detail: err.Error(),
		})
	}
	heartbeats, err := h.taRepo.ListLastHeartbeats(c.Request().Context())
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to load heartbeats", Detail: err.Error(),
		})
	}
	accepted, err := h.termsConsentRepo.ListAcceptedEmployeeIDs(c.Request().Context(), stream.FeatureID)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to load consent", Detail: err.Error(),
		})
	}

	now := time.Now().UTC()
	presenceOn := h.presence != nil && h.presence.Config().Enabled
	out := make([]LiveStreamEmployee, 0, len(employees))
	onlineTotal := 0
	for _, e := range employees {
		if departmentID != nil && e.DepartmentID != *departmentID {
			continue
		}
		snap := h.hub.Snapshot(e.EmployeeID)
		wsConnected := presenceOn && h.presence.IsConnected(e.EmployeeID)
		hb, hasHB := heartbeats[e.EmployeeID]
		hbOnline := hasHB && now.Sub(hb.UTC()) <= liveStreamOnlineWindow
		online := employeeLiveOnline(presenceOn, wsConnected, hbOnline)
		if online {
			onlineTotal++
		}
		if onlineOnly && !online {
			continue
		}
		row := LiveStreamEmployee{
			EmployeeID:      e.EmployeeID,
			Name:            e.Name,
			Department:      e.Department,
			Online:          online,
			WsConnected:     wsConnected,
			Streaming:       snap.Streaming,
			StreamAvailable: snap.StreamAvailable,
			ClientConnected: snap.ClientConnected,
			ConsentMissing:  !accepted[e.EmployeeID],
		}
		if limit > 0 && len(out) >= limit {
			continue
		}
		out = append(out, row)
	}

	total := len(out)
	if onlineOnly || limit > 0 {
		if onlineOnly {
			total = onlineTotal
		} else {
			// limit without onlineOnly: total is full filtered set before truncate
			total = 0
			for _, e := range employees {
				if departmentID != nil && e.DepartmentID != *departmentID {
					continue
				}
				total++
			}
		}
	}
	return c.JSON(http.StatusOK, map[string]interface{}{"data": out, "total": total})
}

// employeeLiveOnline chooses Online for the live-stream rail.
// When presence WS is enabled, a live control socket is authoritative (WsConnected).
// When presence is disabled, fall back to app_status last_heartbeat_at within the online window.
func employeeLiveOnline(presenceEnabled, wsConnected, hbOnline bool) bool {
	if presenceEnabled {
		return wsConnected
	}
	return hbOnline
}

// deviceStillActive re-checks a DeviceAuth device after WS upgrade (F14c).
// Missing deviceID (legacy employee JWT) or nil repo → treat as still active;
// max socket lifetime still bounds the connection.
func (h *StreamHandler) deviceStillActive(deviceID string) bool {
	if deviceID == "" || h.deviceRepo == nil {
		return true
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	ok, err := h.deviceRepo.IsActive(ctx, deviceID)
	if err != nil {
		log.Printf("[live-stream] device re-check error device=%s: %v", deviceID, err)
		return true // transient DB errors must not drop live publishers
	}
	return ok
}

// IssueWatchTicket handles GET /api/v1/live-stream/watch-ticket?employeeId=.
// Response includes ICE servers for the browser RTCPeerConnection.
func (h *StreamHandler) IssueWatchTicket(c echo.Context) error {
	if !h.hub.Config().Enabled {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "Live stream is disabled",
		})
	}

	empID := strings.TrimSpace(c.QueryParam("employeeId"))
	if empID == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code: http.StatusBadRequest, Message: "employeeId is required",
		})
	}
	userID, _ := c.Get("user_id").(string)
	if userID == "" {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code: http.StatusUnauthorized, Message: "Authentication required",
		})
	}
	if existing, err := h.employeeRepo.GetByEmployeeID(c.Request().Context(), empID); err != nil || existing == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code: http.StatusNotFound, Message: "Employee not found",
		})
	}
	accepted, err := h.termsConsentRepo.HasAccepted(c.Request().Context(), empID, stream.FeatureID, "")
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Consent check failed",
		})
	}
	if !accepted {
		return c.JSON(http.StatusForbidden, dto.APIError{
			Code: http.StatusForbidden, Message: "consent_required",
			Detail: "Employee has not accepted live_view terms",
		})
	}

	ticket, expiresIn, err := h.hub.IssueWatchTicket(userID, empID)
	if err != nil {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: err.Error(),
		})
	}

	resp := map[string]interface{}{
		"ticket":     ticket,
		"expiresIn":  expiresIn,
		"iceServers": h.hub.ICEServers(),
	}
	// Phase 2: when the publisher SFU lives on another instance, tell the web
	// client which base URL to open the watch WebSocket against.
	if base, err := h.hub.ResolveWatchTarget(empID); err != nil {
		log.Printf("[live-stream] ResolveWatchTarget employee=%s: %v", empID, err)
	} else if base != "" {
		resp["watchBaseUrl"] = base
	}
	return c.JSON(http.StatusOK, resp)
}

// UplinkProbe handles POST /api/v1/live-stream/uplink-probe (DeviceAuth).
// Discards up to 512 KiB so the client can time a small upload benchmark.
func (h *StreamHandler) UplinkProbe(c echo.Context) error {
	if !h.hub.Config().Enabled {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "Live stream is disabled",
		})
	}
	r := c.Request().Body
	if r == nil {
		return c.NoContent(http.StatusNoContent)
	}
	defer r.Close()
	limited := http.MaxBytesReader(c.Response(), r, uplinkProbeMaxBytes)
	buf := make([]byte, 32*1024)
	for {
		_, err := limited.Read(buf)
		if err != nil {
			break
		}
	}
	return c.NoContent(http.StatusNoContent)
}

type signalMsg struct {
	Type            string               `json:"type"`
	SDP             string               `json:"sdp,omitempty"`
	Candidate       string               `json:"candidate,omitempty"`
	SDPMLineIndex   *uint16              `json:"sdpMLineIndex,omitempty"`
	SDPMid          *string              `json:"sdpMid,omitempty"`
	Index           int                  `json:"index,omitempty"`
	Platform        string               `json:"platform,omitempty"`
	StreamAvailable bool                 `json:"streamAvailable,omitempty"`
	Version         string               `json:"version,omitempty"`
	SelectedMonitor int                  `json:"selectedMonitor,omitempty"`
	Monitors        []stream.MonitorInfo `json:"monitors,omitempty"`
}

// Push handles GET /api/v1/live-stream/push (DeviceAuth) — WebRTC publisher signaling.
func (h *StreamHandler) Push(c echo.Context) error {
	if !h.hub.Config().Enabled {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "Live stream is disabled",
		})
	}
	empID, ok := c.Get("employee_id").(string)
	if !ok || empID == "" {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code: http.StatusUnauthorized, Message: "Unauthorized employee context",
		})
	}
	deviceID, _ := c.Get("device_id").(string)
	accepted, err := h.termsConsentRepo.HasAccepted(c.Request().Context(), empID, stream.FeatureID, "")
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Consent check failed",
		})
	}
	if !accepted {
		log.Printf("[live-stream] push refused employee=%s reason=consent_required", empID)
		return c.JSON(http.StatusForbidden, dto.APIError{
			Code: http.StatusForbidden, Message: "consent_required",
			Detail: "Employee must accept live_view terms before streaming",
		})
	}

	conn, err := h.upgrader.Upgrade(c.Response(), c.Request(), nil)
	if err != nil {
		log.Printf("[live-stream] Push upgrade: %v", err)
		return nil
	}
	defer conn.Close()

	ctrl, gen, ok := h.hub.RegisterClient(empID)
	if !ok {
		_ = writeJSON(conn, map[string]string{"type": "error", "code": "disabled"})
		return nil
	}
	defer h.hub.UnregisterClient(empID, gen)

	log.Printf("[live-stream] push connected employee=%s (webrtc)", empID)

	var writeMu sync.Mutex
	write := func(v interface{}) error {
		writeMu.Lock()
		defer writeMu.Unlock()
		_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
		return conn.WriteJSON(v)
	}

	// Advertise ICE servers to the publisher.
	_ = write(map[string]interface{}{
		"type":       "ice_servers",
		"iceServers": h.hub.ICEServers(),
	})

	done := make(chan struct{})
	var once sync.Once
	closeDone := func() { once.Do(func() { close(done) }) }

	go func() {
		defer closeDone()
		ticker := time.NewTicker(wsPingPeriod)
		defer ticker.Stop()
		recheck := time.NewTicker(wsTokenRecheck)
		defer recheck.Stop()
		for {
			select {
			case ev, ok := <-ctrl:
				if !ok {
					return
				}
				payload := map[string]interface{}{"type": ev.Type}
				if ev.Type == "select_monitor" {
					payload["index"] = ev.MonitorIndex
				}
				if err := write(payload); err != nil {
					return
				}
			case <-ticker.C:
				writeMu.Lock()
				_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
				err := conn.WriteMessage(websocket.PingMessage, nil)
				writeMu.Unlock()
				if err != nil {
					return
				}
			case <-recheck.C:
				if !h.deviceStillActive(deviceID) {
					log.Printf("[live-stream] push device revoked employee=%s device=%s — closing", empID, deviceID)
					_ = conn.Close()
					return
				}
			case <-done:
				return
			}
		}
	}()

	_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))
	conn.SetPongHandler(func(string) error {
		_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))
		return nil
	})

	lifeTimer := time.AfterFunc(wsMaxSocketLife, func() {
		log.Printf("[live-stream] push max lifetime employee=%s — closing", empID)
		_ = conn.Close()
	})
	defer lifeTimer.Stop()

	for {
		_, data, err := conn.ReadMessage()
		if err != nil {
			closeDone()
			log.Printf("[live-stream] push disconnected employee=%s: %v", empID, err)
			return nil
		}
		_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))

		var msg signalMsg
		if json.Unmarshal(data, &msg) != nil {
			continue
		}
		switch msg.Type {
		case "hello":
			h.hub.SetCapability(empID, stream.Capability{
				Platform:        msg.Platform,
				StreamAvailable: msg.StreamAvailable,
				Version:         msg.Version,
				Monitors:        msg.Monitors,
				SelectedMonitor: msg.SelectedMonitor,
			})
			log.Printf("[live-stream] hello employee=%s platform=%s available=%v monitors=%d version=%s",
				empID, msg.Platform, msg.StreamAvailable, len(msg.Monitors), msg.Version)
		case "offer":
			trickle := newIceTrickle(write)
			answer, err := h.hub.SFU().AcceptPublisherOffer(empID, msg.SDP, trickle.on)
			if err != nil {
				log.Printf("[live-stream] publisher offer employee=%s: %v", empID, err)
				_ = write(map[string]string{"type": "error", "code": "offer_failed"})
				continue
			}
			log.Printf("[live-stream] publisher offer accepted employee=%s", empID)
			_ = write(map[string]string{"type": "answer", "sdp": answer})
			trickle.answerMarked()
		case "ice":
			cand := webrtc.ICECandidateInit{Candidate: msg.Candidate}
			if msg.SDPMLineIndex != nil {
				cand.SDPMLineIndex = msg.SDPMLineIndex
			}
			if msg.SDPMid != nil {
				cand.SDPMid = msg.SDPMid
			}
			if err := h.hub.SFU().AddPublisherICE(empID, cand); err != nil {
				log.Printf("[live-stream] publisher ice employee=%s: %v", empID, err)
			}
		}
	}
}

// iceTrickle buffers SFU ICE candidates until the answer has been written to
// the socket. Pion starts ICE gathering during SetLocalDescription, so the
// candidate callback can fire BEFORE the answer message is sent. A peer that
// receives ICE before its remote description exists throws InvalidStateError on
// addIceCandidate and drops that candidate, which strands the connection
// (status LIVE, but no frames ever arrive).
type iceTrickle struct {
	mu       sync.Mutex
	answered bool
	pending  []map[string]interface{}
	write    func(interface{}) error
}

func newIceTrickle(write func(interface{}) error) *iceTrickle {
	return &iceTrickle{write: write}
}

func (t *iceTrickle) on(c webrtc.ICECandidateInit) {
	if c.Candidate == "" {
		return
	}
	payload := map[string]interface{}{
		"type":      "ice",
		"candidate": c.Candidate,
	}
	if c.SDPMLineIndex != nil {
		payload["sdpMLineIndex"] = *c.SDPMLineIndex
	}
	if c.SDPMid != nil {
		payload["sdpMid"] = *c.SDPMid
	}
	t.mu.Lock()
	if !t.answered {
		t.pending = append(t.pending, payload)
		t.mu.Unlock()
		return
	}
	t.mu.Unlock()
	_ = t.write(payload)
}

// answerMarked flushes buffered candidates. Must be called AFTER the answer
// message has been written to the socket.
func (t *iceTrickle) answerMarked() {
	t.mu.Lock()
	t.answered = true
	pending := t.pending
	t.pending = nil
	t.mu.Unlock()
	for _, payload := range pending {
		_ = t.write(payload)
	}
}

func statusPayload(snap stream.EmployeeSnapshot) map[string]interface{} {
	return map[string]interface{}{
		"type":            "status",
		"streaming":       snap.Streaming,
		"streamAvailable": snap.StreamAvailable,
		"clientConnected": snap.ClientConnected,
		"consentMissing":  false,
		"monitors":        snap.Monitors,
		"selectedMonitor": snap.SelectedMonitor,
	}
}

// Watch handles GET /api/v1/live-stream/watch?employeeId=&ticket= — WebRTC subscriber signaling.
func (h *StreamHandler) Watch(c echo.Context) error {
	if !h.hub.Config().Enabled {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "Live stream is disabled",
		})
	}

	empID := strings.TrimSpace(c.QueryParam("employeeId"))
	ticket := strings.TrimSpace(c.QueryParam("ticket"))
	if empID == "" || ticket == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code: http.StatusBadRequest, Message: "employeeId and ticket are required",
		})
	}
	// Route BEFORE burning the ticket so Redis can redirect to the publisher instance.
	if base, err := h.hub.ResolveWatchTarget(empID); err != nil {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: err.Error(),
		})
	} else if base != "" {
		q := url.Values{}
		q.Set("employeeId", empID)
		q.Set("ticket", ticket)
		loc := base + "/api/v1/live-stream/watch?" + q.Encode()
		return c.Redirect(http.StatusTemporaryRedirect, loc)
	}
	watcherUserID, err := h.hub.ConsumeWatchTicket(ticket, empID)
	if err != nil {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code: http.StatusUnauthorized, Message: "invalid_or_expired_ticket",
		})
	}
	if existing, err := h.employeeRepo.GetByEmployeeID(c.Request().Context(), empID); err != nil || existing == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code: http.StatusNotFound, Message: "Employee not found",
		})
	}
	accepted, err := h.termsConsentRepo.HasAccepted(c.Request().Context(), empID, stream.FeatureID, "")
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Consent check failed",
		})
	}
	if !accepted {
		return c.JSON(http.StatusForbidden, dto.APIError{
			Code: http.StatusForbidden, Message: "consent_required",
		})
	}

	if err := h.hub.CanSubscribe(empID); err != nil {
		status := http.StatusServiceUnavailable
		if errors.Is(err, stream.ErrTooManyWatchers) || errors.Is(err, stream.ErrTooManyStreams) {
			status = http.StatusTooManyRequests
		}
		return c.JSON(status, dto.APIError{
			Code: status, Message: err.Error(),
		})
	}

	conn, err := h.upgrader.Upgrade(c.Response(), c.Request(), nil)
	if err != nil {
		log.Printf("[live-stream] Watch upgrade: %v", err)
		return nil
	}
	defer conn.Close()

	watcherID, err := h.hub.Subscribe(empID)
	if err != nil {
		_ = writeJSON(conn, map[string]interface{}{"type": "error", "code": err.Error()})
		return nil
	}
	defer h.hub.Unsubscribe(empID, watcherID)

	log.Printf("[live-stream] watch connected employee=%s watcher=%d user=%s (webrtc)", empID, watcherID, watcherUserID)

	var writeMu sync.Mutex
	write := func(v interface{}) error {
		writeMu.Lock()
		defer writeMu.Unlock()
		_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
		return conn.WriteJSON(v)
	}

	_ = write(map[string]interface{}{
		"type":       "ice_servers",
		"iceServers": h.hub.ICEServers(),
	})
	_ = write(statusPayload(h.hub.Snapshot(empID)))

	done := make(chan struct{})
	var once sync.Once
	closeDone := func() { once.Do(func() { close(done) }) }

	// Late-track renegotiation: registered only when an answer went out without media.
	trackNotify := make(chan struct{}, 1)
	go func() {
		defer closeDone()
		ticker := time.NewTicker(wsPingPeriod)
		defer ticker.Stop()
		statusTicker := time.NewTicker(2 * time.Second)
		defer statusTicker.Stop()
		for {
			select {
			case <-trackNotify:
				if err := write(map[string]string{"type": "track_ready"}); err != nil {
					return
				}
			case <-statusTicker.C:
				if err := write(statusPayload(h.hub.Snapshot(empID))); err != nil {
					return
				}
			case <-ticker.C:
				writeMu.Lock()
				_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
				err := conn.WriteMessage(websocket.PingMessage, nil)
				writeMu.Unlock()
				if err != nil {
					return
				}
			case <-done:
				return
			}
		}
	}()

	_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))
	conn.SetPongHandler(func(string) error {
		_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))
		return nil
	})

	lifeTimer := time.AfterFunc(wsMaxSocketLife, func() {
		log.Printf("[live-stream] watch max lifetime employee=%s watcher=%d — closing", empID, watcherID)
		_ = conn.Close()
	})
	defer lifeTimer.Stop()

	for {
		_, data, err := conn.ReadMessage()
		if err != nil {
			closeDone()
			log.Printf("[live-stream] watch disconnected employee=%s watcher=%d", empID, watcherID)
			return nil
		}
		_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))

		var msg signalMsg
		if json.Unmarshal(data, &msg) != nil {
			continue
		}
		switch msg.Type {
		case "select_monitor":
			h.hub.SelectMonitor(empID, msg.Index)
		case "offer":
			sdp := msg.SDP
			select {
			case h.offerSem <- struct{}{}:
			default:
				_ = write(map[string]string{"type": "error", "code": "too_many_offers"})
				continue
			}
			go func() {
				defer func() { <-h.offerSem }()
				h.hub.SFU().UnwatchTrackReady(empID, watcherID)
			trickle := newIceTrickle(write)
			answer, withTrack, err := h.hub.SFU().AcceptSubscriberOffer(empID, watcherID, sdp, trickle.on)
			if err != nil {
				if errors.Is(err, stream.ErrSuperseded) {
					log.Printf("[live-stream] subscriber offer superseded employee=%s watcher=%d", empID, watcherID)
					return
				}
				log.Printf("[live-stream] subscriber offer employee=%s: %v", empID, err)
				_ = write(map[string]string{"type": "error", "code": "offer_failed"})
				return
			}
			_ = write(map[string]string{"type": "answer", "sdp": answer})
			trickle.answerMarked()
				if withTrack {
					// Late joiner: VP8 decoder needs an IDR or the tile stays LIVE/black.
					h.hub.NudgePublisherKeyframe(empID)
					return
				}
				// Answer had no media — wait for publisher track, then one renegotiate.
				ready := h.hub.SFU().WatchTrackReady(empID, watcherID)
				go func() {
					select {
					case <-ready:
						select {
						case trackNotify <- struct{}{}:
						default:
						}
					case <-done:
					}
					h.hub.SFU().UnwatchTrackReady(empID, watcherID)
				}()
			}()
		case "ice":
			cand := webrtc.ICECandidateInit{Candidate: msg.Candidate}
			if msg.SDPMLineIndex != nil {
				cand.SDPMLineIndex = msg.SDPMLineIndex
			}
			if msg.SDPMid != nil {
				cand.SDPMid = msg.SDPMid
			}
			_ = h.hub.SFU().AddSubscriberICE(empID, watcherID, cand)
		}
	}
}

func writeJSON(conn *websocket.Conn, v interface{}) error {
	_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
	return conn.WriteJSON(v)
}
