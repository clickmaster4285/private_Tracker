package handlers

import (
	"fmt"
	"net/http"
	"os"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

var screenshotHHMM = regexp.MustCompile(`^([01]\d|2[0-3]):[0-5]\d$`)

type ScreenshotHandler struct {
	svc *services.ScreenshotService
}

func NewScreenshotHandler(svc *services.ScreenshotService) *ScreenshotHandler {
	return &ScreenshotHandler{svc: svc}
}

// Upload handles DeviceAuth multipart POST /screenshots/upload.
func (h *ScreenshotHandler) Upload(c echo.Context) error {
	empID, _ := c.Get("employee_id").(string)
	if strings.TrimSpace(empID) == "" {
		return echo.NewHTTPError(http.StatusUnauthorized, "missing employee")
	}

	screenshotID := strings.TrimSpace(c.FormValue("screenshotId"))
	if screenshotID == "" {
		return echo.NewHTTPError(http.StatusBadRequest, "screenshotId (UUID) required")
	}

	// Idempotent fast path: already accepted → 200 without reading the file body again.
	if existing, err := h.svc.Get(c.Request().Context(), screenshotID); err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	} else if existing != nil {
		if existing.EmployeeID != empID {
			return echo.NewHTTPError(http.StatusForbidden, "screenshotId belongs to another employee")
		}
		return c.JSON(http.StatusOK, map[string]any{
			"accepted":         true,
			"id":               existing.ID,
			"relPath":          existing.RelPath,
			"byteSize":         existing.ByteSize,
			"idempotentReplay": true,
		})
	}

	file, err := c.FormFile("file")
	if err != nil {
		return echo.NewHTTPError(http.StatusBadRequest, "file required")
	}
	if file.Size > h.svc.MaxBytes() {
		return echo.NewHTTPError(http.StatusRequestEntityTooLarge,
			fmt.Sprintf("file too large (max %d bytes)", h.svc.MaxBytes()))
	}

	src, err := file.Open()
	if err != nil {
		return echo.NewHTTPError(http.StatusBadRequest, "cannot open file")
	}
	defer src.Close()

	capturedAt := time.Now().UTC()
	if raw := strings.TrimSpace(c.FormValue("capturedAt")); raw != "" {
		if t, e := time.Parse(time.RFC3339Nano, raw); e == nil {
			capturedAt = t.UTC()
		} else if t, e := time.Parse(time.RFC3339, raw); e == nil {
			capturedAt = t.UTC()
		}
	}
	machineID := strings.TrimSpace(c.FormValue("machineId"))
	width, _ := strconv.Atoi(c.FormValue("width"))
	height, _ := strconv.Atoi(c.FormValue("height"))
	contentType := strings.TrimSpace(c.FormValue("contentType"))
	if contentType == "" {
		contentType = file.Header.Get("Content-Type")
	}
	if contentType == "" {
		contentType = "image/webp"
	}

	result, err := h.svc.SaveUpload(
		c.Request().Context(),
		screenshotID, empID, machineID, capturedAt, width, height, contentType,
		src, file.Size,
	)
	if err != nil {
		msg := err.Error()
		if strings.Contains(msg, "too large") {
			return echo.NewHTTPError(http.StatusRequestEntityTooLarge, msg)
		}
		if strings.Contains(msg, "only image/webp") || strings.Contains(msg, "screenshotId") {
			return echo.NewHTTPError(http.StatusBadRequest, msg)
		}
		if strings.Contains(msg, "another employee") {
			return echo.NewHTTPError(http.StatusForbidden, msg)
		}
		return echo.NewHTTPError(http.StatusInternalServerError, msg)
	}

	return c.JSON(http.StatusOK, map[string]any{
		"accepted":         true,
		"id":               result.Row.ID,
		"relPath":          result.Row.RelPath,
		"byteSize":         result.Row.ByteSize,
		"idempotentReplay": result.Replay,
	})
}

