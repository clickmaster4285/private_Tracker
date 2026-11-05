package services

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/alpha-ai-tracker/server/internal/config"
	"github.com/alpha-ai-tracker/server/internal/dto"
)

var (
	ErrGitHubNotConfigured = errors.New("github releases integration is not configured")
	ErrAssetNotFound       = errors.New("release asset not found")
	ErrAssetNotAllowed     = errors.New("release asset is not an approved installer")
	ErrGitHubUpstream      = errors.New("github upstream error")
	ErrGitHubRateLimited   = errors.New("github rate limit exceeded")
	ErrGitHubUnauthorized  = errors.New("github authentication failed")
)

// GitHubReleaseService fetches private-repo release metadata and streams assets.
type GitHubReleaseService struct {
	cfg    config.GitHubConfig
	client *http.Client

	mu        sync.RWMutex
	cachedAt  time.Time
	cached    *dto.ReleasesResponse
	assetIndex map[int64]cachedAsset // rebuilt with cache
	rangeOK   bool                   // true once we observe Accept-Ranges / 206 from object host
}

type cachedAsset struct {
	ID          int64
	Name        string
	Platform    string
	Size        int64
	ContentType string
	TagName     string
	Prerelease  bool
}

// AssetStream is an open upstream body ready to relay to the browser.
type AssetStream struct {
	Body          io.ReadCloser
	StatusCode    int
	ContentLength int64 // -1 if unknown
	ContentRange  string
	AcceptRanges  bool
	ContentType   string
	Filename      string
	Size          int64 // full asset size from metadata
	TagName       string
	Platform      string
}

func NewGitHubReleaseService(cfg config.GitHubConfig) *GitHubReleaseService {
	transport := &http.Transport{
		Proxy:                 http.ProxyFromEnvironment,
		MaxIdleConns:          32,
		IdleConnTimeout:       90 * time.Second,
		TLSHandshakeTimeout:   10 * time.Second,
		ExpectContinueTimeout: 1 * time.Second,
		ResponseHeaderTimeout: 30 * time.Second,
	}
	client := &http.Client{
		Timeout:   0, // streaming downloads must not use a global body timeout
		Transport: transport,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if len(via) >= 5 {
				return fmt.Errorf("too many redirects")
			}
			// Never forward the GitHub token to a non-API host.
			if !isGitHubAPIHost(req.URL.Host) {
				req.Header.Del("Authorization")
			}
			if !isAllowedGitHubDownloadHost(req.URL.Host) {
				return fmt.Errorf("blocked redirect host %q", req.URL.Host)
			}
			return nil
		},
	}
	return &GitHubReleaseService{cfg: cfg, client: client, assetIndex: map[int64]cachedAsset{}}
}

func (s *GitHubReleaseService) Configured() bool {
	return s != nil && strings.TrimSpace(s.cfg.Token) != "" &&
		strings.TrimSpace(s.cfg.Owner) != "" && strings.TrimSpace(s.cfg.Repo) != ""
}

// ListReleases returns published releases (cached). Drafts are excluded.
func (s *GitHubReleaseService) ListReleases(ctx context.Context) (*dto.ReleasesResponse, error) {
	if !s.Configured() {
		return nil, ErrGitHubNotConfigured
	}

	s.mu.RLock()
	if s.cached != nil && time.Since(s.cachedAt) < s.cfg.CacheTTL {
		out := cloneReleasesResponse(s.cached)
		s.mu.RUnlock()
		return out, nil
	}
	s.mu.RUnlock()

	return s.refresh(ctx)
}

func (s *GitHubReleaseService) refresh(ctx context.Context) (*dto.ReleasesResponse, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	// Double-check after acquiring write lock.
	if s.cached != nil && time.Since(s.cachedAt) < s.cfg.CacheTTL {
		return cloneReleasesResponse(s.cached), nil
	}

	raw, err := s.fetchAllReleases(ctx)
	if err != nil {
		// Serve stale cache on upstream failure when available.
		if s.cached != nil {
			log.Printf("[releases] github refresh failed (%v); serving stale cache age=%s", err, time.Since(s.cachedAt).Round(time.Second))
			return cloneReleasesResponse(s.cached), nil
		}
		return nil, err
	}

	resp, index := mapGitHubReleases(raw)
	s.cached = resp
	s.cachedAt = time.Now()
	s.assetIndex = index
	return cloneReleasesResponse(resp), nil
}

