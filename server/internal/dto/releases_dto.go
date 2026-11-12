package dto

// ReleasesResponse is the public metadata payload for GET /api/v1/releases.
type ReleasesResponse struct {
	LatestStable string            `json:"latestStable"`
	Releases     []ReleaseResponse `json:"releases"`
}

// ReleaseResponse is one published local installer version from RELEASES_DIR.
type ReleaseResponse struct {
	TagName     string               `json:"tagName"`
	Name        string               `json:"name"`
	PublishedAt string               `json:"publishedAt"`
	Prerelease  bool                 `json:"prerelease"`
	Body        string               `json:"body"`
	Assets      []ReleaseAssetResponse `json:"assets"`
}

// ReleaseAssetResponse is a downloadable installer asset exposed to the website
// and desktop clients (served from the local setup folder).
type ReleaseAssetResponse struct {
	ID          int64  `json:"id"`
	Name        string `json:"name"`
	Platform    string `json:"platform"` // windows | linux | macos
	Size        int64  `json:"size"`
	ContentType string `json:"contentType,omitempty"`
}
