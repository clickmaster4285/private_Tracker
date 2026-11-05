package handlers

import (
	"errors"
	"io"
	"log"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
	"golang.org/x/time/rate"
)

// ReleaseHandler serves public release metadata and the private-asset download bridge.
type ReleaseHandler struct {
	svc *services.GitHubReleaseService

	// Per-IP soft rate limit for downloads (abuse prevention; no login required).
	limiters sync.Map // string → *rate.Limiter
}

func NewReleaseHandler(svc *services.GitHubReleaseService) *ReleaseHandler {
	return &ReleaseHandler{svc: svc}
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
	if h.svc == nil || !h.svc.Configured() {
		return c.JSON(http.StatusServiceUnavailable, map[string]string{
			"error": "Release downloads are temporarily unavailable",
		})
	}
	resp, err := h.svc.ListReleases(c.Request().Context())
	if err != nil {
		return mapGitHubErr(c, err)
	}
	c.Response().Header().Set("Cache-Control", "public, max-age=60")
	return c.JSON(http.StatusOK, resp)
}

// DownloadAsset — GET|HEAD /api/v1/downloads/:assetId (public).
func (h *ReleaseHandler) DownloadAsset(c echo.Context) error {
	if h.svc == nil || !h.svc.Configured() {
		return c.JSON(http.StatusServiceUnavailable, map[string]string{
			"error": "Release downloads are temporarily unavailable",
		})
	}

	if !h.limiterFor(c.RealIP()).Allow() {
		return c.JSON(http.StatusTooManyRequests, map[string]string{
			"error": "Too many download requests; try again shortly",
		})
	}

	idStr := c.Param("assetId")
	assetID, err := strconv.ParseInt(idStr, 10, 64)
	if err != nil || assetID <= 0 {
		return c.JSON(http.StatusBadRequest, map[string]string{"error": "Invalid asset id"})
	}

	rangeHeader := strings.TrimSpace(c.Request().Header.Get("Range"))
	isHead := c.Request().Method == http.MethodHead

	// Warm metadata cache and reject unsatisfiable ranges before opening upstream.
	meta, err := h.svc.ListReleases(c.Request().Context())
	if err != nil {
		return mapGitHubErr(c, err)
	}
	var knownSize int64
	found := false
	for _, rel := range meta.Releases {
		for _, a := range rel.Assets {
			if a.ID == assetID {
				found = true
				knownSize = a.Size
				break
			}
		}
		if found {
			break
		}
	}
	var assetName, assetPlatform, assetCT string
	for _, rel := range meta.Releases {
		for _, a := range rel.Assets {
			if a.ID == assetID {
				assetName = a.Name
				assetPlatform = a.Platform
				assetCT = a.ContentType
				break
			}
		}
		if assetName != "" {
			break
		}
	}
	if !found || assetName == "" {
		return c.JSON(http.StatusNotFound, map[string]string{"error": "Installer not found"})
	}
	if rangeHeader != "" && knownSize > 0 {
		_, _, status, msg := services.ParseByteRange(rangeHeader, knownSize)
		if status == http.StatusRequestedRangeNotSatisfiable {
			c.Response().Header().Set("Content-Range", "bytes */"+strconv.FormatInt(knownSize, 10))
			return c.JSON(status, map[string]string{"error": msg})
		}
	}

	if assetCT == "" {
		assetCT = "application/octet-stream"
	}

	// HEAD is metadata-only — do not pull installer bytes from GitHub.
	if isHead {
		c.Response().Header().Set("Content-Disposition", services.SafeContentDisposition(assetName))
		c.Response().Header().Set("Content-Type", assetCT)
		c.Response().Header().Set("X-Content-Type-Options", "nosniff")
		c.Response().Header().Set("Cache-Control", "private, no-store")
		c.Response().Header().Set("Accept-Ranges", "bytes")
		if knownSize > 0 {
			c.Response().Header().Set("Content-Length", strconv.FormatInt(knownSize, 10))
		}
		return c.NoContent(http.StatusOK)
	}

	start := time.Now()
	stream, err := h.svc.OpenAssetStream(c.Request().Context(), assetID, rangeHeader)
	if err != nil {
		return mapGitHubErr(c, err)
	}
	defer stream.Body.Close()

	c.Response().Header().Set("Content-Disposition", services.SafeContentDisposition(stream.Filename))
	c.Response().Header().Set("Content-Type", stream.ContentType)
	c.Response().Header().Set("X-Content-Type-Options", "nosniff")
	c.Response().Header().Set("Cache-Control", "private, no-store")
	if stream.AcceptRanges || stream.Size > 0 {
		c.Response().Header().Set("Accept-Ranges", "bytes")
	}
	if stream.ContentRange != "" {
		c.Response().Header().Set("Content-Range", stream.ContentRange)
	}
	if stream.ContentLength >= 0 {
		c.Response().Header().Set("Content-Length", strconv.FormatInt(stream.ContentLength, 10))
	} else if stream.StatusCode == http.StatusOK && stream.Size > 0 && rangeHeader == "" {
		c.Response().Header().Set("Content-Length", strconv.FormatInt(stream.Size, 10))
	}

	status := stream.StatusCode
	if status == 0 {
		status = http.StatusOK
	}

	c.Response().WriteHeader(status)
	written, copyErr := io.Copy(c.Response(), stream.Body)
	dur := time.Since(start).Round(time.Millisecond)
	if copyErr != nil {
		// Client disconnect / upstream cut mid-stream — do not rewrite status (headers already sent).
		log.Printf("[releases] download asset=%d tag=%s platform=%s status=%d bytes=%d dur=%s result=interrupted err=%v",
			assetID, stream.TagName, stream.Platform, status, written, dur, copyErr)
		return nil
	}
	log.Printf("[releases] download asset=%d tag=%s platform=%s status=%d bytes=%d dur=%s result=ok",
		assetID, stream.TagName, assetPlatform, status, written, dur)
	return nil
}

func mapGitHubErr(c echo.Context, err error) error {
	switch {
	case errors.Is(err, services.ErrGitHubNotConfigured):
		return c.JSON(http.StatusServiceUnavailable, map[string]string{
			"error": "Release downloads are temporarily unavailable",
		})
	case errors.Is(err, services.ErrAssetNotFound), errors.Is(err, services.ErrAssetNotAllowed):
		return c.JSON(http.StatusNotFound, map[string]string{"error": "Installer not found"})
	case errors.Is(err, services.ErrGitHubUnauthorized):
		log.Printf("[releases] upstream auth failure (token invalid or insufficient scope)")
		return c.JSON(http.StatusServiceUnavailable, map[string]string{
			"error": "Release downloads are temporarily unavailable",
		})
	case errors.Is(err, services.ErrGitHubRateLimited):
		return c.JSON(http.StatusTooManyRequests, map[string]string{
			"error": "Upstream rate limit; try again later",
		})
	default:
		log.Printf("[releases] error: %v", err)
		return c.JSON(http.StatusBadGateway, map[string]string{
			"error": "Could not reach release source",
		})
	}
}
