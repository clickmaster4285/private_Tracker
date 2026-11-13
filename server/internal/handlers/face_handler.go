package handlers

import (
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

type FaceHandler struct {
	svc                *services.FaceService
	workerAPIKey       string
	modelsDir          string
	adaFacePackagePath string
	yuNetPackagePath   string
}

func NewFaceHandler(svc *services.FaceService, workerAPIKey, modelsDir, adaFacePackagePath, yuNetPackagePath string) *FaceHandler {
	return &FaceHandler{
		svc:                svc,
		workerAPIKey:       strings.TrimSpace(workerAPIKey),
		modelsDir:          strings.TrimSpace(modelsDir),
		adaFacePackagePath: strings.TrimSpace(adaFacePackagePath),
		yuNetPackagePath:   strings.TrimSpace(yuNetPackagePath),
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
func (h *FaceHandler) VerifyEvent(c echo.Context) error {
	empID, _ := c.Get("employee_id").(string)
	var body struct {
		Result         string   `json:"result"`
		Score          *float64 `json:"score"`
		Threshold      *float64 `json:"threshold"`
		ClientDeviceID string   `json:"clientDeviceId"`
		Detail         string   `json:"detail"`
	}
	if err := c.Bind(&body); err != nil {
		return echo.NewHTTPError(http.StatusBadRequest, "invalid body")
	}
	result := strings.ToLower(strings.TrimSpace(body.Result))
	action := "face_verify_failed"
	switch result {
	case "verified", "ok", "success":
		action = "face_verify_ok"
		result = "verified"
	case "no_profile", "none":
		action = "face_verify_no_profile"
		result = "no_profile"
	case "model_error":
		action = "face_verify_model_error"
		result = "model_error"
	case "camera_error":
		action = "face_verify_camera_error"
		result = "camera_error"
	case "failed", "mismatch", "not_verified":
		action = "face_verify_failed"
		result = "failed"
	default:
		if result == "" {
			return echo.NewHTTPError(http.StatusBadRequest, "result is required")
		}
		action = "face_verify_failed"
	}
	meta := map[string]any{
		"result": result,
	}
	if body.Score != nil {
		meta["score"] = *body.Score
	}
	if body.Threshold != nil {
		meta["threshold"] = *body.Threshold
	}
	if d := strings.TrimSpace(body.Detail); d != "" {
		meta["detail"] = d
	}
	_ = h.svc.InsertAudit(c.Request().Context(), empID, body.ClientDeviceID, action, meta)
	return c.JSON(http.StatusOK, map[string]any{"ok": true})
}

// ModelManifest handles DeviceAuth GET /face-models/manifest
func (h *FaceHandler) ModelManifest(c echo.Context) error {
	list, err := h.svc.ModelManifest(c.Request().Context())
	if err != nil {
		return echo.NewHTTPError(http.StatusInternalServerError, err.Error())
	}
	adaReady := h.resolveAdaFacePackage() != ""
	yunetReady := h.resolveYuNetPackage() != ""
	return c.JSON(http.StatusOK, map[string]any{
		"models":             list,
		"preprocessVersion":  "face-preprocess-v1",
		"packagesAvailable":  adaReady && yunetReady,
		"yunetAvailable":     yunetReady,
		"adafaceAvailable":   adaReady,
		"licenseGatePending": !adaReady,
		"yunetPackageId":     "yunet",
		"adafacePackageId":   "adaface-ir50",
		"modelsDir":          h.modelsDir,
	})
}

// DownloadModelPackage handles DeviceAuth GET /face-models/packages/:packageId
// Serves ONNX from face_worker/models (YuNet + AdaFace). Clients cache under LocalAppData.
func (h *FaceHandler) DownloadModelPackage(c echo.Context) error {
	id := strings.TrimSpace(c.Param("packageId"))
	var path, filename string
	switch strings.ToLower(id) {
	case "yunet", "face_detection_yunet_2023mar", "face_detection_yunet_2023mar.onnx":
		path = h.resolveYuNetPackage()
		filename = "face_detection_yunet_2023mar.onnx"
		if path == "" {
			return echo.NewHTTPError(http.StatusNotFound, "YuNet ONNX missing on API (face_worker/models or FACE_YUNET_ONNX)")
		}
	case "adaface-ir50", "adaface_ir50_webface4m.onnx", "adaface":
		path = h.resolveAdaFacePackage()
		filename = "adaface_ir50_webface4m.onnx"
		if path == "" {
			return echo.NewHTTPError(http.StatusNotFound, "AdaFace ONNX not configured on API (FACE_ADAFACE_ONNX)")
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

func (h *FaceHandler) resolveAdaFacePackage() string {
	return h.resolvePackageFile(h.adaFacePackagePath, 1_000_000, "adaface_ir50_webface4m.onnx")
}

func (h *FaceHandler) resolveYuNetPackage() string {
	return h.resolvePackageFile(h.yuNetPackagePath, 100_000, "face_detection_yunet_2023mar.onnx")
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
		"packagesAvailable":  h.resolveAdaFacePackage() != "",
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
