package dto

// ReleasesResponse is the public metadata payload for GET /api/v1/releases.
type ReleasesResponse struct {
	LatestStable string            `json:"latestStable"`
	Releases     []ReleaseResponse `json:"releases"`
}

// ReleaseResponse is one published GitHub release (drafts never included).
type ReleaseResponse struct {
	TagName     string               `json:"tagName"`
	Name        string               `json:"name"`
	PublishedAt string               `json:"publishedAt"`
	Prerelease  bool                 `json:"prerelease"`
	Body        string               `json:"body"`
	Assets      []ReleaseAssetResponse `json:"assets"`
}

// ReleaseAssetResponse is a downloadable installer asset exposed to the website.
// Upstream GitHub URLs are intentionally omitted.
type ReleaseAssetResponse struct {
	ID          int64  `json:"id"`
	Name        string `json:"name"`
	Platform    string `json:"platform"` // windows | linux | macos
	Size        int64  `json:"size"`
	ContentType string `json:"contentType,omitempty"`
}