func (s *GitHubReleaseService) lookupAsset(ctx context.Context, assetID int64) (cachedAsset, error) {
	if !s.Configured() {
		return cachedAsset{}, ErrGitHubNotConfigured
	}
	if _, err := s.ListReleases(ctx); err != nil {
		return cachedAsset{}, err
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	a, ok := s.assetIndex[assetID]
	if !ok {
		return cachedAsset{}, ErrAssetNotFound
	}
	return a, nil
}

// OpenAssetStream validates the asset and opens a streaming upstream response.
func (s *GitHubReleaseService) OpenAssetStream(ctx context.Context, assetID int64, rangeHeader string) (*AssetStream, error) {
	asset, err := s.lookupAsset(ctx, assetID)
	if err != nil {
		return nil, err
	}
	if asset.Platform == "" {
		return nil, ErrAssetNotAllowed
	}

	apiURL := fmt.Sprintf("https://api.github.com/repos/%s/%s/releases/assets/%d",
		url.PathEscape(s.cfg.Owner), url.PathEscape(s.cfg.Repo), assetID)

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, apiURL, nil)
	if err != nil {
		return nil, err
	}
	s.setGitHubHeaders(req)
	req.Header.Set("Accept", "application/octet-stream")
	if rangeHeader != "" {
		req.Header.Set("Range", rangeHeader)
	}

	res, err := s.client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("%w: %v", ErrGitHubUpstream, err)
	}

	if res.StatusCode == http.StatusUnauthorized || res.StatusCode == http.StatusForbidden {
		res.Body.Close()
		return nil, ErrGitHubUnauthorized
	}
	if res.StatusCode == http.StatusTooManyRequests {
		res.Body.Close()
		return nil, ErrGitHubRateLimited
	}
	if res.StatusCode == http.StatusNotFound {
		res.Body.Close()
		return nil, ErrAssetNotFound
	}
	if res.StatusCode != http.StatusOK && res.StatusCode != http.StatusPartialContent {
		defer res.Body.Close()
		snippet, _ := io.ReadAll(io.LimitReader(res.Body, 512))
		log.Printf("[releases] asset %d upstream status=%d body=%q", assetID, res.StatusCode, string(snippet))
		return nil, fmt.Errorf("%w: status %d", ErrGitHubUpstream, res.StatusCode)
	}

	acceptRanges := strings.EqualFold(res.Header.Get("Accept-Ranges"), "bytes") ||
		res.StatusCode == http.StatusPartialContent ||
		s.rangeSupported()
	if acceptRanges {
		s.markRangeSupported()
	}

	cl := int64(-1)
	if v := res.Header.Get("Content-Length"); v != "" {
		if n, err := strconv.ParseInt(v, 10, 64); err == nil {
			cl = n
		}
	}

	ct := res.Header.Get("Content-Type")
	if ct == "" || strings.HasPrefix(ct, "text/") || strings.Contains(ct, "json") {
		ct = contentTypeForFilename(asset.Name)
	}

	return &AssetStream{
		Body:          res.Body,
		StatusCode:    res.StatusCode,
		ContentLength: cl,
		ContentRange:  res.Header.Get("Content-Range"),
		AcceptRanges:  acceptRanges || asset.Size > 0,
		ContentType:   ct,
		Filename:      asset.Name,
		Size:          asset.Size,
		TagName:       asset.TagName,
		Platform:      asset.Platform,
	}, nil
}

// ProbeRangeSupport issues a tiny ranged GET to learn whether the object host supports Range.
func (s *GitHubReleaseService) ProbeRangeSupport(ctx context.Context, assetID int64) bool {
	if s.rangeSupported() {
		return true
	}
	stream, err := s.OpenAssetStream(ctx, assetID, "bytes=0-0")
	if err != nil {
		return false
	}
	defer stream.Body.Close()
	ok := stream.StatusCode == http.StatusPartialContent
	if ok {
		s.markRangeSupported()
	}
	return ok
}

func (s *GitHubReleaseService) rangeSupported() bool {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.rangeOK
}

func (s *GitHubReleaseService) markRangeSupported() {
	s.mu.Lock()
	s.rangeOK = true
	s.mu.Unlock()
}

func (s *GitHubReleaseService) setGitHubHeaders(req *http.Request) {
	req.Header.Set("Authorization", "Bearer "+s.cfg.Token)
	req.Header.Set("X-GitHub-Api-Version", s.cfg.APIVersion)
	req.Header.Set("User-Agent", "AlphaAITracker-ReleaseBridge")
}

