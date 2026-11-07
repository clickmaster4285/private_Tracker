package services

import (
	"errors"
	"fmt"
	"hash/fnv"
	"io"
	"log"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
)

// ErrAssetNotFound is returned when an asset id does not map to an installer in the setup folder.
var ErrAssetNotFound = errors.New("release asset not found")

// releaseScanTTL bounds how often the setup folder is re-listed. Every client polls
// GET /api/v1/releases once a minute, so the directory read must stay cheap.
const releaseScanTTL = 10 * time.Second

// maxReleaseNotesBytes caps an optional v<version>.md release-notes file.
const maxReleaseNotesBytes = 64 * 1024

// installerVersionRx finds the first X.Y.Z in an installer filename, e.g.
// "v1.0.0.exe", "AlphaAITracker-Setup-2.10.2.exe", "alpha-ai-tracker_2.10.2_amd64.deb".
var installerVersionRx = regexp.MustCompile(`(?:^|[^0-9.])[vV]?(\d+)\.(\d+)\.(\d+)(?:[^0-9]|$)`)

// LocalAsset is one installer file in the setup folder.
type LocalAsset struct {
	ID          int64
	Name        string
	Path        string
	Platform    string
	Version     string
	Size        int64
	ModTime     time.Time
	ContentType string
}

// LocalReleaseService serves installers that an admin drops into the server's setup
// folder (RELEASES_DIR, default "setup"). One release per version found in the
// filenames; the newest version is latestStable. No GitHub, no token.
type LocalReleaseService struct {
	dir string

	mu       sync.Mutex
	cached   *dto.ReleasesResponse
	index    map[int64]LocalAsset
	cachedAt time.Time
}

// NewLocalReleaseService resolves dir. A relative dir is tried against the working
// directory first, then next to the server binary (and its parent, for bin/ layouts).
func NewLocalReleaseService(dir string) *LocalReleaseService {
	return &LocalReleaseService{dir: resolveReleasesDir(dir)}
}

// Dir is the absolute setup folder being served.
func (s *LocalReleaseService) Dir() string { return s.dir }

func resolveReleasesDir(dir string) string {
	dir = strings.TrimSpace(dir)
	if dir == "" {
		dir = "setup"
	}
	if filepath.IsAbs(dir) {
		return filepath.Clean(dir)
	}
	candidates := []string{}
	if wd, err := os.Getwd(); err == nil {
		candidates = append(candidates, filepath.Join(wd, dir))
	}
	if exe, err := os.Executable(); err == nil {
		exeDir := filepath.Dir(exe)
		candidates = append(candidates, filepath.Join(exeDir, dir), filepath.Join(exeDir, "..", dir))
	}
	for _, c := range candidates {
		if st, err := os.Stat(c); err == nil && st.IsDir() {
			return filepath.Clean(c)
		}
	}
	if len(candidates) > 0 {
		return filepath.Clean(candidates[0])
	}
	return dir
}

// ListReleases returns installers grouped by version, newest first.
func (s *LocalReleaseService) ListReleases() (*dto.ReleasesResponse, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.cached != nil && time.Since(s.cachedAt) < releaseScanTTL {
		return cloneReleasesResponse(s.cached), nil
	}
	resp, index, err := scanReleasesDir(s.dir)
	if err != nil {
		return nil, err
	}
	s.cached, s.index, s.cachedAt = resp, index, time.Now()
	return cloneReleasesResponse(resp), nil
}

