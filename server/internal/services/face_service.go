package services

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/google/uuid"
)

var allowedFacePoses = map[string]struct{}{
	"front": {}, "left_slight": {}, "left": {}, "right_slight": {}, "right": {},
	"up": {}, "down": {}, "front_return": {}, "neutral_extra": {},
}

var allowedFaceContentTypes = map[string]string{
	"image/jpeg": ".jpg",
	"image/jpg":  ".jpg",
	"image/png":  ".png",
	"image/webp": ".webp",
}

// FaceService owns enrollment uploads and biometric profile reads.
type FaceService struct {
	repo         *repository.FaceRepo
	empRepo      *repository.EmployeeRepo
	rootDir      string
	maxBytes     int64
	maxFiles     int
	workerURL    string
	workerAPIKey string
	httpClient   *http.Client
}

func NewFaceService(repo *repository.FaceRepo, empRepo *repository.EmployeeRepo, rootDir string, maxBytes int64, maxFiles int) *FaceService {
	if strings.TrimSpace(rootDir) == "" {
		rootDir = "media/faces"
	}
	if maxBytes <= 0 {
		maxBytes = 3 << 20 // 3 MiB per image
	}
	if maxFiles <= 0 {
		maxFiles = 16
	}
	return &FaceService{
		repo: repo, empRepo: empRepo, rootDir: rootDir, maxBytes: maxBytes, maxFiles: maxFiles,
		httpClient: &http.Client{Timeout: 5 * time.Second},
	}
}

// SetWorkerPush configures async push to the Python face_worker HTTP server.
func (s *FaceService) SetWorkerPush(workerURL, apiKey string) {
	s.workerURL = strings.TrimRight(strings.TrimSpace(workerURL), "/")
	s.workerAPIKey = strings.TrimSpace(apiKey)
}

func (s *FaceService) RootDir() string  { return s.rootDir }
func (s *FaceService) MaxBytes() int64  { return s.maxBytes }
func (s *FaceService) MaxFiles() int    { return s.maxFiles }

// ResolveEmployeeID maps route :id (UUID or business employee_id) to an employee row.
// Non-UUID codes (e.g. WA-44) must not be queried against employees.id (uuid column) —
// Postgres "invalid input syntax for type uuid" used to abort before the business-id lookup,
// which broke sample image URLs that embed employee_id.
func (s *FaceService) ResolveEmployeeID(ctx context.Context, idOrCode string) (*models.Employee, error) {
	idOrCode = strings.TrimSpace(idOrCode)
	if idOrCode == "" {
		return nil, fmt.Errorf("employee id required")
	}
	if _, err := uuid.Parse(idOrCode); err == nil {
		emp, err := s.empRepo.GetByID(ctx, idOrCode)
		if err != nil {
			return nil, err
		}
		if emp != nil {
			return emp, nil
		}
	}
	return s.empRepo.GetByEmployeeID(ctx, idOrCode)
}

type FaceSampleUpload struct {
	PoseID         string
	ClientSampleID string
	ContentType    string
	Width          int
	Height         int
	QualityScore   float64
	Reader         io.Reader
	SizeHint       int64
}

type CreateEnrollmentResult struct {
	Job     *models.FaceEnrollmentJob     `json:"job"`
	Profile *models.EmployeeFaceProfile   `json:"profile"`
	Samples []models.FaceEnrollmentSample `json:"samples"`
}

