package services

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/alpha-ai-tracker/server/internal/config"
)

func TestClassifyInstallerPlatform(t *testing.T) {
	cases := map[string]string{
		"AlphaAITracker-Setup-2.1.11.exe": "windows",
		"alpha-ai-tracker_2.1.11_amd64.deb": "linux",
		"AlphaAITracker-2.1.11.dmg":         "macos",
		"SOURCE.tar.gz":                    "",
		"notes.txt":                        "",
		"Setup.EXE":                        "windows",
	}
	for name, want := range cases {
		if got := ClassifyInstallerPlatform(name); got != want {
			t.Fatalf("%s: got %q want %q", name, got, want)
		}
	}
}

func TestMapGitHubReleasesFiltersDraftsAndLatestStable(t *testing.T) {
	raw := []ghRelease{
		{TagName: "v2.0.0-rc1", Name: "RC", Prerelease: true, PublishedAt: "2026-10-20T00:00:00Z",
			Assets: []ghAsset{{ID: 1, Name: "a.exe", Size: 10}}},
		{TagName: "v1.9.0", Name: "Stable", Draft: false, PublishedAt: "2026-10-19T00:00:00Z",
			Assets: []ghAsset{
				{ID: 2, Name: "a.exe", Size: 11},
				{ID: 3, Name: "a.deb", Size: 12},
				{ID: 4, Name: "source.zip", Size: 99},
			}},
		{TagName: "v0.0.1-draft", Draft: true, Assets: []ghAsset{{ID: 5, Name: "d.exe", Size: 1}}},
	}
	resp, index := mapGitHubReleases(raw)
	if resp.LatestStable != "v1.9.0" {
		t.Fatalf("latestStable=%q", resp.LatestStable)
	}
	if len(resp.Releases) != 2 {
		t.Fatalf("expected 2 published releases, got %d", len(resp.Releases))
	}
	if _, ok := index[5]; ok {
		t.Fatal("draft asset must not be indexed")
	}
	if _, ok := index[4]; ok {
		t.Fatal("non-installer must not be indexed")
	}
	stable := resp.Releases[1]
	if len(stable.Assets) != 2 {
		t.Fatalf("expected 2 installer assets, got %d", len(stable.Assets))
	}
}

func TestParseByteRange(t *testing.T) {
	start, end, status, _ := ParseByteRange("bytes=0-1023", 5000)
	if status != http.StatusPartialContent || start != 0 || end != 1023 {
		t.Fatalf("got %d-%d status=%d", start, end, status)
	}
	_, _, status, _ = ParseByteRange("bytes=9000-9010", 100)
	if status != http.StatusRequestedRangeNotSatisfiable {
		t.Fatalf("expected 416, got %d", status)
	}
	start, end, status, _ = ParseByteRange("bytes=10-", 100)
	if status != http.StatusPartialContent || start != 10 || end != -1 {
		t.Fatalf("open end: %d-%d status=%d", start, end, status)
	}
	start, end, status, _ = ParseByteRange("bytes=-5", 100)
	if status != http.StatusPartialContent || start != 95 || end != 99 {
		t.Fatalf("suffix: %d-%d status=%d", start, end, status)
	}
}

func TestSafeContentDisposition(t *testing.T) {
	h := SafeContentDisposition("evil\"\r\nX-Injected: yes.exe")
	if strings.Contains(h, "\r") || strings.Contains(h, "\n") {
		t.Fatalf("CRLF injection possible: %q", h)
	}
	if strings.Count(h, `"`) != 2 {
		t.Fatalf("expected exactly two quotes around filename: %q", h)
	}
	if !strings.Contains(h, "evil") || !strings.Contains(h, "yes.exe") {
		t.Fatalf("unexpected: %q", h)
	}
}

func TestIsAllowedGitHubDownloadHost(t *testing.T) {
	ok := []string{"api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "github.com"}
	for _, h := range ok {
		if !isAllowedGitHubDownloadHost(h) {
			t.Fatalf("expected allow %q", h)
		}
	}
	for _, h := range []string{"evil.example", "169.254.169.254", "localhost"} {
		if isAllowedGitHubDownloadHost(h) {
			t.Fatalf("expected deny %q", h)
		}
	}
}

func TestListReleasesAgainstMockGitHub(t *testing.T) {
	var hits int
	mux := http.NewServeMux()
	mux.HandleFunc("/repos/o/r/releases", func(w http.ResponseWriter, r *http.Request) {
		hits++
		if r.Header.Get("Authorization") != "Bearer test-token" {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		_ = json.NewEncoder(w).Encode([]ghRelease{
			{
				TagName: "v1.0.0", Name: "One", PublishedAt: "2026-01-01T00:00:00Z",
				Assets: []ghAsset{{ID: 42, Name: "app.exe", Size: 3, ContentType: "application/octet-stream"}},
			},
		})
	})
	srv := httptest.NewServer(mux)
	defer srv.Close()

	svc := NewGitHubReleaseService(config.GitHubConfig{
		Owner: "o", Repo: "r", Token: "test-token", APIVersion: "2022-11-28", CacheTTL: time.Minute,
	})
	// Point client at mock by replacing transport via custom RoundTripper through Base URL hack:
	// Override by swapping client to rewrite host — use a transport that redirects api.github.com.
	svc.client = &http.Client{
		Transport: roundTripFunc(func(req *http.Request) (*http.Response, error) {
			u := *req.URL
			u.Scheme = "http"
			u.Host = strings.TrimPrefix(srv.URL, "http://")
			req2 := req.Clone(req.Context())
			req2.URL = &u
			req2.Host = u.Host
			return http.DefaultTransport.RoundTrip(req2)
		}),
		CheckRedirect: svc.client.CheckRedirect,
	}

	resp, err := svc.ListReleases(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if resp.LatestStable != "v1.0.0" || len(resp.Releases) != 1 {
		t.Fatalf("%+v", resp)
	}
	// Second call should hit cache.
	_, err = svc.ListReleases(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if hits != 1 {
		t.Fatalf("expected 1 upstream hit (cache), got %d", hits)
	}
}

func TestCheckRedirectStripsAuthAndBlocksSSRF(t *testing.T) {
	svc := NewGitHubReleaseService(config.GitHubConfig{
		Owner: "o", Repo: "r", Token: "secret", APIVersion: "2022-11-28", CacheTTL: time.Minute,
	})
	check := svc.client.CheckRedirect

	via := []*http.Request{httptest.NewRequest(http.MethodGet, "https://api.github.com/x", nil)}
	via[0].Header.Set("Authorization", "Bearer secret")

	next, _ := http.NewRequest(http.MethodGet, "https://objects.githubusercontent.com/file", nil)
	next.Header.Set("Authorization", "Bearer secret")
	if err := check(next, via); err != nil {
		t.Fatal(err)
	}
	if next.Header.Get("Authorization") != "" {
		t.Fatal("expected Authorization stripped on object host")
	}

	evil, _ := http.NewRequest(http.MethodGet, "http://169.254.169.254/latest", nil)
	if err := check(evil, via); err == nil {
		t.Fatal("expected SSRF redirect blocked")
	}
}

type roundTripFunc func(*http.Request) (*http.Response, error)

func (f roundTripFunc) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
