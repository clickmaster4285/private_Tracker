package handlers

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"testing"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

func newReleaseFixture(t *testing.T) (*ReleaseHandler, *echo.Echo) {
	t.Helper()
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "v1.2.3.exe"), []byte("0123456789"), 0o644); err != nil {
		t.Fatal(err)
	}
	h := NewReleaseHandler(services.NewLocalReleaseService(dir), 25, 60)
	e := echo.New()
	e.GET("/api/v1/releases", h.ListReleases)
	e.GET("/api/v1/downloads/:assetId", h.DownloadAsset)
	e.HEAD("/api/v1/downloads/:assetId", h.DownloadAsset)
	return h, e
}

func TestDownloadGlobalAdmissionReturns429(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "v9.9.9.exe"), []byte("abcdefghij"), 0o644); err != nil {
		t.Fatal(err)
	}
	h := NewReleaseHandler(services.NewLocalReleaseService(dir), 1, 42)
	e := echo.New()
	e.GET("/api/v1/releases", h.ListReleases)
	e.GET("/api/v1/downloads/:assetId", h.DownloadAsset)

	body := listReleases(t, e)
	url := "/api/v1/downloads/" + strconv.FormatInt(body.Releases[0].Assets[0].ID, 10)

	// Hold the only slot so the next GET body is rejected (same package can see the channel).
	h.downloadSlots <- struct{}{}
	defer func() { <-h.downloadSlots }()

	rec := httptest.NewRecorder()
	e.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, url, nil))
	if rec.Code != http.StatusTooManyRequests {
		t.Fatalf("download status=%d want 429", rec.Code)
	}
	if rec.Header().Get("Retry-After") != "42" {
		t.Fatalf("Retry-After=%q", rec.Header().Get("Retry-After"))
	}
}

func listReleases(t *testing.T, e *echo.Echo) dto.ReleasesResponse {
	t.Helper()
	rec := httptest.NewRecorder()
	e.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/api/v1/releases", nil))
	if rec.Code != http.StatusOK {
		t.Fatalf("list status=%d", rec.Code)
	}
	var body dto.ReleasesResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &body); err != nil {
		t.Fatal(err)
	}
	return body
}

func TestListAndDownloadFromSetupDir(t *testing.T) {
	_, e := newReleaseFixture(t)
	body := listReleases(t, e)
	if body.LatestStable != "v1.2.3" || len(body.Releases[0].Assets) != 1 {
		t.Fatalf("body=%+v", body)
	}
	a := body.Releases[0].Assets[0]
	if a.Platform != "windows" || a.Size != 10 {
		t.Fatalf("asset=%+v", a)
	}
	url := "/api/v1/downloads/" + strconv.FormatInt(a.ID, 10)

	rec := httptest.NewRecorder()
	e.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, url, nil))
	if rec.Code != http.StatusOK || rec.Body.String() != "0123456789" {
		t.Fatalf("full download status=%d body=%q", rec.Code, rec.Body.String())
	}

	// Resume from byte 6 — updaters rely on Range.
	req := httptest.NewRequest(http.MethodGet, url, nil)
	req.Header.Set("Range", "bytes=6-")
	rec = httptest.NewRecorder()
	e.ServeHTTP(rec, req)
	if rec.Code != http.StatusPartialContent || rec.Body.String() != "6789" {
		t.Fatalf("range status=%d body=%q", rec.Code, rec.Body.String())
	}
}

func TestDownloadInvalidAndUnknownAssetID(t *testing.T) {
	_, e := newReleaseFixture(t)
	for path, want := range map[string]int{
		"/api/v1/downloads/abc":   http.StatusBadRequest,
		"/api/v1/downloads/99999": http.StatusNotFound,
	} {
		rec := httptest.NewRecorder()
		e.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, path, nil))
		if rec.Code != want {
			t.Fatalf("%s: status=%d want %d", path, rec.Code, want)
		}
	}
}