func (s *FaceService) CreateEnrollment(ctx context.Context, routeEmployeeID, actorUserID string, uploads []FaceSampleUpload) (*CreateEnrollmentResult, error) {
	emp, err := s.ResolveEmployeeID(ctx, routeEmployeeID)
	if err != nil {
		return nil, err
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(uploads) == 0 {
		return nil, fmt.Errorf("at least one image required")
	}
	if len(uploads) > s.maxFiles {
		return nil, fmt.Errorf("too many images (max %d)", s.maxFiles)
	}

	for _, u := range uploads {
		pose := strings.TrimSpace(u.PoseID)
		if _, ok := allowedFacePoses[pose]; !ok {
			return nil, fmt.Errorf("invalid poseId %q", u.PoseID)
		}
		ct := normalizeContentType(u.ContentType)
		if _, ok := allowedFaceContentTypes[ct]; !ok {
			return nil, fmt.Errorf("unsupported content type %q", u.ContentType)
		}
		if u.SizeHint > s.maxBytes {
			return nil, fmt.Errorf("file too large (max %d bytes)", s.maxBytes)
		}
	}

	job, err := s.repo.CreateJob(ctx, emp.EmployeeID, actorUserID)
	if err != nil {
		return nil, err
	}

	maxVer, err := s.repo.MaxProfileVersion(ctx, emp.EmployeeID)
	if err != nil {
		return nil, err
	}
	profile, err := s.repo.CreatePendingProfile(ctx, emp.EmployeeID, actorUserID, maxVer+1)
	if err != nil {
		return nil, err
	}
	if err := s.repo.AttachJobProfile(ctx, job.ID, profile.ID); err != nil {
		return nil, err
	}
	job.ProfileID = &profile.ID

	var saved []models.FaceEnrollmentSample
	for _, u := range uploads {
		sample, err := s.writeSample(ctx, emp.EmployeeID, job.ID, u)
		if err != nil {
			_ = s.repo.FailJob(ctx, job.ID, "sample_write_failed", err.Error())
			return nil, err
		}
		saved = append(saved, *sample)
	}

	_ = s.repo.InsertAudit(ctx, emp.EmployeeID, actorUserID, "enroll_submitted", map[string]any{
		"jobId":       job.ID,
		"profileId":   profile.ID,
		"sampleCount": len(saved),
	})

	job.SampleCount = len(saved)
	job.Status = "pending"
	s.enrichSampleURLs(emp.EmployeeID, saved)
	s.NotifyWorker(job.ID)
	return &CreateEnrollmentResult{Job: job, Profile: profile, Samples: saved}, nil
}

func (s *FaceService) writeSample(ctx context.Context, employeeID, jobID string, u FaceSampleUpload) (*models.FaceEnrollmentSample, error) {
	ct := normalizeContentType(u.ContentType)
	ext := allowedFaceContentTypes[ct]
	sampleID := uuid.NewString()
	safeEmp := sanitizeSegment(employeeID)
	rel := filepath.ToSlash(filepath.Join(safeEmp, jobID, sampleID+ext))
	absDir := filepath.Join(s.rootDir, safeEmp, jobID)
	if err := os.MkdirAll(absDir, 0o750); err != nil {
		return nil, err
	}
	absPath := filepath.Join(absDir, sampleID+ext)

	f, err := os.OpenFile(absPath, os.O_CREATE|os.O_WRONLY|os.O_EXCL, 0o640)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	written, err := io.Copy(f, io.LimitReader(u.Reader, s.maxBytes+1))
	if err != nil {
		_ = os.Remove(absPath)
		return nil, err
	}
	if written > s.maxBytes {
		_ = os.Remove(absPath)
		return nil, fmt.Errorf("file too large (max %d bytes)", s.maxBytes)
	}

	sample := &models.FaceEnrollmentSample{
		ID:             sampleID,
		JobID:          jobID,
		EmployeeID:     employeeID,
		PoseID:         strings.TrimSpace(u.PoseID),
		RelPath:        rel,
		ContentType:    ct,
		ByteSize:       written,
		Width:          u.Width,
		Height:         u.Height,
		QualityScore:   u.QualityScore,
		ClientSampleID: strings.TrimSpace(u.ClientSampleID),
		CreatedAt:      time.Now().UTC(),
	}
	if err := s.repo.InsertSample(ctx, sample); err != nil {
		_ = os.Remove(absPath)
		return nil, err
	}
	return sample, nil
}

func (s *FaceService) GetJob(ctx context.Context, routeEmployeeID, jobID string) (*models.FaceEnrollmentJob, []models.FaceEnrollmentSample, error) {
	emp, err := s.ResolveEmployeeID(ctx, routeEmployeeID)
	if err != nil {
		return nil, nil, err
	}
	if emp == nil {
		return nil, nil, fmt.Errorf("employee not found")
	}
	job, err := s.repo.GetJob(ctx, jobID)
	if err != nil {
		return nil, nil, err
	}
	if job == nil {
		return nil, nil, nil
	}
	if job.EmployeeID != emp.EmployeeID {
		return nil, nil, fmt.Errorf("job belongs to another employee")
	}
	samples, err := s.repo.ListSamplesByJob(ctx, jobID)
	if err != nil {
		return nil, nil, err
	}
	s.enrichSampleURLs(emp.EmployeeID, samples)
	return job, samples, nil
}

type RetryEnrollmentResult struct {
	Job     *models.FaceEnrollmentJob     `json:"job"`
	Samples []models.FaceEnrollmentSample `json:"samples"`
}

type ProfileSamplesResult struct {
	Job     *models.FaceEnrollmentJob     `json:"job,omitempty"`
	Samples []models.FaceEnrollmentSample `json:"samples"`
}

// RetryEnrollment replaces rejected samples on an existing job and re-queues FaceWorker.
func (s *FaceService) RetryEnrollment(ctx context.Context, routeEmployeeID, jobID, actorUserID string, uploads []FaceSampleUpload) (*RetryEnrollmentResult, error) {
	emp, err := s.ResolveEmployeeID(ctx, routeEmployeeID)
	if err != nil {
		return nil, err
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(uploads) == 0 {
		return nil, fmt.Errorf("at least one replacement image required")
	}
	if len(uploads) > s.maxFiles {
		return nil, fmt.Errorf("too many images (max %d)", s.maxFiles)
	}

	job, err := s.repo.GetJob(ctx, jobID)
	if err != nil {
		return nil, err
	}
	if job == nil {
		return nil, fmt.Errorf("job not found")
	}
	if job.EmployeeID != emp.EmployeeID {
		return nil, fmt.Errorf("job belongs to another employee")
	}
	switch job.Status {
	case "needs_more_samples", "failed", "succeeded":
		// succeeded may still have deferred embeddings; allow retake of rejected leftovers only.
	case "pending", "processing":
		return nil, fmt.Errorf("job is still being processed — wait before retry")
	default:
		return nil, fmt.Errorf("job cannot accept retries in status %q", job.Status)
	}

	existing, err := s.repo.ListSamplesByJob(ctx, jobID)
	if err != nil {
		return nil, err
	}
	byPose := map[string]*models.FaceEnrollmentSample{}
	for i := range existing {
		sample := &existing[i]
		byPose[sample.PoseID] = sample
	}

	replaced := 0
	for _, u := range uploads {
		pose := strings.TrimSpace(u.PoseID)
		if _, ok := allowedFacePoses[pose]; !ok {
			return nil, fmt.Errorf("invalid poseId %q", u.PoseID)
		}
		ct := normalizeContentType(u.ContentType)
		if _, ok := allowedFaceContentTypes[ct]; !ok {
			return nil, fmt.Errorf("unsupported content type %q", u.ContentType)
		}
		if u.SizeHint > s.maxBytes {
			return nil, fmt.Errorf("file too large (max %d bytes)", s.maxBytes)
		}

		target := byPose[pose]
		if target == nil && strings.TrimSpace(u.ClientSampleID) != "" {
			// Optional: match by prior clientSampleId among rejected rows.
			for i := range existing {
				if existing[i].ClientSampleID == strings.TrimSpace(u.ClientSampleID) {
					target = &existing[i]
					break
				}
			}
		}
		if target == nil {
			return nil, fmt.Errorf("no existing sample for pose %q to replace", pose)
		}
		if target.ProcessStatus != "rejected" && target.ProcessStatus != "uploaded" {
			return nil, fmt.Errorf("pose %q is %s — only rejected images can be replaced", pose, target.ProcessStatus)
		}

		abs, err := s.AbsolutePath(target.RelPath)
		if err != nil {
			return nil, err
		}
		if err := os.MkdirAll(filepath.Dir(abs), 0o750); err != nil {
			return nil, err
		}
		tmp := abs + ".tmp"
		f, err := os.OpenFile(tmp, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o640)
		if err != nil {
			return nil, err
		}
		written, err := io.Copy(f, io.LimitReader(u.Reader, s.maxBytes+1))
		_ = f.Close()
		if err != nil {
			_ = os.Remove(tmp)
			return nil, err
		}
		if written > s.maxBytes {
			_ = os.Remove(tmp)
			return nil, fmt.Errorf("file too large (max %d bytes)", s.maxBytes)
		}
		if err := os.Rename(tmp, abs); err != nil {
			_ = os.Remove(tmp)
			return nil, err
		}

		target.ContentType = ct
		target.ByteSize = written
		target.Width = u.Width
		target.Height = u.Height
		target.QualityScore = u.QualityScore
		if cid := strings.TrimSpace(u.ClientSampleID); cid != "" {
			target.ClientSampleID = cid
		}
		if err := s.repo.UpdateSampleAfterReplace(ctx, target); err != nil {
			return nil, err
		}
		replaced++
	}

	if err := s.repo.ReopenJobPending(ctx, jobID); err != nil {
		return nil, err
	}
	_ = s.repo.InsertAudit(ctx, emp.EmployeeID, actorUserID, "enroll_retry", map[string]any{
		"jobId":           jobID,
		"replacedSamples": replaced,
	})

	job, err = s.repo.GetJob(ctx, jobID)
	if err != nil {
		return nil, err
	}
	samples, err := s.repo.ListSamplesByJob(ctx, jobID)
	if err != nil {
		return nil, err
	}
	s.enrichSampleURLs(emp.EmployeeID, samples)
	s.NotifyWorker(job.ID)
	return &RetryEnrollmentResult{Job: job, Samples: samples}, nil
}

// ListProfileSamples returns enrollment images for the gallery (latest job preferred).
func (s *FaceService) ListProfileSamples(ctx context.Context, routeEmployeeID string) (*ProfileSamplesResult, error) {
	emp, err := s.ResolveEmployeeID(ctx, routeEmployeeID)
	if err != nil {
		return nil, err
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	job, err := s.repo.GetLatestJob(ctx, emp.EmployeeID)
	if err != nil {
		return nil, err
	}
	var samples []models.FaceEnrollmentSample
	if job != nil {
		samples, err = s.repo.ListSamplesByJob(ctx, job.ID)
	} else {
		samples, err = s.repo.ListSamplesByEmployee(ctx, emp.EmployeeID)
	}
	if err != nil {
		return nil, err
	}
	s.enrichSampleURLs(emp.EmployeeID, samples)
	return &ProfileSamplesResult{Job: job, Samples: samples}, nil
}

func (s *FaceService) enrichSampleURLs(employeeID string, samples []models.FaceEnrollmentSample) {
	for i := range samples {
		samples[i].ImageURL = "/api/v1/employees/" + employeeID + "/face-enrollment/samples/" + samples[i].ID + "/image"
	}
}

func (s *FaceService) GetProfileForAdmin(ctx context.Context, routeEmployeeID string) (*models.EmployeeFaceProfile, error) {
	emp, err := s.ResolveEmployeeID(ctx, routeEmployeeID)
	if err != nil {
		return nil, err
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	active, err := s.repo.GetActiveProfile(ctx, emp.EmployeeID)
	if err != nil {
		return nil, err
	}
	if active != nil {
		return active, nil
	}
	return s.repo.GetLatestProfile(ctx, emp.EmployeeID)
}

func (s *FaceService) RevokeProfile(ctx context.Context, routeEmployeeID, actorUserID string) error {
	emp, err := s.ResolveEmployeeID(ctx, routeEmployeeID)
	if err != nil {
		return err
	}
	if emp == nil {
		return fmt.Errorf("employee not found")
	}
	if err := s.repo.RevokeActiveProfiles(ctx, emp.EmployeeID, actorUserID); err != nil {
		return err
	}
	return s.repo.InsertAudit(ctx, emp.EmployeeID, actorUserID, "revoked", map[string]any{})
}

func (s *FaceService) GetMyProfile(ctx context.Context, deviceEmployeeID string) (*models.EmployeeFaceProfile, error) {
	deviceEmployeeID = strings.TrimSpace(deviceEmployeeID)
	if deviceEmployeeID == "" {
		return nil, fmt.Errorf("missing employee")
	}
	return s.repo.GetActiveProfile(ctx, deviceEmployeeID)
}

func (s *FaceService) AbsolutePath(rel string) (string, error) {
	rel = filepath.ToSlash(strings.TrimSpace(rel))
	if rel == "" || strings.Contains(rel, "..") {
		return "", fmt.Errorf("invalid path")
	}
	abs := filepath.Join(s.rootDir, filepath.FromSlash(rel))
	rootAbs, err := filepath.Abs(s.rootDir)
	if err != nil {
		return "", err
	}
	abs, err = filepath.Abs(abs)
	if err != nil {
		return "", err
	}
	if !strings.HasPrefix(abs, rootAbs+string(os.PathSeparator)) && abs != rootAbs {
		return "", fmt.Errorf("path escapes media root")
	}
	return abs, nil
}

func (s *FaceService) GetSampleFile(ctx context.Context, sampleID string) (*models.FaceEnrollmentSample, string, error) {
	sample, err := s.repo.GetSample(ctx, sampleID)
	if err != nil || sample == nil {
		return sample, "", err
	}
	abs, err := s.AbsolutePath(sample.RelPath)
	return sample, abs, err
}

func (s *FaceService) ModelManifest(ctx context.Context) ([]models.FaceModelVersion, error) {
	return s.repo.ListModelManifest(ctx)
}

func (s *FaceService) ClaimNextJob(ctx context.Context, workerID string) (*models.FaceEnrollmentJob, []models.FaceEnrollmentSample, error) {
	job, err := s.repo.ClaimNextJob(ctx, workerID, 10*time.Minute)
	if err != nil || job == nil {
		return job, nil, err
	}
	samples, err := s.repo.ListSamplesByJob(ctx, job.ID)
	return job, samples, err
}

// ClaimJobByID leases one specific job (push mode — FaceWorker was notified for this id).
func (s *FaceService) ClaimJobByID(ctx context.Context, jobID, workerID string) (*models.FaceEnrollmentJob, []models.FaceEnrollmentSample, error) {
	job, err := s.repo.ClaimJobByID(ctx, jobID, workerID, 10*time.Minute)
	if err != nil || job == nil {
		return job, nil, err
	}
	samples, err := s.repo.ListSamplesByJob(ctx, job.ID)
	return job, samples, err
}

// NotifyWorker fire-and-forgets POST {FACE_WORKER_URL}/v1/process with {jobId}.
func (s *FaceService) NotifyWorker(jobID string) {
	jobID = strings.TrimSpace(jobID)
	if jobID == "" || s.workerURL == "" || s.workerAPIKey == "" || s.httpClient == nil {
		return
	}
	go func() {
		body, _ := json.Marshal(map[string]string{"jobId": jobID})
		req, err := http.NewRequest(http.MethodPost, s.workerURL+"/v1/process", bytes.NewReader(body))
		if err != nil {
			log.Printf("[face] notify worker build request: %v", err)
			return
		}
		req.Header.Set("Content-Type", "application/json")
		req.Header.Set("X-Face-Worker-Key", s.workerAPIKey)
		resp, err := s.httpClient.Do(req)
		if err != nil {
			log.Printf("[face] notify worker job=%s: %v (is face_worker listening on %s?)", jobID, err, s.workerURL)
			return
		}
		defer resp.Body.Close()
		if resp.StatusCode >= 300 {
			b, _ := io.ReadAll(io.LimitReader(resp.Body, 512))
			log.Printf("[face] notify worker job=%s HTTP %d: %s", jobID, resp.StatusCode, string(b))
			return
		}
		log.Printf("[face] notified worker job=%s → %s", jobID, s.workerURL)
	}()
}

func (s *FaceService) MarkJobFailed(ctx context.Context, jobID, code, message string) error {
	return s.repo.FailJob(ctx, jobID, code, message)
}

func (s *FaceService) InsertAudit(ctx context.Context, employeeID, actor, action string, meta map[string]any) error {
	return s.repo.InsertAudit(ctx, employeeID, actor, action, meta)
}

// ReportSampleResult records one-by-one FaceWorker outcome for a single enrollment image.
func (s *FaceService) ReportSampleResult(ctx context.Context, jobID, sampleID, status, rejectReason string, faceCount int) (*models.FaceEnrollmentSample, error) {
	sample, err := s.repo.GetSample(ctx, sampleID)
	if err != nil {
		return nil, err
	}
	if sample == nil {
		return nil, fmt.Errorf("sample not found")
	}
	if sample.JobID != jobID {
		return nil, fmt.Errorf("sample does not belong to job")
	}
	switch status {
	case "processing", "validated", "embedded", "rejected":
	default:
		return nil, fmt.Errorf("invalid process status")
	}
	if err := s.repo.UpdateSampleProcessResult(ctx, sampleID, status, rejectReason, faceCount); err != nil {
		return nil, err
	}
	_ = s.repo.BumpJobProgress(ctx, jobID)
	return s.repo.GetSample(ctx, sampleID)
}

// CompleteJobFinalizes finishes a claimed job after all samples were processed.
func (s *FaceService) CompleteJobFinalizes(ctx context.Context, jobID, status, code, message string) error {
	switch status {
	case "succeeded", "failed", "needs_more_samples":
	default:
		return fmt.Errorf("invalid job status")
	}
	return s.repo.CompleteJob(ctx, jobID, status, code, message)
}

func normalizeContentType(ct string) string {
	ct = strings.ToLower(strings.TrimSpace(ct))
	if i := strings.Index(ct, ";"); i >= 0 {
		ct = strings.TrimSpace(ct[:i])
	}
	if ct == "image/jpg" {
		return "image/jpeg"
	}
	return ct
}
