package handlers

import (
	"errors"
	"log"
	"net/http"
	"os"
	"strconv"
	"sync"
	"time"

	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
	"golang.org/x/time/rate"
)

// ReleaseHandler serves release metadata and installer downloads from the server's
// local setup folder (RELEASES_DIR). Public: the website /releases page and the
// desktop updaters (client + Windows service) call it without login.
type ReleaseHandler struct {
	svc *services.LocalReleaseService

	// Per-IP soft rate limit for downloads (abuse prevention; no login required).
	limiters sync.Map // string → *rate.Limiter

	// Global concurrent body downloads (fleet stampede admission).
	downloadSlots chan struct{}
	retryAfterSec int
}

// NewReleaseHandler builds the handler. maxConcurrent caps simultaneous GET body
// streams (HEAD is free). Values ≤0 default to 25. retryAfterSec is returned on 429
// when the gate is full (default 60).
func NewReleaseHandler(svc *services.LocalReleaseService, maxConcurrent, retryAfterSec int) *ReleaseHandler {
	if maxConcurrent <= 0 {
		maxConcurrent = 25
	}
	if retryAfterSec <= 0 {
		retryAfterSec = 60
	}
	return &ReleaseHandler{
		svc:           svc,
		downloadSlots: make(chan struct{}, maxConcurrent),
		retryAfterSec: retryAfterSec,
	}
}

func (h *ReleaseHandler) limiterFor(ip string) *rate.Limiter {
	if ip == "" {
		ip = "unknown"
	}
	if v, ok := h.limiters.Load(ip); ok {
		return v.(*rate.Limiter)
	}
	// ~30 downloads/minute with a burst of 5 — enough for real users, harsh on scrapers.
	lim := rate.NewLimiter(rate.Every(2*time.Second), 5)
	actual, _ := h.limiters.LoadOrStore(ip, lim)
	return actual.(*rate.Limiter)
}

// ListReleases — GET /api/v1/releases (public).
func (h *ReleaseHandler) ListReleases(c echo.Context) error {
	if h.svc == nil {
		return c.JSON(http.StatusServiceUnavailable, map[string]string{
			"error": "Release downloads are temporarily unavailable",
		})
	}
	resp, err := h.svc.ListReleases()
	if err != nil {
		log.Printf("[releases] list error: %v", err)
		return c.JSON(http.StatusInternalServerError, map[string]string{
			"error": "Could not read releases",
		})
	}
	// Short cache: updaters poll every minute and must see a new installer quickly.
	c.Response().Header().Set("Cache-Control", "no-cache")
	return c.JSON(http.StatusOK, resp)
}

// DownloadAsset — GET|HEAD /api/v1/downloads/:assetId (public). Range is supported
// (http.ServeContent) so updaters can resume a partial download.
func (h *ReleaseHandler) DownloadAsset(c echo.Context) error {
	if h.svc == nil {
		return c.JSON(http.StatusServiceUnavailable, map[string]string{
			"error": "Release downloads are temporarily unavailable",
		})
	}

	assetID, err := strconv.ParseInt(c.Param("assetId"), 10, 64)
	if err != nil || assetID <= 0 {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "Invalid asset id"})
	}

	isHead := c.Request().Method == http.MethodHead
	if !isHead && !h.limiterFor(c.RealIP()).Allow() {
		c.Response().Header().Set("Retry-After", strconv.Itoa(h.retryAfterSec))
		return c.JSON(http.StatusTooManyRequests, map[string]string{
			"error": "Too many download requests; try again shortly",
		})
	}

	// Global fleet admission — only body GETs hold a slot for the full ServeContent.
	if !isHead {
		select {
		case h.downloadSlots <- struct{}{}:
			defer func() { <-h.downloadSlots }()
		default:
			c.Response().Header().Set("Retry-After", strconv.Itoa(h.retryAfterSec))
			return c.JSON(http.StatusTooManyRequests, map[string]string{
				"error": "Installer download capacity full; retry after Retry-After seconds",
			})
		}
	}

	asset, err := h.svc.LookupAsset(assetID)
	if err != nil {
		if errors.Is(err, services.ErrAssetNotFound) {
			return c.JSON(http.StatusNotFound, map[string]string{"error": "Installer not found"})
		}
		log.Printf("[releases] lookup asset=%d error: %v", assetID, err)
		return c.JSON(http.StatusInternalServerError, map[string]string{"error": "Could not read releases"})
	}

	f, err := os.Open(asset.Path)
	if err != nil {
		log.Printf("[releases] open %s: %v", asset.Path, err)
		return c.JSON(http.StatusNotFound, map[string]string{"error": "Installer not found"})
	}
	defer f.Close()

	hdr := c.Response().Header()
	hdr.Set("Content-Disposition", services.SafeContentDisposition(asset.Name))
	hdr.Set("Content-Type", asset.ContentType)
	hdr.Set("X-Content-Type-Options", "nosniff")
	hdr.Set("Cache-Control", "private, no-store")

	start := time.Now()
	http.ServeContent(c.Response(), c.Request(), asset.Name, asset.ModTime, f)
	if !isHead {
		log.Printf("[releases] download asset=%d file=%s version=%s status=%d bytes=%d dur=%s",
			assetID, asset.Name, asset.Version, c.Response().Status, c.Response().Size,
			time.Since(start).Round(time.Millisecond))
	}
	return nil
}
