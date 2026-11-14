package services

import (
	"context"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/google/uuid"
)

var safePathSegment = regexp.MustCompile(`[^a-zA-Z0-9._-]`)

type ScreenshotService struct {
	repo    *repository.ScreenshotRepo
	rootDir string
	maxBytes int64
}

func NewScreenshotService(repo *repository.ScreenshotRepo, rootDir string, maxBytes int64) *ScreenshotService {
	if strings.TrimSpace(rootDir) == "" {
		rootDir = "media/screenshots"
	}
	if maxBytes <= 0 {
		maxBytes = 2 << 20 // 2 MiB
	}
	return &ScreenshotService{repo: repo, rootDir: rootDir, maxBytes: maxBytes}
}

func (s *ScreenshotService) RootDir() string { return s.rootDir }

func (s *ScreenshotService) MaxBytes() int64 { return s.maxBytes }

// SaveUploadResult is the outcome of an idempotent screenshot upload.
type SaveUploadResult struct {
	Row     *models.Screenshot
	// Replay is true when this screenshotId was already accepted (lost-200 retry).
	Replay bool
}

// SaveUpload writes WebP under {root}/{emp}/{yyyy}/{MM}/{dd}/{screenshotId}.webp.
// screenshotID is the client-generated UUID (idempotency key = primary key).
// A retry with the same id returns the existing row without creating a second copy.
func (s *ScreenshotService) SaveUpload(
	ctx context.Context,
	screenshotID, employeeID, machineID string,
	capturedAt time.Time,
	width, height int,
	contentType string,
	r io.Reader,
	sizeHint int64,
) (*SaveUploadResult, error) {
	employeeID = strings.TrimSpace(employeeID)
	if employeeID == "" {
		return nil, fmt.Errorf("employee_id required")
	}
	screenshotID = strings.TrimSpace(screenshotID)
	if _, err := uuid.Parse(screenshotID); err != nil {
		return nil, fmt.Errorf("screenshotId must be a UUID")
	}

	// Fast path: already accepted — discard body, no second file.
	if existing, err := s.repo.GetByID(ctx, screenshotID); err != nil {
		return nil, err
	} else if existing != nil {
		if existing.EmployeeID != employeeID {
			return nil, fmt.Errorf("screenshotId belongs to another employee")
		}
		return &SaveUploadResult{Row: existing, Replay: true}, nil
	}

	if capturedAt.IsZero() {
		capturedAt = time.Now().UTC()
	}
	capturedAt = capturedAt.UTC()
	if contentType == "" {
		contentType = "image/webp"
	}
	if !strings.Contains(strings.ToLower(contentType), "webp") {
		return nil, fmt.Errorf("only image/webp accepted")
	}
	if sizeHint > s.maxBytes {
		return nil, fmt.Errorf("file too large (max %d bytes)", s.maxBytes)
	}

	safeEmp := sanitizeSegment(employeeID)
	year := capturedAt.Format("2006")
	month := capturedAt.Format("01")
	day := capturedAt.Format("02")
	// Stable name from idempotency key so a rare race rewrites the same path.
	base := screenshotID + ".webp"

	rel := filepath.ToSlash(filepath.Join(safeEmp, year, month, day, base))
	absDir := filepath.Join(s.rootDir, safeEmp, year, month, day)
	if err := os.MkdirAll(absDir, 0o755); err != nil {
		return nil, fmt.Errorf("mkdir: %w", err)
	}
	absPath := filepath.Join(absDir, base)

	tmp := absPath + ".tmp"
	f, err := os.OpenFile(tmp, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
	if err != nil {
		return nil, err
	}
	limited := io.LimitReader(r, s.maxBytes+1)
	n, copyErr := io.Copy(f, limited)
	closeErr := f.Close()
	if copyErr != nil {
		_ = os.Remove(tmp)
		return nil, copyErr
	}
	if closeErr != nil {
		_ = os.Remove(tmp)
		return nil, closeErr
	}
	if n > s.maxBytes {
		_ = os.Remove(tmp)
		return nil, fmt.Errorf("file too large (max %d bytes)", s.maxBytes)
	}
	if err := os.Rename(tmp, absPath); err != nil {
		_ = os.Remove(tmp)
		return nil, err
	}

	row := &models.Screenshot{
		ID:          screenshotID,
		EmployeeID:  employeeID,
		MachineID:   strings.TrimSpace(machineID),
		CapturedAt:  capturedAt,
		RelPath:     rel,
		ByteSize:    n,
		Width:       width,
		Height:      height,
		ContentType: contentType,
	}
	created, err := s.repo.InsertIdempotent(ctx, row)
	if err != nil {
		_ = os.Remove(absPath)
		return nil, err
	}
	if !created {
		// Lost race / retry: peer already committed this id.
		if row.RelPath != "" && row.RelPath != rel {
			_ = os.Remove(absPath)
		}
		return &SaveUploadResult{Row: row, Replay: true}, nil
	}
	return &SaveUploadResult{Row: row, Replay: false}, nil
}

func (s *ScreenshotService) Get(ctx context.Context, id string) (*models.Screenshot, error) {
	return s.repo.GetByID(ctx, id)
}

func (s *ScreenshotService) AbsolutePath(rel string) (string, error) {
	rel = filepath.Clean(filepath.FromSlash(rel))
	if strings.HasPrefix(rel, "..") || filepath.IsAbs(rel) {
		return "", fmt.Errorf("invalid path")
	}
	abs := filepath.Join(s.rootDir, rel)
	rootAbs, err := filepath.Abs(s.rootDir)
	if err != nil {
		return "", err
	}
	abs, err = filepath.Abs(abs)
	if err != nil {
		return "", err
	}
	if !strings.HasPrefix(abs, rootAbs+string(os.PathSeparator)) && abs != rootAbs {
		return "", fmt.Errorf("path escape")
	}
	return abs, nil
}

func (s *ScreenshotService) List(
	ctx context.Context,
	f repository.ScreenshotListFilter,
) ([]models.ScreenshotListItem, error) {
	items, err := s.repo.List(ctx, f)
	if err != nil {
		return nil, err
	}
	for i := range items {
		items[i].ImageURL = "/api/v1/screenshots/" + items[i].ID + "/image"
	}
	return items, nil
}

func sanitizeSegment(s string) string {
	s = strings.TrimSpace(s)
	s = safePathSegment.ReplaceAllString(s, "-")
	s = strings.Trim(s, "-.")
	if s == "" {
		return "unknown"
	}
	if len(s) > 120 {
		s = s[:120]
	}
	return s
}