func (s *GitHubReleaseService) fetchAllReleases(ctx context.Context) ([]ghRelease, error) {
	var all []ghRelease
	page := 1
	const perPage = 100
	const maxPages = 20 // defensive cap

	for page <= maxPages {
		u := fmt.Sprintf("https://api.github.com/repos/%s/%s/releases?per_page=%d&page=%d",
			url.PathEscape(s.cfg.Owner), url.PathEscape(s.cfg.Repo), perPage, page)
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
		if err != nil {
			return nil, err
		}
		s.setGitHubHeaders(req)
		req.Header.Set("Accept", "application/vnd.github+json")

		res, err := s.client.Do(req)
		if err != nil {
			return nil, fmt.Errorf("%w: %v", ErrGitHubUpstream, err)
		}
		body, readErr := io.ReadAll(io.LimitReader(res.Body, 8<<20))
		res.Body.Close()
		if readErr != nil {
			return nil, fmt.Errorf("%w: %v", ErrGitHubUpstream, readErr)
		}

		switch res.StatusCode {
		case http.StatusOK:
			// continue
		case http.StatusUnauthorized, http.StatusForbidden:
			return nil, ErrGitHubUnauthorized
		case http.StatusTooManyRequests:
			return nil, ErrGitHubRateLimited
		default:
			log.Printf("[releases] list status=%d body=%q", res.StatusCode, truncate(string(body), 200))
			return nil, fmt.Errorf("%w: status %d", ErrGitHubUpstream, res.StatusCode)
		}

		var batch []ghRelease
		if err := json.Unmarshal(body, &batch); err != nil {
			return nil, fmt.Errorf("%w: decode: %v", ErrGitHubUpstream, err)
		}
		all = append(all, batch...)
		if len(batch) < perPage {
			break
		}
		page++
	}
	return all, nil
}

// ─── GitHub wire types (internal) ───────────────────────────────────────────

type ghRelease struct {
	TagName         string    `json:"tag_name"`
	Name            string    `json:"name"`
	Body            string    `json:"body"`
	Draft           bool      `json:"draft"`
	Prerelease      bool      `json:"prerelease"`
	PublishedAt     string    `json:"published_at"`
	CreatedAt       string    `json:"created_at"`
	Assets          []ghAsset `json:"assets"`
}

type ghAsset struct {
	ID                 int64  `json:"id"`
	Name               string `json:"name"`
	ContentType        string `json:"content_type"`
	Size               int64  `json:"size"`
	BrowserDownloadURL string `json:"browser_download_url"`
}

func mapGitHubReleases(raw []ghRelease) (*dto.ReleasesResponse, map[int64]cachedAsset) {
	out := &dto.ReleasesResponse{Releases: make([]dto.ReleaseResponse, 0, len(raw))}
	index := make(map[int64]cachedAsset)

	for _, r := range raw {
		if r.Draft {
			continue
		}
		published := r.PublishedAt
		if published == "" {
			published = r.CreatedAt
		}
		name := r.Name
		if name == "" {
			name = r.TagName
		}
		assets := make([]dto.ReleaseAssetResponse, 0, len(r.Assets))
		for _, a := range r.Assets {
			platform := ClassifyInstallerPlatform(a.Name)
			if platform == "" {
				continue
			}
			assets = append(assets, dto.ReleaseAssetResponse{
				ID:          a.ID,
				Name:        a.Name,
				Platform:    platform,
				Size:        a.Size,
				ContentType: a.ContentType,
			})
			index[a.ID] = cachedAsset{
				ID:          a.ID,
				Name:        a.Name,
				Platform:    platform,
				Size:        a.Size,
				ContentType: a.ContentType,
				TagName:     r.TagName,
				Prerelease:  r.Prerelease,
			}
		}
		out.Releases = append(out.Releases, dto.ReleaseResponse{
			TagName:     r.TagName,
			Name:        name,
			PublishedAt: published,
			Prerelease:  r.Prerelease,
			Body:        r.Body,
			Assets:      assets,
		})
	}

	// GitHub returns newest-first; keep that order. Latest stable = first non-prerelease.
	for _, r := range out.Releases {
		if !r.Prerelease {
			out.LatestStable = r.TagName
			break
		}
	}
	return out, index
}

// ClassifyInstallerPlatform returns windows|linux|macos or "" if unsupported.
func ClassifyInstallerPlatform(filename string) string {
	lower := strings.ToLower(strings.TrimSpace(filename))
	switch {
	case strings.HasSuffix(lower, ".exe"):
		return "windows"
	case strings.HasSuffix(lower, ".deb"):
		return "linux"
	case strings.HasSuffix(lower, ".dmg"):
		return "macos"
	default:
		return ""
	}
}

