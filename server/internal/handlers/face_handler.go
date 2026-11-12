package handlers

import (
	"net/http"
	"os"
	"strconv"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

type FaceHandler struct {
	svc          *services.FaceService
	workerAPIKey string
}

func NewFaceHandler(svc *services.FaceService, workerAPIKey string) *FaceHandler {
	return &FaceHandler{svc: svc, workerAPIKey: strings.TrimSpace(workerAPIKey)}
}

// CreateEnrollment handles JWT multipart POST /employees/:id/face-enrollment
func (h *FaceHandler) CreateEnrollment(c echo.Context) error {
	actor, _ := c.Get("user_id").(string)
	uploads, closer, err := h.parseMultipartUploads(c)
	if closer != nil {
		defer closer()
	}
	if err != nil {
		return err
	}

	result, err := h.svc.CreateEnrollment(c.Request().Context(), c.Param("id"), actor, uploads)
	if err != nil {
		msg := err.Error()
		switch {
		case strings.Contains(msg, "not found"):
			return echo.NewHTTPError(http.StatusNotFound, msg)
		case strings.Contains(msg, "too large"):
			return echo.NewHTTPError(http.StatusRequestEntityTooLarge, msg)
		case strings.Contains(msg, "invalid pose") || strings.Contains(msg, "unsupported") ||
			strings.Contains(msg, "required") || strings.Contains(msg, "too many"):
			return echo.NewHTTPError(http.StatusUnprocessableEntity, msg)
		default:
			return echo.NewHTTPError(http.StatusInternalServerError, msg)
		}
	}
	return c.JSON(http.StatusAccepted, result)
}

func (h *FaceHandler) parseMultipartUploads(c echo.Context) ([]services.FaceSampleUpload, func(), error) {
	form, err := c.MultipartForm()
	if err != nil {
		return nil, nil, echo.NewHTTPError(http.StatusBadRequest, "multipart form required")
	}

	files := form.File["files"]
	if len(files) == 0 {
		files = form.File["file"]
	}
	if len(files) == 0 {
		return nil, nil, echo.NewHTTPError(http.StatusBadRequest, "files required")
	}

	poseValues := form.Value["poseId"]
	clientIDs := form.Value["clientSampleId"]
	qualityValues := form.Value["qualityScore"]

	type openUpload struct {
		u      services.FaceSampleUpload
		closer interface{ Close() error }
	}
	opened := make([]openUpload, 0, len(files))
	cleanup := func() {
		for _, o := range opened {
			_ = o.closer.Close()
		}
	}

	for i, fh := range files {
		if fh.Size > h.svc.MaxBytes() {
			cleanup()
			return nil, nil, echo.NewHTTPError(http.StatusRequestEntityTooLarge,
				"file too large (max "+strconv.FormatInt(h.svc.MaxBytes(), 10)+" bytes)")
		}
		src, err := fh.Open()
		if err != nil {
			cleanup()
			return nil, nil, echo.NewHTTPError(http.StatusBadRequest, "cannot open file")
		}
		pose := "front"
		if i < len(poseValues) && strings.TrimSpace(poseValues[i]) != "" {
			pose = strings.TrimSpace(poseValues[i])
		} else if single := strings.TrimSpace(c.FormValue("poseId")); single != "" && len(files) == 1 {
			pose = single
		}
		clientSampleID := ""
		if i < len(clientIDs) {
			clientSampleID = strings.TrimSpace(clientIDs[i])
		}
		quality := 0.0
		if i < len(qualityValues) {
			quality, _ = strconv.ParseFloat(qualityValues[i], 64)
		}
		ct := fh.Header.Get("Content-Type")
		if ct == "" {
			ct = "image/jpeg"
		}
		opened = append(opened, openUpload{
			closer: src,
			u: services.FaceSampleUpload{
				PoseID:         pose,
				ClientSampleID: clientSampleID,
				ContentType:    ct,
				QualityScore:   quality,
				Reader:         src,
				SizeHint:       fh.Size,
			},
		})
	}

	uploads := make([]services.FaceSampleUpload, len(opened))
	for i := range opened {
		uploads[i] = opened[i].u
	}
	return uploads, cleanup, nil
}

// GetEnrollmentJob handles JWT GET /employees/:id/face-enrollment/:jobId
func (h *FaceHandler) GetEnrollmentJob(c echo.Context) error {
	job, samples, err := h.svc.GetJob(c.Request().Context(), c.Param("id"), c.Param("jobId"))
	if err != nil {
		if strings.Contains(err.Error(), "another employee") {
			return echo.NewHTTPError(http.StatusForbidden, err.Error())
		}
		if strings.Contains(err.Error(), "not found") {
			return echo.NewHTTPError(http.StatusNotFound, err.Error())
		}
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if job == nil {
		return echo.NewHTTPError(http.StatusNotFound, "job not found")
	}
	return c.JSON(http.StatusOK, map[string]any{
		"job":     job,
		"samples": samples,
	})
}

// RetryEnrollment handles JWT multipart POST /employees/:id/face-enrollment/:jobId/retry
func (h *FaceHandler) RetryEnrollment(c echo.Context) error {
	actor, _ := c.Get("user_id").(string)
	uploads, closer, err := h.parseMultipartUploads(c)
	if closer != nil {
		defer closer()
	}
	if err != nil {
		return err
	}
	result, err := h.svc.RetryEnrollment(c.Request().Context(), c.Param("id"), c.Param("jobId"), actor, uploads)
	if err != nil {
		msg := err.Error()
		switch {
		case strings.Contains(msg, "not found"):
			return echo.NewHTTPError(http.StatusNotFound, msg)
		case strings.Contains(msg, "another employee"):
			return echo.NewHTTPError(http.StatusForbidden, msg)
		case strings.Contains(msg, "too large"):
			return echo.NewHTTPError(http.StatusRequestEntityTooLarge, msg)
		case strings.Contains(msg, "still being processed") || strings.Contains(msg, "cannot accept") ||
			strings.Contains(msg, "only rejected") || strings.Contains(msg, "invalid") ||
			strings.Contains(msg, "unsupported") || strings.Contains(msg, "required") ||
			strings.Contains(msg, "no existing") || strings.Contains(msg, "too many"):
			return echo.NewHTTPError(http.StatusUnprocessableEntity, msg)
		default:
			return echo.NewHTTPError(http.StatusInternalServerError, msg)
		}
	}
	return c.JSON(http.StatusAccepted, result)
}

// ListProfileSamples handles JWT GET /employees/:id/face-profile/samples
func (h *FaceHandler) ListProfileSamples(c echo.Context) error {
	result, err := h.svc.ListProfileSamples(c.Request().Context(), c.Param("id"))
	if err != nil {
		if strings.Contains(err.Error(), "not found") {
			return echo.NewHTTPError(http.StatusNotFound, err.Error())
		}
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	return c.JSON(http.StatusOK, result)
}

// GetFaceProfile handles JWT GET /employees/:id/face-profile
func (h *FaceHandler) GetFaceProfile(c echo.Context) error {
	profile, err := h.svc.GetProfileForAdmin(c.Request().Context(), c.Param("id"))
	if err != nil {
		if strings.Contains(err.Error(), "not found") {
			return echo.NewHTTPError(http.StatusNotFound, err.Error())
		}
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if profile == nil {
		return echo.NewHTTPError(http.StatusNotFound, "face profile not found")
	}
	return c.JSON(http.StatusOK, profile)
}

// DeleteFaceProfile handles JWT DELETE /employees/:id/face-profile
func (h *FaceHandler) DeleteFaceProfile(c echo.Context) error {
	actor, _ := c.Get("user_id").(string)
	if err := h.svc.RevokeProfile(c.Request().Context(), c.Param("id"), actor); err != nil {
		if strings.Contains(err.Error(), "not found") {
			return echo.NewHTTPError(http.StatusNotFound, err.Error())
		}
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	return c.JSON(http.StatusOK, map[string]any{"revoked": true})
}

// GetEnrollmentSampleImage handles JWT GET /employees/:id/face-enrollment/samples/:sampleId/image
func (h *FaceHandler) GetEnrollmentSampleImage(c echo.Context) error {
	emp, err := h.svc.ResolveEmployeeID(c.Request().Context(), c.Param("id"))
	if err != nil || emp == nil {
		return echo.NewHTTPError(http.StatusNotFound, "employee not found")
	}
	sample, abs, err := h.svc.GetSampleFile(c.Request().Context(), c.Param("sampleId"))
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if sample == nil {
		return echo.NewHTTPError(http.StatusNotFound, "sample not found")
	}
	if sample.EmployeeID != emp.EmployeeID {
		return echo.NewHTTPError(http.StatusForbidden, "sample belongs to another employee")
	}
	f, err := os.Open(abs)
	if err != nil {
		return echo.NewHTTPError(http.StatusNotFound, "file missing")
	}
	defer f.Close()
	st, err := f.Stat()
	if err != nil {
		return echo.NewHTTPError(http.StatusNotFound, "file missing")
	}
	ct := sample.ContentType
	if ct == "" {
		ct = "image/jpeg"
	}
	c.Response().Header().Set(echo.HeaderContentType, ct)
	http.ServeContent(c.Response(), c.Request(), st.Name(), st.ModTime(), f)
	return nil
}

// GetMyFaceProfile handles DeviceAuth GET /face-profiles/me
func (h *FaceHandler) GetMyFaceProfile(c echo.Context) error {
	empID, _ := c.Get("employee_id").(string)
	profile, err := h.svc.GetMyProfile(c.Request().Context(), empID)
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if profile == nil {
		return echo.NewHTTPError(http.StatusNotFound, "no active face profile")
	}
	return c.JSON(http.StatusOK, profile)
}

// SyncAck handles DeviceAuth POST /face-profiles/sync-ack
func (h *FaceHandler) SyncAck(c echo.Context) error {
	empID, _ := c.Get("employee_id").(string)
	var body struct {
		ProfileVersion int    `json:"profileVersion"`
		ModelVersion   string `json:"modelVersion"`
		ClientDeviceID string `json:"clientDeviceId"`
	}
	_ = c.Bind(&body)
	_ = h.svc.InsertAudit(c.Request().Context(), empID, body.ClientDeviceID, "package_sync_ack", map[string]any{
		"profileVersion": body.ProfileVersion,
		"modelVersion":   body.ModelVersion,
	})
	return c.JSON(http.StatusOK, map[string]any{"ok": true})
}

// ModelManifest handles DeviceAuth GET /face-models/manifest
func (h *FaceHandler) ModelManifest(c echo.Context) error {
	list, err := h.svc.ModelManifest(c.Request().Context())
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	return c.JSON(http.StatusOK, map[string]any{
		"models":             list,
		"preprocessVersion":  "face-preprocess-v1",
		"packagesAvailable":  false,
		"licenseGatePending": true,
	})
}

// WorkerClaimJob POST /internal/face-worker/claim (optional recovery poll; push mode preferred)
func (h *FaceHandler) WorkerClaimJob(c echo.Context) error {
	if !h.authorizeWorker(c) {
		return echo.NewHTTPError(http.StatusUnauthorized, "worker auth required")
	}
	workerID := strings.TrimSpace(c.Request().Header.Get("X-Face-Worker-Id"))
	if workerID == "" {
		workerID = "faceworker"
	}
	job, samples, err := h.svc.ClaimNextJob(c.Request().Context(), workerID)
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if job == nil {
		return c.NoContent(http.StatusNoContent)
	}
	return c.JSON(http.StatusOK, map[string]any{
		"job":     job,
		"samples": samples,
	})
}

// WorkerLeaseJob POST /internal/face-worker/jobs/:jobId/lease — push-mode lease of a specific job.
func (h *FaceHandler) WorkerLeaseJob(c echo.Context) error {
	if !h.authorizeWorker(c) {
		return echo.NewHTTPError(http.StatusUnauthorized, "worker auth required")
	}
	workerID := strings.TrimSpace(c.Request().Header.Get("X-Face-Worker-Id"))
	if workerID == "" {
		workerID = "faceworker"
	}
	job, samples, err := h.svc.ClaimJobByID(c.Request().Context(), c.Param("jobId"), workerID)
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if job == nil {
		return echo.NewHTTPError(http.StatusConflict, "job not claimable (missing, not pending, or locked)")
	}
	return c.JSON(http.StatusOK, map[string]any{
		"job":     job,
		"samples": samples,
	})
}

// WorkerFailJob POST /internal/face-worker/jobs/:jobId/fail
func (h *FaceHandler) WorkerFailJob(c echo.Context) error {
	if !h.authorizeWorker(c) {
		return echo.NewHTTPError(http.StatusUnauthorized, "worker auth required")
	}
	var body struct {
		Code    string `json:"code"`
		Message string `json:"message"`
	}
	_ = c.Bind(&body)
	if body.Code == "" {
		body.Code = "worker_error"
	}
	if err := h.svc.MarkJobFailed(c.Request().Context(), c.Param("jobId"), body.Code, body.Message); err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	return c.JSON(http.StatusOK, map[string]any{"ok": true})
}

// WorkerSampleResult POST /internal/face-worker/jobs/:jobId/samples/:sampleId/result
func (h *FaceHandler) WorkerSampleResult(c echo.Context) error {
	if !h.authorizeWorker(c) {
		return echo.NewHTTPError(http.StatusUnauthorized, "worker auth required")
	}
	var body struct {
		Status       string `json:"status"`
		RejectReason string `json:"rejectReason"`
		FaceCount    int    `json:"faceCount"`
	}
	if err := c.Bind(&body); err != nil {
		return echo.NewHTTPError(http.StatusBadRequest, "invalid body")
	}
	sample, err := h.svc.ReportSampleResult(
		c.Request().Context(), c.Param("jobId"), c.Param("sampleId"),
		body.Status, body.RejectReason, body.FaceCount,
	)
	if err != nil {
		msg := err.Error()
		if strings.Contains(msg, "not found") {
			return echo.NewHTTPError(http.StatusNotFound, msg)
		}
		if strings.Contains(msg, "invalid") || strings.Contains(msg, "belong") {
			return echo.NewHTTPError(http.StatusUnprocessableEntity, msg)
		}
		return echo.NewHTTPError(http.StatusInternalServerError, msg)
	}
	return c.JSON(http.StatusOK, sample)
}

// WorkerCompleteJob POST /internal/face-worker/jobs/:jobId/complete
func (h *FaceHandler) WorkerCompleteJob(c echo.Context) error {
	if !h.authorizeWorker(c) {
		return echo.NewHTTPError(http.StatusUnauthorized, "worker auth required")
	}
	var body struct {
		Status  string `json:"status"`
		Code    string `json:"code"`
		Message string `json:"message"`
	}
	_ = c.Bind(&body)
	if body.Status == "" {
		body.Status = "failed"
	}
	if err := h.svc.CompleteJobFinalizes(c.Request().Context(), c.Param("jobId"), body.Status, body.Code, body.Message); err != nil {
		return echo.NewHTTPError(http.StatusUnprocessableEntity, err.Error())
	}
	return c.JSON(http.StatusOK, map[string]any{"ok": true})
}

// WorkerSampleImage GET /internal/face-worker/samples/:sampleId/image
func (h *FaceHandler) WorkerSampleImage(c echo.Context) error {
	if !h.authorizeWorker(c) {
		return echo.NewHTTPError(http.StatusUnauthorized, "worker auth required")
	}
	sample, abs, err := h.svc.GetSampleFile(c.Request().Context(), c.Param("sampleId"))
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if sample == nil {
		return echo.NewHTTPError(http.StatusNotFound, "sample not found")
	}
	f, err := os.Open(abs)
	if err != nil {
		return echo.NewHTTPError(http.StatusNotFound, "file missing")
	}
	defer f.Close()
	return c.Stream(http.StatusOK, sample.ContentType, f)
}

func (h *FaceHandler) authorizeWorker(c echo.Context) bool {
	if h.workerAPIKey == "" {
		return false
	}
	got := strings.TrimSpace(c.Request().Header.Get("X-Face-Worker-Key"))
	return got != "" && got == h.workerAPIKey
}