// List handles JWT GET /screenshots (infinite scroll metadata).
// Filters are applied server-side only:
//   employeeId, from, to (date or RFC3339), timeFrom, timeTo (HH:mm), timeZone (IANA).
func (h *ScreenshotHandler) List(c echo.Context) error {
	employeeID := strings.TrimSpace(c.QueryParam("employeeId"))
	limit, _ := strconv.Atoi(c.QueryParam("limit"))
	if limit <= 0 {
		limit = 40
	}

	f := repository.ScreenshotListFilter{
		EmployeeID: employeeID,
		Limit:      limit,
	}

	if raw := strings.TrimSpace(c.QueryParam("from")); raw != "" {
		if t, e := time.Parse(time.RFC3339, raw); e == nil {
			f.From = &t
		} else if t, e := time.Parse("2006-01-02", raw); e == nil {
			f.From = &t
		} else {
			return echo.NewHTTPError(http.StatusBadRequest, "invalid from (use YYYY-MM-DD or RFC3339)")
		}
	}
	if raw := strings.TrimSpace(c.QueryParam("to")); raw != "" {
		if t, e := time.Parse(time.RFC3339, raw); e == nil {
			f.To = &t
		} else if t, e := time.Parse("2006-01-02", raw); e == nil {
			end := t.Add(24*time.Hour - time.Nanosecond)
			f.To = &end
		} else {
			return echo.NewHTTPError(http.StatusBadRequest, "invalid to (use YYYY-MM-DD or RFC3339)")
		}
	}

	timeFrom := strings.TrimSpace(c.QueryParam("timeFrom"))
	timeTo := strings.TrimSpace(c.QueryParam("timeTo"))
	if timeFrom != "" {
		if !screenshotHHMM.MatchString(timeFrom) {
			return echo.NewHTTPError(http.StatusBadRequest, "invalid timeFrom (use HH:mm)")
		}
		f.TimeFrom = timeFrom
	}
	if timeTo != "" {
		if !screenshotHHMM.MatchString(timeTo) {
			return echo.NewHTTPError(http.StatusBadRequest, "invalid timeTo (use HH:mm)")
		}
		f.TimeTo = timeTo
	}

	tz := strings.TrimSpace(c.QueryParam("timeZone"))
	if tz == "" {
		tz = "UTC"
	}
	if _, err := time.LoadLocation(tz); err != nil {
		return echo.NewHTTPError(http.StatusBadRequest, "invalid timeZone (IANA name required)")
	}
	f.TimeZone = tz

	if raw := strings.TrimSpace(c.QueryParam("cursor")); raw != "" {
		if t, e := time.Parse(time.RFC3339Nano, raw); e == nil {
			f.Cursor = &t
		} else if t, e := time.Parse(time.RFC3339, raw); e == nil {
			f.Cursor = &t
		}
	}

	items, err := h.svc.List(c.Request().Context(), f)
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}

	var nextCursor string
	if len(items) > 0 {
		nextCursor = items[len(items)-1].CapturedAt.UTC().Format(time.RFC3339Nano)
	}
	return c.JSON(http.StatusOK, map[string]any{
		"data":       items,
		"nextCursor": nextCursor,
		"hasMore":    len(items) >= limit,
	})
}

// Image serves the WebP via ServeContent (lazy load / Range).
func (h *ScreenshotHandler) Image(c echo.Context) error {
	id := strings.TrimSpace(c.Param("id"))
	if id == "" {
		return echo.NewHTTPError(http.StatusBadRequest, "id required")
	}
	row, err := h.svc.Get(c.Request().Context(), id)
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if row == nil {
		return echo.NewHTTPError(http.StatusNotFound, "not found")
	}
	abs, err := h.svc.AbsolutePath(row.RelPath)
	if err != nil {
		return echo.NewHTTPError(http.StatusBadRequest, "invalid path")
	}
	f, err := os.Open(abs)
	if err != nil {
		if os.IsNotExist(err) {
			return echo.NewHTTPError(http.StatusNotFound, "file missing")
		}
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	defer f.Close()
	st, err := f.Stat()
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	ct := row.ContentType
	if ct == "" {
		ct = "image/webp"
	}
	c.Response().Header().Set(echo.HeaderContentType, ct)
	http.ServeContent(c.Response(), c.Request(), st.Name(), st.ModTime(), f)
	return nil
}
