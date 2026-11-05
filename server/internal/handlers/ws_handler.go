package handlers

import (
	"context"
	"encoding/json"
	"log"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/ws"
	"github.com/gorilla/websocket"
	"github.com/labstack/echo/v4"
)

const (
	presenceWriteWait  = 10 * time.Second
	presencePongWait   = 60 * time.Second
	presencePingPeriod = 30 * time.Second
	presenceMaxLife    = 4 * time.Hour
	presenceTokenCheck = 5 * time.Minute
)

// WsHandler serves the long-lived control / presence WebSocket (DeviceAuth).
type WsHandler struct {
	hub            *ws.Hub
	deviceRepo     *repository.DeviceRepo // optional — post-upgrade device re-check
	allowedOrigins map[string]bool
	upgrader       websocket.Upgrader
}

// NewWsHandler constructs the presence WS handler.
// deviceRepo may be nil (skips post-upgrade device re-validation).
func NewWsHandler(hub *ws.Hub, deviceRepo *repository.DeviceRepo, allowedOrigins []string) *WsHandler {
	originSet := make(map[string]bool, len(allowedOrigins))
	for _, o := range allowedOrigins {
		originSet[strings.TrimSpace(o)] = true
	}
	h := &WsHandler{
		hub:            hub,
		deviceRepo:     deviceRepo,
		allowedOrigins: originSet,
	}
	h.upgrader = websocket.Upgrader{
		ReadBufferSize:  1024,
		WriteBufferSize: 1024,
		CheckOrigin: func(r *http.Request) bool {
			origin := r.Header.Get("Origin")
			if origin == "" {
				// Non-browser clients (desktop) omit Origin.
				return true
			}
			// Fail closed: empty allow-list must not accept arbitrary browser Origins (CSWSH).
			if len(h.allowedOrigins) == 0 {
				log.Printf("[ws] CheckOrigin rejected origin=%q (no CORS_ALLOWED_ORIGINS configured)", origin)
				return false
			}
			if h.allowedOrigins[origin] {
				return true
			}
			log.Printf("[ws] CheckOrigin rejected origin=%q", origin)
			return false
		},
	}
	return h
}

// Connect handles GET /api/v1/ws (DeviceAuth) — keep-alive control channel.
func (h *WsHandler) Connect(c echo.Context) error {
	if !h.hub.Config().Enabled {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "Control WebSocket is disabled",
		})
	}

	empID, ok := c.Get("employee_id").(string)
	if !ok || empID == "" {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code: http.StatusUnauthorized, Message: "Unauthorized employee context",
		})
	}
	deviceID, _ := c.Get("device_id").(string)

	gen, ok := h.hub.Register(empID)
	if !ok {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "Control WebSocket at capacity",
		})
	}

	conn, err := h.upgrader.Upgrade(c.Response(), c.Request(), nil)
	if err != nil {
		h.hub.Unregister(empID, gen)
		log.Printf("[ws] upgrade failed employee=%s: %v", empID, err)
		return nil
	}
	defer func() {
		h.hub.Unregister(empID, gen)
		_ = conn.Close()
	}()

	log.Printf("[ws] connected employee=%s (live=%d)", empID, h.hub.Count())

	var writeMu sync.Mutex
	done := make(chan struct{})
	var once sync.Once
	closeDone := func() { once.Do(func() { close(done) }) }

	lifeTimer := time.AfterFunc(presenceMaxLife, func() {
		log.Printf("[ws] max lifetime employee=%s — closing", empID)
		_ = conn.Close()
	})
	defer lifeTimer.Stop()

	// Welcome + protocol-level ping pump + periodic device re-check (F14c).
	go func() {
		defer closeDone()
		writeMu.Lock()
		_ = conn.SetWriteDeadline(time.Now().Add(presenceWriteWait))
		err := conn.WriteJSON(map[string]string{"type": "welcome"})
		writeMu.Unlock()
		if err != nil {
			return
		}

		ticker := time.NewTicker(presencePingPeriod)
		defer ticker.Stop()
		recheck := time.NewTicker(presenceTokenCheck)
		defer recheck.Stop()
		for {
			select {
			case <-ticker.C:
				writeMu.Lock()
				_ = conn.SetWriteDeadline(time.Now().Add(presenceWriteWait))
				err := conn.WriteMessage(websocket.PingMessage, nil)
				writeMu.Unlock()
				if err != nil {
					return
				}
			case <-recheck.C:
				if !h.deviceStillActive(deviceID) {
					log.Printf("[ws] device revoked employee=%s device=%s — closing", empID, deviceID)
					_ = conn.Close()
					return
				}
			case <-done:
				return
			}
		}
	}()

	_ = conn.SetReadDeadline(time.Now().Add(presencePongWait))
	conn.SetPongHandler(func(string) error {
		_ = conn.SetReadDeadline(time.Now().Add(presencePongWait))
		h.hub.Touch(empID, gen)
		return nil
	})

	for {
		msgType, data, err := conn.ReadMessage()
		if err != nil {
			closeDone()
			log.Printf("[ws] disconnected employee=%s: %v", empID, err)
			return nil
		}
		_ = conn.SetReadDeadline(time.Now().Add(presencePongWait))
		h.hub.Touch(empID, gen)

		if msgType != websocket.TextMessage {
			continue
		}

		var msg struct {
			Type     string `json:"type"`
			Platform string `json:"platform"`
			Version  string `json:"version"`
		}
		if err := json.Unmarshal(data, &msg); err != nil {
			continue
		}

		switch msg.Type {
		case "hello":
			h.hub.SetHello(empID, gen, msg.Platform, msg.Version)
		case "ping":
			writeMu.Lock()
			_ = conn.SetWriteDeadline(time.Now().Add(presenceWriteWait))
			_ = conn.WriteJSON(map[string]string{"type": "pong"})
			writeMu.Unlock()
		default:
			// Ignore unknown control frames — channel is keep-alive only for now.
		}
	}
}

func (h *WsHandler) deviceStillActive(deviceID string) bool {
	if deviceID == "" || h.deviceRepo == nil {
		return true
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	ok, err := h.deviceRepo.IsActive(ctx, deviceID)
	if err != nil {
		log.Printf("[ws] device re-check error device=%s: %v", deviceID, err)
		return true
	}
	return ok
}
