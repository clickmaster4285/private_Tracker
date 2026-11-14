package admit

import (
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/labstack/echo/v4"
)

func TestLimiterRejectsAfterBurst(t *testing.T) {
	l := NewLimiter(1, 2) // 1 rps, burst 2
	e := echo.New()
	h := l.Middleware(func(c echo.Context) string { return "k" })(func(c echo.Context) error {
		return c.NoContent(http.StatusOK)
	})

	ok := 0
	denied := 0
	for i := 0; i < 5; i++ {
		req := httptest.NewRequest(http.MethodPost, "/", nil)
		rec := httptest.NewRecorder()
		c := e.NewContext(req, rec)
		_ = h(c)
		if rec.Code == http.StatusOK {
			ok++
		}
		if rec.Code == http.StatusTooManyRequests {
			denied++
			if rec.Header().Get("Retry-After") == "" {
				t.Fatal("expected Retry-After header")
			}
		}
	}
	if ok < 1 || denied < 1 {
		t.Fatalf("expected mix of ok/denied, got ok=%d denied=%d", ok, denied)
	}
}

func TestSyncGateSaturates(t *testing.T) {
	g := NewSyncGate(1)
	e := echo.New()
	entered := make(chan struct{})
	release := make(chan struct{})

	h := g.Middleware()(func(c echo.Context) error {
		close(entered)
		<-release
		return c.NoContent(http.StatusOK)
	})

	go func() {
		req := httptest.NewRequest(http.MethodPost, "/sync", nil)
		rec := httptest.NewRecorder()
		c := e.NewContext(req, rec)
		_ = h(c)
	}()
	<-entered

	req := httptest.NewRequest(http.MethodPost, "/sync", nil)
	rec := httptest.NewRecorder()
	c := e.NewContext(req, rec)
	_ = h(c)
	if rec.Code != http.StatusServiceUnavailable {
		t.Fatalf("expected 503, got %d", rec.Code)
	}
	if rec.Header().Get("Retry-After") == "" {
		t.Fatal("expected Retry-After")
	}
	close(release)
}

func TestMaxFromPool(t *testing.T) {
	if MaxFromPool(25) != 15 {
		t.Fatalf("got %d", MaxFromPool(25))
	}
}
