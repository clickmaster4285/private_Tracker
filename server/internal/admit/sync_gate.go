package admit

import (
	"net/http"
	"strconv"

	"github.com/labstack/echo/v4"
	"golang.org/x/sync/semaphore"
)

// SyncGate bounds concurrent DeviceAuth sync handlers so Postgres pool
// connections are not exhausted under a 1000-agent storm.
type SyncGate struct {
	sem *semaphore.Weighted
}

// NewSyncGate allows at most maxConcurrent in-flight sync requests.
func NewSyncGate(maxConcurrent int64) *SyncGate {
	if maxConcurrent < 1 {
		maxConcurrent = 1
	}
	return &SyncGate{sem: semaphore.NewWeighted(maxConcurrent)}
}

// Middleware rejects with 503 + Retry-After when the gate is full (non-blocking try).
func (g *SyncGate) Middleware() echo.MiddlewareFunc {
	return func(next echo.HandlerFunc) echo.HandlerFunc {
		return func(c echo.Context) error {
			if !g.sem.TryAcquire(1) {
				c.Response().Header().Set("Retry-After", "2")
				return c.JSON(http.StatusServiceUnavailable, map[string]any{
					"code":    http.StatusServiceUnavailable,
					"message": "sync admission saturated — retry after Retry-After seconds",
					"detail":  "retry_after_sec=2",
				})
			}
			defer g.sem.Release(1)
			return next(c)
		}
	}
}

// MaxFromPool suggests a sync concurrency from DB pool size (leave headroom).
func MaxFromPool(maxOpenConns int) int64 {
	if maxOpenConns < 4 {
		return 2
	}
	// Cap sync at ~60% of pool so JWT reads / sweeps still get connections.
	n := int64(maxOpenConns) * 3 / 5
	if n < 2 {
		return 2
	}
	return n
}

// FormatRetryAfter is a tiny helper for tests/docs.
func FormatRetryAfter(sec int) string {
	return strconv.Itoa(sec)
}