// LookupAsset returns the installer for an id from the latest scan.
func (s *LocalReleaseService) LookupAsset(id int64) (LocalAsset, error) {
	if _, err := s.ListReleases(); err != nil {
		return LocalAsset{}, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	a, ok := s.index[id]
	if !ok {
		return LocalAsset{}, ErrAssetNotFound
	}
	return a, nil
}

func scanReleasesDir(dir string) (*dto.ReleasesResponse, map[int64]LocalAsset, error) {
	empty := &dto.ReleasesResponse{Releases: []dto.ReleaseResponse{}}
	entries, err := os.ReadDir(dir)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return empty, map[int64]LocalAsset{}, nil
		}
		return nil, nil, fmt.Errorf("read releases dir: %w", err)
	}

	byVersion := map[string][]LocalAsset{}
	index := map[int64]LocalAsset{}
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		platform := ClassifyInstallerPlatform(name)
		if platform == "" {
			continue // .part, .md, stray files
		}
		version := VersionFromInstallerName(name)
		if version == "" {
			log.Printf("[releases] skipping %q: no X.Y.Z version in filename", name)
			continue
		}
		info, err := e.Info()
		if err != nil || !info.Mode().IsRegular() || info.Size() == 0 {
			continue
		}
		a := LocalAsset{
			ID:          assetIDForName(name),
			Name:        name,
			Path:        filepath.Join(dir, name),
			Platform:    platform,
			Version:     version,
			Size:        info.Size(),
			ModTime:     info.ModTime().UTC(),
			ContentType: contentTypeForFilename(name),
		}
		index[a.ID] = a
		byVersion[version] = append(byVersion[version], a)
	}

	versions := make([]string, 0, len(byVersion))
	for v := range byVersion {
		versions = append(versions, v)
	}
	sort.Slice(versions, func(i, j int) bool { return CompareVersions(versions[i], versions[j]) > 0 })

	out := &dto.ReleasesResponse{Releases: make([]dto.ReleaseResponse, 0, len(versions))}
	for _, v := range versions {
		assets := byVersion[v]
		sort.Slice(assets, func(i, j int) bool { return assets[i].Name < assets[j].Name })
		var newest time.Time
		rel := dto.ReleaseResponse{
			TagName: "v" + v,
			Name:    "v" + v,
			Body:    readReleaseNotes(dir, v),
			Assets:  make([]dto.ReleaseAssetResponse, 0, len(assets)),
		}
		for _, a := range assets {
			if a.ModTime.After(newest) {
				newest = a.ModTime
			}
			rel.Assets = append(rel.Assets, dto.ReleaseAssetResponse{
				ID:          a.ID,
				Name:        a.Name,
				Platform:    a.Platform,
				Size:        a.Size,
				ContentType: a.ContentType,
			})
		}
		rel.PublishedAt = newest.Format(time.RFC3339)
		out.Releases = append(out.Releases, rel)
	}
	if len(out.Releases) > 0 {
		out.LatestStable = out.Releases[0].TagName
	}
	return out, index, nil
}

// readReleaseNotes returns the optional v<version>.md (or <version>.md) next to the installers.
func readReleaseNotes(dir, version string) string {
	for _, n := range []string{"v" + version + ".md", version + ".md"} {
		f, err := os.Open(filepath.Join(dir, n))
		if err != nil {
			continue
		}
		b, err := io.ReadAll(io.LimitReader(f, maxReleaseNotesBytes))
		f.Close()
		if err == nil {
			return strings.TrimSpace(string(b))
		}
	}
	return ""
}

// VersionFromInstallerName extracts "X.Y.Z" from an installer filename, or "".
func VersionFromInstallerName(name string) string {
	m := installerVersionRx.FindStringSubmatch(name)
	if m == nil {
		return ""
	}
	return m[1] + "." + m[2] + "." + m[3]
}

// CompareVersions numerically compares two X.Y.Z strings (<0, 0, >0).
func CompareVersions(a, b string) int {
	pa, pb := strings.Split(a, "."), strings.Split(b, ".")
	for i := 0; i < 3; i++ {
		var x, y int
		if i < len(pa) {
			x, _ = strconv.Atoi(pa[i])
		}
		if i < len(pb) {
			y, _ = strconv.Atoi(pb[i])
		}
		if x != y {
			if x < y {
				return -1
			}
			return 1
		}
	}
	return 0
}

// assetIDForName is a stable id derived from the filename, kept within the
// JavaScript safe-integer range so the web can use it as a number.
func assetIDForName(name string) int64 {
	h := fnv.New64a()
	_, _ = h.Write([]byte(strings.ToLower(name)))
	id := int64(h.Sum64() & (1<<53 - 1))
	if id == 0 {
		id = 1
	}
	return id
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

// SafeContentDisposition builds a Content-Disposition attachment header with a safe filename.
func SafeContentDisposition(filename string) string {
	return fmt.Sprintf(`attachment; filename="%s"`, sanitizeFilename(filename))
}

func sanitizeFilename(name string) string {
	name = strings.TrimSpace(name)
	for _, bad := range []string{`"`, "\r", "\n"} {
		name = strings.ReplaceAll(name, bad, "")
	}
	name = strings.ReplaceAll(name, "/", "_")
	name = strings.ReplaceAll(name, "\\", "_")
	if name == "" {
		return "download.bin"
	}
	return name
}
