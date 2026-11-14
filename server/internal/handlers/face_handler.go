package handlers

import (
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/google/uuid"
	"github.com/labstack/echo/v4"
)

type FaceHandler struct {
	svc                   *services.FaceService
	workerAPIKey          string
	modelsDir             string
	buffaloDetPackagePath string
	buffaloRecPackagePath string
}

func NewFaceHandler(svc *services.FaceService, workerAPIKey, modelsDir, buffaloDetPackagePath, buffaloRecPackagePath string) *FaceHandler {
	return &FaceHandler{
		svc:                   svc,
		workerAPIKey:          strings.TrimSpace(workerAPIKey),
		modelsDir:             strings.TrimSpace(modelsDir),
		buffaloDetPackagePath: strings.TrimSpace(buffaloDetPackagePath),
		buffaloRecPackagePath: strings.TrimSpace(buffaloRecPackagePath),
	}
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

// VerifyEvent handles DeviceAuth POST /face-profiles/verify-event
// Soft warning sink from the Windows --face-verify subprocess (does not gate tracking).
// Prefer batch SyncVerifyEvents for the Face Verify module; this remains for compatibility.
func (h *FaceHandler) VerifyEvent(c echo.Context) error {
	empID, _ := c.Get("employee_id").(string)
	var body struct {
		Result         string   `json:"result"`
		Score          *float64 `json:"score"`
		Threshold      *float64 `json:"threshold"`
		ClientDeviceID string   `json:"clientDeviceId"`
		Detail         string   `json:"detail"`
		ClientEventID  string   `json:"clientEventId"`
		Mode           string   `json:"mode"`
		Trigger        string   `json:"trigger"`
		PoseID         string   `json:"poseId"`
		CreatedAt      string   `json:"createdAt"`
	}
	if err := c.Bind(&body); err != nil {
		return echo.NewHTTPError(http.StatusBadRequest, "invalid body")
	}
	result := strings.ToLower(strings.TrimSpace(body.Result))
	if result == "" {
		return echo.NewHTTPError(http.StatusBadRequest, "result is required")
	}
	cid := strings.TrimSpace(body.ClientEventID)
	if cid == "" {
		cid = uuid.NewString()
	}
	createdAt := strings.TrimSpace(body.CreatedAt)
	if createdAt == "" {
		createdAt = time.Now().UTC().Format(time.RFC3339Nano)
	}
	resp, err := h.svc.SyncVerifyEvents(c.Request().Context(), empID, []services.FaceVerifySyncItem{{
		ClientEventID: cid,
		MachineID:     body.ClientDeviceID,
		Mode:          body.Mode,
		Trigger:       body.Trigger,
		PoseID:        body.PoseID,
		Result:        result,
		Score:         body.Score,
		Threshold:     body.Threshold,
		Detail:        body.Detail,
		CreatedAt:     createdAt,
	}})
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	return c.JSON(http.StatusOK, map[string]any{"ok": true, "acceptedIds": resp.AcceptedIds})
}

// SyncVerifyEvents handles DeviceAuth POST /face-profiles/verify-events/sync
func (h *FaceHandler) SyncVerifyEvents(c echo.Context) error {
	empID, _ := c.Get("employee_id").(string)
	var body struct {
		Events []services.FaceVerifySyncItem `json:"events"`
	}
	if err := c.Bind(&body); err != nil {
		return echo.NewHTTPError(http.StatusBadRequest, "invalid body")
	}
	resp, err := h.svc.SyncVerifyEvents(c.Request().Context(), empID, body.Events)
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	return c.JSON(http.StatusOK, resp)
}

// ListMySamples handles DeviceAuth GET /face-profiles/me/samples
func (h *FaceHandler) ListMySamples(c echo.Context) error {
	empID, _ := c.Get("employee_id").(string)
	items, err := h.svc.ListMySamplePackage(c.Request().Context(), empID)
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if items == nil {
		items = []models.FaceSamplePackageItem{}
	}
	return c.JSON(http.StatusOK, map[string]any{"samples": items})
}

// GetMySampleImage handles DeviceAuth GET /face-profiles/me/samples/:sampleId/image
func (h *FaceHandler) GetMySampleImage(c echo.Context) error {
	empID, _ := c.Get("employee_id").(string)
	sample, abs, err := h.svc.GetMySampleFile(c.Request().Context(), empID, c.Param("sampleId"))
	if err != nil {
		if strings.Contains(err.Error(), "forbidden") {
			return echo.NewHTTPError(http.StatusForbidden, "sample belongs to another employee")
		}
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

// ListVerifyEvents handles JWT GET /face-verify/events
func (h *FaceHandler) ListVerifyEvents(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	f := repository.FaceVerifyListFilter{
		EmployeeID: strings.TrimSpace(c.QueryParam("employeeId")),
		Result:     strings.TrimSpace(c.QueryParam("result")),
		Mode:       strings.TrimSpace(c.QueryParam("mode")),
		Q:          strings.TrimSpace(c.QueryParam("q")),
		Page:       page,
		PerPage:    perPage,
	}
	if from := strings.TrimSpace(c.QueryParam("dateFrom")); from != "" {
		if t, err := time.Parse("2006-01-02", from); err == nil {
			f.DateFrom = &t
		}
	}
	if to := strings.TrimSpace(c.QueryParam("dateTo")); to != "" {
		if t, err := time.Parse("2006-01-02", to); err == nil {
			end := t.Add(24 * time.Hour)
			f.DateTo = &end
		}
	}
	events, total, err := h.svc.ListVerifyEvents(c.Request().Context(), f)
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if page < 1 {
		page = 1
	}
	if perPage < 1 {
		perPage = 30
	}
	return c.JSON(http.StatusOK, map[string]any{
		"events":  events,
		"total":   total,
		"page":    page,
		"perPage": perPage,
	})
}

// GetVerifyEvent handles JWT GET /face-verify/events/:id
func (h *FaceHandler) GetVerifyEvent(c echo.Context) error {
	ev, err := h.svc.GetVerifyEvent(c.Request().Context(), c.Param("id"))
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if ev == nil {
		return echo.NewHTTPError(http.StatusNotFound, "event not found")
	}
	return c.JSON(http.StatusOK, ev)
}

// ModelManifest handles DeviceAuth GET /face-models/manifest
func (h *FaceHandler) ModelManifest(c echo.Context) error {
	list, err := h.svc.ModelManifest(c.Request().Context())
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	detReady := h.resolveBuffaloDetPackage() != ""
	recReady := h.resolveBuffaloRecPackage() != ""
	return c.JSON(http.StatusOK, map[string]any{
		"models":               list,
		"preprocessVersion":    "face-preprocess-v2-buffalo-l",
		"packagesAvailable":    detReady && recReady,
		"buffaloDetAvailable":  detReady,
		"buffaloRecAvailable":  recReady,
		"licenseGatePending":   !recReady,
		"buffaloDetPackageId":  "buffalo-l-det",
		"buffaloRecPackageId":  "buffalo-l-rec",
		"modelId":              "buffalo-l-w600k-r50",
		"modelsDir":            h.modelsDir,
	})
}

// DownloadModelPackage handles DeviceAuth GET /face-models/packages/:packageId
// Serves buffalo_l ONNX from face_worker/models. Clients cache under LocalAppData.
func (h *FaceHandler) DownloadModelPackage(c echo.Context) error {
	id := strings.TrimSpace(c.Param("packageId"))
	var path, filename string
	switch strings.ToLower(id) {
	case "buffalo-l-det", "det_10g", "det_10g.onnx", "scrfd":
		path = h.resolveBuffaloDetPackage()
		filename = "det_10g.onnx"
		if path == "" {
			return echo.NewHTTPError(http.StatusNotFound, "buffalo_l det_10g missing on API (face_worker/models or FACE_BUFFALO_DET_ONNX)")
		}
	case "buffalo-l-rec", "w600k_r50", "w600k_r50.onnx", "buffalo-l":
		path = h.resolveBuffaloRecPackage()
		filename = "w600k_r50.onnx"
		if path == "" {
			return echo.NewHTTPError(http.StatusNotFound, "buffalo_l w600k_r50 missing on API (FACE_BUFFALO_REC_ONNX)")
		}
	default:
		return echo.NewHTTPError(http.StatusNotFound, "unknown face model package")
	}
	f, err := os.Open(path)
	if err != nil {
		return echo.NewHTTPError(http.StatusNotFound, "face model file missing on API host")
	}
	defer f.Close()
	st, err := f.Stat()
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	c.Response().Header().Set(echo.HeaderContentType, "application/octet-stream")
	c.Response().Header().Set(echo.HeaderContentDisposition, `attachment; filename="`+filename+`"`)
	http.ServeContent(c.Response(), c.Request(), st.Name(), st.ModTime(), f)
	return nil
}

func (h *FaceHandler) resolvePackageFile(explicit string, minSize int64, names ...string) string {
	var candidates []string
	add := func(p string) {
		p = strings.TrimSpace(p)
		if p == "" {
			return
		}
		candidates = append(candidates, p)
	}
	add(explicit)
	if explicit != "" && !filepath.IsAbs(explicit) && !strings.HasPrefix(explicit, "face_worker"+string(filepath.Separator)) {
		add(filepath.Join("face_worker", explicit))
	}
	dir := strings.TrimSpace(h.modelsDir)
	if dir == "" {
		dir = filepath.Join("face_worker", "models")
	}
	for _, name := range names {
		add(filepath.Join(dir, name))
		add(filepath.Join("face_worker", "models", name))
	}
	if wd, err := os.Getwd(); err == nil {
		n := len(candidates)
		for i := 0; i < n; i++ {
			if !filepath.IsAbs(candidates[i]) {
				add(filepath.Join(wd, candidates[i]))
			}
		}
	}
	for _, p := range candidates {
		if st, err := os.Stat(p); err == nil && !st.IsDir() && st.Size() > minSize {
			return p
		}
	}
	return ""
}

func (h *FaceHandler) resolveBuffaloDetPackage() string {
	return h.resolvePackageFile(h.buffaloDetPackagePath, 100_000, "det_10g.onnx")
}

func (h *FaceHandler) resolveBuffaloRecPackage() string {
	return h.resolvePackageFile(h.buffaloRecPackagePath, 1_000_000, "w600k_r50.onnx")
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

// WorkerSubmitEmbeddings POST /internal/face-worker/jobs/:jobId/embeddings
func (h *FaceHandler) WorkerSubmitEmbeddings(c echo.Context) error {
	if !h.authorizeWorker(c) {
		return echo.NewHTTPError(http.StatusUnauthorized, "worker auth required")
	}
	var body services.SubmitJobEmbeddingsRequest
	if err := c.Bind(&body); err != nil {
		return echo.NewHTTPError(http.StatusBadRequest, "invalid body")
	}
	if err := h.svc.SubmitJobEmbeddings(c.Request().Context(), c.Param("jobId"), body); err != nil {
		msg := err.Error()
		if strings.Contains(msg, "not found") {
			return echo.NewHTTPError(http.StatusNotFound, msg)
		}
		return echo.NewHTTPError(http.StatusUnprocessableEntity, msg)
	}
	return c.JSON(http.StatusOK, map[string]any{"ok": true})
}

// GetMyEmbeddings handles DeviceAuth GET /face-profiles/me/embeddings
func (h *FaceHandler) GetMyEmbeddings(c echo.Context) error {
	empID, _ := c.Get("employee_id").(string)
	profile, templates, err := h.svc.ListMyEmbeddingTemplates(c.Request().Context(), empID)
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	if profile == nil {
		return c.JSON(http.StatusOK, map[string]any{
			"profile":    nil,
			"embeddings": []any{},
			"status":     "none",
		})
	}
	return c.JSON(http.StatusOK, map[string]any{
		"profile":            profile,
		"embeddings":         templates,
		"status":             profile.Status,
		"preprocessVersion":  profile.PreprocessVersion,
		"normalization":      "l2",
		"packagesAvailable":  h.resolveBuffaloDetPackage() != "" && h.resolveBuffaloRecPackage() != "",
	})
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
