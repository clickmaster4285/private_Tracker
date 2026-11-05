package handlers

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/alpha-ai-tracker/server/internal/config"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

func TestListReleasesUnavailableWithoutToken(t *testing.T) {
	svc := services.NewGitHubReleaseService(config.GitHubConfig{
		Owner: "o", Repo: "r", Token: "",
	})
	h := NewReleaseHandler(svc)
	e := echo.New()
	req := httptest.NewRequest(http.MethodGet, "/api/v1/releases", nil)
	rec := httptest.NewRecorder()
	c := e.NewContext(req, rec)

	if err := h.ListReleases(c); err != nil {
		t.Fatal(err)
	}
	if rec.Code != http.StatusServiceUnavailable {
		t.Fatalf("status=%d", rec.Code)
	}
	var body map[string]string
	_ = json.Unmarshal(rec.Body.Bytes(), &body)
	if body["error"] == "" {
		t.Fatal("expected error message")
	}
}

func TestDownloadInvalidAssetID(t *testing.T) {
	svc := services.NewGitHubReleaseService(config.GitHubConfig{
		Owner: "o", Repo: "r", Token: "x",
	})
	h := NewReleaseHandler(svc)
	e := echo.New()
	req := httptest.NewRequest(http.MethodGet, "/api/v1/downloads/nope", nil)
	rec := httptest.NewRecorder()
	c := e.NewContext(req, rec)
	c.SetParamNames("assetId")
	c.SetParamValues("nope")

	if err := h.DownloadAsset(c); err != nil {
		t.Fatal(err)
	}
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("status=%d body=%s", rec.Code, rec.Body.String())
	}
}
