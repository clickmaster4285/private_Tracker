package services

import (
	"os"
	"path/filepath"
	"testing"
)

func writeFile(t *testing.T, dir, name, body string) {
	t.Helper()
	if err := os.WriteFile(filepath.Join(dir, name), []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestVersionFromInstallerName(t *testing.T) {
	cases := map[string]string{
		"v1.0.0.exe":                        "1.0.0",
		"v2.10.2.exe":                       "2.10.2",
		"AlphaAITracker-Setup-2.10.2.exe":   "2.10.2",
		"alpha-ai-tracker_2.10.2_amd64.deb": "2.10.2",
		"setup.exe":                         "",
	}
	for in, want := range cases {
		if got := VersionFromInstallerName(in); got != want {
			t.Errorf("%s: got %q want %q", in, got, want)
		}
	}
}

func TestCompareVersions(t *testing.T) {
	if CompareVersions("2.10.2", "2.9.9") <= 0 {
		t.Fatal("2.10.2 must be newer than 2.9.9")
	}
	if CompareVersions("1.0.0", "1.0.0") != 0 {
		t.Fatal("equal versions")
	}
}

func TestLocalReleasesNewestFirstWithNotes(t *testing.T) {
	dir := t.TempDir()
	writeFile(t, dir, "v1.0.0.exe", "old")
	writeFile(t, dir, "v2.10.2.exe", "new-win")
	writeFile(t, dir, "alpha-ai-tracker_2.10.2_amd64.deb", "new-deb")
	writeFile(t, dir, "v2.10.2.md", "What changed")
	writeFile(t, dir, "v3.0.0.exe.part", "partial") // ignored
	writeFile(t, dir, "readme.txt", "ignored")

	svc := NewLocalReleaseService(dir)
	resp, err := svc.ListReleases()
	if err != nil {
		t.Fatal(err)
	}
	if resp.LatestStable != "v2.10.2" {
		t.Fatalf("latestStable=%q", resp.LatestStable)
	}
	if len(resp.Releases) != 2 || resp.Releases[1].TagName != "v1.0.0" {
		t.Fatalf("releases=%+v", resp.Releases)
	}
	top := resp.Releases[0]
	if top.Body != "What changed" || len(top.Assets) != 2 {
		t.Fatalf("top=%+v", top)
	}
	for _, a := range top.Assets {
		got, err := svc.LookupAsset(a.ID)
		if err != nil || got.Name != a.Name || got.Size != a.Size {
			t.Fatalf("lookup %d: %+v %v", a.ID, got, err)
		}
		if a.ID > 1<<53-1 {
			t.Fatalf("id %d exceeds JS safe integer", a.ID)
		}
	}
	if _, err := svc.LookupAsset(12345); err != ErrAssetNotFound {
		t.Fatalf("want ErrAssetNotFound, got %v", err)
	}
}

func TestLocalReleasesMissingDirIsEmpty(t *testing.T) {
	svc := NewLocalReleaseService(filepath.Join(t.TempDir(), "nope"))
	resp, err := svc.ListReleases()
	if err != nil || len(resp.Releases) != 0 || resp.LatestStable != "" {
		t.Fatalf("resp=%+v err=%v", resp, err)
	}
}

func TestSafeContentDisposition(t *testing.T) {
	got := SafeContentDisposition("a\"b/c\r\n.exe")
	if got != `attachment; filename="ab_c.exe"` {
		t.Fatalf("got %s", got)
	}
}