func contentTypeForFilename(name string) string {
	lower := strings.ToLower(name)
	switch {
	case strings.HasSuffix(lower, ".exe"):
		return "application/vnd.microsoft.portable-executable"
	case strings.HasSuffix(lower, ".deb"):
		return "application/vnd.debian.binary-package"
	case strings.HasSuffix(lower, ".dmg"):
		return "application/x-apple-diskimage"
	default:
		return "application/octet-stream"
	}
}

func isGitHubAPIHost(host string) bool {
	h := strings.ToLower(host)
	return h == "api.github.com"
}

func isAllowedGitHubDownloadHost(host string) bool {
	h := strings.ToLower(host)
	if h == "api.github.com" || h == "github.com" {
		return true
	}
	return strings.HasSuffix(h, ".githubusercontent.com") ||
		strings.HasSuffix(h, ".github.com")
}

func cloneReleasesResponse(in *dto.ReleasesResponse) *dto.ReleasesResponse {
	if in == nil {
		return nil
	}
	out := &dto.ReleasesResponse{
		LatestStable: in.LatestStable,
		Releases:     make([]dto.ReleaseResponse, len(in.Releases)),
	}
	copy(out.Releases, in.Releases)
	for i := range out.Releases {
		if out.Releases[i].Assets != nil {
			assets := make([]dto.ReleaseAssetResponse, len(out.Releases[i].Assets))
			copy(assets, out.Releases[i].Assets)
			out.Releases[i].Assets = assets
		}
	}
	return out
}

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n] + "…"
}

// ParseByteRange parses a single HTTP Range bytes unit. Multi-range is rejected.
// Returns start, end (inclusive), and ok. end == -1 means open-ended (suffix until EOF).
func ParseByteRange(header string, size int64) (start, end int64, status int, errMsg string) {
	header = strings.TrimSpace(header)
	if header == "" {
		return 0, 0, 0, ""
	}
	if !strings.HasPrefix(header, "bytes=") {
		return 0, 0, http.StatusRequestedRangeNotSatisfiable, "invalid range unit"
	}
	spec := strings.TrimPrefix(header, "bytes=")
	if strings.Contains(spec, ",") {
		return 0, 0, http.StatusRequestedRangeNotSatisfiable, "multipart ranges not supported"
	}
	parts := strings.SplitN(spec, "-", 2)
	if len(parts) != 2 {
		return 0, 0, http.StatusRequestedRangeNotSatisfiable, "invalid range"
	}
	if parts[0] == "" {
		// suffix: bytes=-N
		suffix, err := strconv.ParseInt(parts[1], 10, 64)
		if err != nil || suffix <= 0 {
			return 0, 0, http.StatusRequestedRangeNotSatisfiable, "invalid suffix range"
		}
		if size <= 0 {
			return 0, 0, http.StatusRequestedRangeNotSatisfiable, "unknown size"
		}
		if suffix > size {
			suffix = size
		}
		return size - suffix, size - 1, http.StatusPartialContent, ""
	}
	start, err := strconv.ParseInt(parts[0], 10, 64)
	if err != nil || start < 0 {
		return 0, 0, http.StatusRequestedRangeNotSatisfiable, "invalid start"
	}
	if parts[1] == "" {
		if size > 0 && start >= size {
			return 0, 0, http.StatusRequestedRangeNotSatisfiable, "unsatisfiable"
		}
		return start, -1, http.StatusPartialContent, ""
	}
	end, err = strconv.ParseInt(parts[1], 10, 64)
	if err != nil || end < start {
		return 0, 0, http.StatusRequestedRangeNotSatisfiable, "invalid end"
	}
	if size > 0 {
		if start >= size {
			return 0, 0, http.StatusRequestedRangeNotSatisfiable, "unsatisfiable"
		}
		if end >= size {
			end = size - 1
		}
	}
	return start, end, http.StatusPartialContent, ""
}

// SafeContentDisposition builds a Content-Disposition attachment header with a safe filename.
func SafeContentDisposition(filename string) string {
	safe := sanitizeFilename(filename)
	return fmt.Sprintf(`attachment; filename="%s"`, safe)
}

func sanitizeFilename(name string) string {
	name = strings.TrimSpace(name)
	name = strings.ReplaceAll(name, `"`, "")
	name = strings.ReplaceAll(name, "\r", "")
	name = strings.ReplaceAll(name, "\n", "")
	name = strings.ReplaceAll(name, "/", "_")
	name = strings.ReplaceAll(name, "\\", "_")
	if name == "" {
		return "download.bin"
	}
	return name
}
