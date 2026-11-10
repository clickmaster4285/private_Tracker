package admit

import (
	"net/http"
	"strconv"
	"sync"
	"time"

	"github.com/labstack/echo/v4"
	"golang.org/x/time/rate"
)

// Limiter is a per-key token-bucket rate limiter (in-process).
// Suitable for a single API process; multi-instance deploys get per-process budgets
// (still useful) — Redis-backed global RL can be layered later without API change.
type Limiter struct {
	mu       sync.Mutex
	limiters map[string]*visitor
	rps      rate.Limit
	burst    int
	ttl      time.Duration
}

type visitor struct {
	limiter  *rate.Limiter
	lastSeen time.Time
}

// NewLimiter creates a limiter that allows rps events/sec with the given burst.
func NewLimiter(rps float64, burst int) *Limiter {
	if rps <= 0 {
		rps = 1
	}
	if burst < 1 {
		burst = 1
	}
	l := &Limiter{
		limiters: make(map[string]*visitor),
		rps:      rate.Limit(rps),
		burst:    burst,
		ttl:      10 * time.Minute,
	}
	go l.gc()
	return l
}

func (l *Limiter) allow(key string) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	v, ok := l.limiters[key]
	if !ok {
		v = &visitor{limiter: rate.NewLimiter(l.rps, l.burst)}
		l.limiters[key] = v
	}
	v.lastSeen = time.Now()
	return v.limiter.Allow()
}

func (l *Limiter) gc() {
	t := time.NewTicker(time.Minute)
	defer t.Stop()
	for range t.C {
		cutoff := time.Now().Add(-l.ttl)
		l.mu.Lock()
		for k, v := range l.limiters {
			if v.lastSeen.Before(cutoff) {
				delete(l.limiters, k)
			}
		}
		l.mu.Unlock()
	}
}

// Middleware returns Echo middleware that rate-limits by keyFn.
// On reject: 429 + Retry-After: 1.
func (l *Limiter) Middleware(keyFn func(echo.Context) string) echo.MiddlewareFunc {
	return func(next echo.HandlerFunc) echo.HandlerFunc {
		return func(c echo.Context) error {
			key := keyFn(c)
			if key == "" {
				key = c.RealIP()
			}
			if !l.allow(key) {
				c.Response().Header().Set("Retry-After", "1")
				return c.JSON(http.StatusTooManyRequests, map[string]any{
					"code":    http.StatusTooManyRequests,
					"message": "rate limit exceeded — retry after Retry-After seconds",
					"detail":  "retry_after_sec=1",
				})
			}
			return next(c)
		}
	}
}

// RetryAfterSeconds parses Retry-After for clients; exported for tests.
func RetryAfterSeconds(h http.Header) int {
	v := h.Get("Retry-After")
	if v == "" {
		return 0
	}
	n, err := strconv.Atoi(v)
	if err != nil || n < 0 {
		return 0
	}
	return n
}
