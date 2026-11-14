package config

import (
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/joho/godotenv"
)

// Config holds all configuration for the server.
type Config struct {
	Server   ServerConfig
	Database DatabaseConfig
	Redis    RedisConfig
	JWT      JWTConfig
	Admin    AdminConfig
	CORS     CORSConfig
	LogLevel string

	// LinkStaleDays is the staleness threshold (in days) after which an
	// employee↔app/package junction link is marked is_active = false.
	LinkStaleDays int

	// DefaultShiftTimezone is the IANA timezone applied to shifts that still
	// carry the migration default UTC (and used as the create fallback when the
	// client omits timezone). Empty = leave UTC unchanged.
	DefaultShiftTimezone string

	// DlpRemovalCodeTTLMin is the default lifetime (minutes) for one-time
	// protected stop/uninstall codes issued to Windows agents.
	DlpRemovalCodeTTLMin int

	// LiveStream configures the WebRTC SFU live preview hub.
	LiveStream LiveStreamConfig

	// PresenceWS configures the long-lived control / keep-alive WebSocket hub
	// (GET /api/v1/ws) — independent of LiveStream.
	PresenceWS PresenceWSConfig

	// Releases configures the local installer folder behind /releases + /downloads.
	Releases ReleasesConfig

	// Screenshots configures offline-first WebP storage (no retention purge yet).
	Screenshots ScreenshotsConfig

	// Face configures biometric enrollment media + FaceWorker shared secret.
	Face FaceConfig

	// Scale holds admission-control / Asynq / cache knobs for ~1000-agent fleets.
	// All Go processes still share ONE PostgreSQL — pools must fit max_connections.
	Scale ScaleConfig
}

// FaceConfig points at media/faces/{employeeId}/{jobId}/….
type FaceConfig struct {
	Dir            string
	MaxUploadBytes int64
	MaxFilesPerJob int
	// WorkerAPIKey authenticates POST /internal/face-worker/* (empty = worker endpoints disabled).
	WorkerAPIKey string
	// WorkerURL is the face_worker HTTP base (e.g. http://127.0.0.1:8010). API pushes jobs here.
	WorkerURL string
	// EmbeddingKeySecret seals embedding_ciphertext (AES-GCM). Prefer FACE_EMBEDDING_KEY;
	// falls back to JWT_SECRET when empty so lab boots without a second secret.
	EmbeddingKeySecret string
	// ModelsDir is face_worker/models — shared buffalo_l ONNX for worker + DeviceAuth download.
	ModelsDir string
	// BuffaloDetPackagePath is SCRFD det_10g for GET /face-models/packages/buffalo-l-det.
	BuffaloDetPackagePath string
	// BuffaloRecPackagePath is w600k_r50 for GET /face-models/packages/buffalo-l-rec.
	BuffaloRecPackagePath string
}

// ScreenshotsConfig points at media/screenshots/{emp}/{yyyy}/{MM}/{dd}/….
type ScreenshotsConfig struct {
	Dir              string
	MaxUploadBytes   int64
	UploadRPS        float64
	UploadBurst      int
}

// ScaleConfig controls rate limits, sync admission, Asynq workers, and Redis cache TTLs.
type ScaleConfig struct {
	// AsynqEnabled runs background sweeps via Asynq instead of in-process tickers.
	// Requires Redis. When true, prefer `go run ./cmd/worker` (or embed with AsynqEmbed).
	AsynqEnabled bool
	// AsynqEmbed starts Asynq scheduler+worker inside the API process (single-VPS default).
	AsynqEmbed bool
	// AsynqConcurrency is the hard cap on job handler goroutines.
	AsynqConcurrency int
	// SyncMaxConcurrent bounds in-flight DeviceAuth sync handlers (503 when full).
	SyncMaxConcurrent int
	// LoginRPS / LoginBurst rate-limit POST /auth/login and /auth/employee-login per IP.
	LoginRPS   float64
	LoginBurst int
	// SyncRPS / SyncBurst rate-limit DeviceAuth sync POSTs per IP (coarse).
	SyncRPS   float64
	SyncBurst int
	// ScreenshotUploadRPS / Burst rate-limit DeviceAuth screenshot uploads per IP.
	ScreenshotUploadRPS   float64
	ScreenshotUploadBurst int
	// AdminSearchRPS rate-limits JWT list/search-ish GETs per IP.
	AdminSearchRPS   float64
	AdminSearchBurst int
	// DashboardCacheTTLSec caches GET /dashboard/summary in Redis (0 = off).
	DashboardCacheTTLSec int
	// SessionSweepSec is the Asynq period for session lifecycle (default 60).
	SessionSweepSec int
}

// ReleasesConfig points at the folder holding client installers (e.g. setup/v1.0.0.exe).
type ReleasesConfig struct {
	Dir string
	// MaxConcurrentDownloads caps simultaneous GET /downloads body streams (fleet admission).
	MaxConcurrentDownloads int
	// DownloadRetryAfterSec is returned with 429 when the download gate is full.
	DownloadRetryAfterSec int
}

// LiveStreamConfig holds caps for the in-memory WebRTC SFU (no DB persistence).
type LiveStreamConfig struct {
	Enabled                bool
	MaxStreams             int
	MaxWatchersPerEmployee int
	IdleSec                int
	MaxBitrateKbps         int
	STUNURLs               []string
	TURNURLs               []string
	TURNUser               string
	TURNPass               string
}

// PresenceWSConfig holds caps for the in-memory control-channel presence hub.
type PresenceWSConfig struct {
	Enabled        bool
	MaxConnections int
}

type ServerConfig struct {
	Host string
	Port int
	// InstanceID uniquely identifies this process in a multi-node deploy
	// (presence + publisher registry + watch redirects). Empty → auto-generated.
	InstanceID string
	// InstancePublicURL is this process's externally reachable base URL
	// (scheme://host:port, no trailing slash). Used for Redis-driven watch
	// redirects if a second API instance ever exists. Empty → redirect disabled.
	InstancePublicURL string
	// TLSCertFile / TLSKeyFile enable HTTPS via Echo StartTLS when both are set
	// (paths relative to process cwd or absolute). Empty = plain HTTP.
	TLSCertFile string
	TLSKeyFile  string
	// CookieSecure forces Secure on auth_token / refresh_token cookies.
	// Defaults to true when TLS cert+key are configured.
	CookieSecure bool
}

type DatabaseConfig struct {
	Host            string
	Port            int
	User            string
	Password        string
	Name            string
	SSLMode         string
	MaxOpenConns    int
	MaxIdleConns    int
	ConnMaxLifetime time.Duration
}

type JWTConfig struct {
	Secret string
	// AccessExpiry is the WEB ADMIN access-token TTL (auth_token cookie).
	// Short-lived by design; re-minted via the rotating refresh token.
	AccessExpiry time.Duration
	// RefreshExpiry is the WEB ADMIN refresh-token lifetime (refresh_token cookie,
	// rotated on every successful refresh).
	RefreshExpiry time.Duration
	// EmployeeAccessExpiry is the DESKTOP CLIENT session JWT TTL. Employees send it
	// in sync request bodies (no cookie), so it stays long-lived.
	EmployeeAccessExpiry time.Duration
}

type RedisConfig struct {
	Host     string
	Port     int
	Password string
	DB       int
}

type AdminConfig struct {
	Email    string
	Password string
	Name     string
}

type CORSConfig struct {
	AllowedOrigins []string
}

func (c *Config) ServerAddr() string {
	return fmt.Sprintf("%s:%d", c.Server.Host, c.Server.Port)
}

// TLSEnabled is true when both cert and key paths are configured.
func (c *Config) TLSEnabled() bool {
	return strings.TrimSpace(c.Server.TLSCertFile) != "" && strings.TrimSpace(c.Server.TLSKeyFile) != ""
}

func (c *DatabaseConfig) DSN() string {
	return fmt.Sprintf(
		"postgres://%s:%s@%s:%d/%s?sslmode=%s",
		c.User, c.Password, c.Host, c.Port, c.Name, c.SSLMode,
	)
}

// Load reads configuration from environment variables (and .env file if present).
func Load() (*Config, error) {
	// Load .env file if it exists (don't fail if it doesn't)
	_ = godotenv.Load()

	tlsCertFile := strings.TrimSpace(getEnv("TLS_CERT_FILE", ""))
	tlsKeyFile := strings.TrimSpace(getEnv("TLS_KEY_FILE", ""))
	cookieSecure := getEnvBool("COOKIE_SECURE", tlsCertFile != "" && tlsKeyFile != "")

	cfg := &Config{
		Server: ServerConfig{
			Host:              getEnv("SERVER_HOST", "0.0.0.0"),
			Port:              getEnvInt("SERVER_PORT", 8080),
			InstanceID:        strings.TrimSpace(getEnv("INSTANCE_ID", "")),
			InstancePublicURL: strings.TrimRight(strings.TrimSpace(getEnv("INSTANCE_PUBLIC_URL", "")), "/"),
			TLSCertFile:       tlsCertFile,
			TLSKeyFile:        tlsKeyFile,
			CookieSecure:      cookieSecure,
		},
		Database: DatabaseConfig{
			Host:            getEnv("DB_HOST", "localhost"),
			Port:            getEnvInt("DB_PORT", 5432),
			User:            getEnv("DB_USER", "alpha_ai"),
			Password:        getEnv("DB_PASSWORD", ""),
			Name:            getEnv("DB_NAME", "alpha_ai_tracker"),
			SSLMode:         getEnv("DB_SSLMODE", "disable"),
			MaxOpenConns:    getEnvInt("DB_MAX_OPEN_CONNS", 25),
			MaxIdleConns:    getEnvInt("DB_MAX_IDLE_CONNS", 10),
			ConnMaxLifetime: getEnvDuration("DB_CONN_MAX_LIFETIME", 5*time.Minute),
		},
		Redis: RedisConfig{
			Host:     getEnv("REDIS_HOST", "localhost"),
			Port:     getEnvInt("REDIS_PORT", 6379),
			Password: getEnv("REDIS_PASSWORD", ""),
			DB:       getEnvInt("REDIS_DB", 0),
		},
		JWT: JWTConfig{
			Secret:               getEnv("JWT_SECRET", ""),
			AccessExpiry:         getEnvDuration("JWT_ACCESS_EXPIRY", 15*time.Minute),
			RefreshExpiry:        getEnvDuration("JWT_REFRESH_EXPIRY", 30*24*time.Hour),
			EmployeeAccessExpiry: getEnvDuration("JWT_EMPLOYEE_ACCESS_EXPIRY", 24*time.Hour),
		},
		Admin: AdminConfig{
			Email:    getEnv("ADMIN_EMAIL", "admin@alphai.com"),
			Password: getEnv("ADMIN_PASSWORD", "AlphaAI@2024!"),
			Name:     getEnv("ADMIN_NAME", "Company Admin"),
		},
		CORS: CORSConfig{
			AllowedOrigins: getEnvSlice("CORS_ALLOWED_ORIGINS", []string{"http://localhost:3000"}),
		},
		LogLevel: getEnv("LOG_LEVEL", "info"),

		LinkStaleDays: getEnvInt("LINK_STALE_DAYS", 7),

		DefaultShiftTimezone: strings.TrimSpace(getEnv("DEFAULT_SHIFT_TIMEZONE", "")),

		DlpRemovalCodeTTLMin: getEnvInt("DLP_REMOVAL_CODE_TTL_MIN", 60),

		LiveStream: LiveStreamConfig{
			Enabled:                getEnvBool("LIVE_STREAM_ENABLED", true),
			MaxStreams:             getEnvInt("LIVE_STREAM_MAX_STREAMS", 100),
			MaxWatchersPerEmployee: getEnvInt("LIVE_STREAM_MAX_WATCHERS_PER_EMPLOYEE", 10),
			IdleSec:                getEnvInt("LIVE_STREAM_IDLE_SEC", 90),
			MaxBitrateKbps:         getEnvInt("WEBRTC_MAX_BITRATE_KBPS", 8000),
			STUNURLs:               getEnvSlice("WEBRTC_STUN_URLS", []string{"stun:stun.l.google.com:19302"}),
			TURNURLs:               getEnvSlice("WEBRTC_TURN_URLS", []string{}),
			TURNUser:               getEnv("WEBRTC_TURN_USER", ""),
			TURNPass:               getEnv("WEBRTC_TURN_PASS", ""),
		},
		PresenceWS: PresenceWSConfig{
			Enabled:        getEnvBool("PRESENCE_WS_ENABLED", true),
			MaxConnections: getEnvInt("PRESENCE_WS_MAX_CONNECTIONS", 10000),
		},

		Releases: ReleasesConfig{
			Dir:                    strings.TrimSpace(getEnv("RELEASES_DIR", "setup")),
			MaxConcurrentDownloads: getEnvInt("RELEASE_DOWNLOAD_MAX_CONCURRENT", 25),
			DownloadRetryAfterSec:  getEnvInt("RELEASE_DOWNLOAD_RETRY_AFTER_SEC", 60),
		},

		Screenshots: ScreenshotsConfig{
			Dir:            strings.TrimSpace(getEnv("SCREENSHOTS_DIR", "media/screenshots")),
			MaxUploadBytes: getEnvInt64("SCREENSHOTS_MAX_UPLOAD_BYTES", 2<<20),
		},

		Face: FaceConfig{
			Dir:                strings.TrimSpace(getEnv("FACE_MEDIA_DIR", "media/faces")),
			MaxUploadBytes:     getEnvInt64("FACE_MAX_UPLOAD_BYTES", 3<<20),
			MaxFilesPerJob:     getEnvInt("FACE_MAX_FILES_PER_JOB", 16),
			WorkerAPIKey:       strings.TrimSpace(getEnv("FACE_WORKER_API_KEY", "")),
			WorkerURL:          strings.TrimRight(strings.TrimSpace(getEnv("FACE_WORKER_URL", "http://127.0.0.1:8010")), "/"),
			EmbeddingKeySecret: strings.TrimSpace(getEnv("FACE_EMBEDDING_KEY", "")),
			ModelsDir:             strings.TrimSpace(getEnv("FACE_MODELS_DIR", "face_worker/models")),
			BuffaloDetPackagePath: strings.TrimSpace(getEnv("FACE_BUFFALO_DET_ONNX", "face_worker/models/det_10g.onnx")),
			BuffaloRecPackagePath: strings.TrimSpace(getEnv("FACE_BUFFALO_REC_ONNX", "face_worker/models/w600k_r50.onnx")),
		},

		Scale: ScaleConfig{
			AsynqEnabled:          getEnvBool("ASYNQ_ENABLED", true),
			AsynqEmbed:            getEnvBool("ASYNQ_EMBED", true),
			AsynqConcurrency:      getEnvInt("ASYNQ_CONCURRENCY", 8),
			SyncMaxConcurrent:     getEnvInt("SYNC_MAX_CONCURRENT", 0), // 0 → derive from DB pool
			LoginRPS:              getEnvFloat("RATE_LIMIT_LOGIN_RPS", 2),
			LoginBurst:            getEnvInt("RATE_LIMIT_LOGIN_BURST", 5),
			SyncRPS:               getEnvFloat("RATE_LIMIT_SYNC_RPS", 30),
			SyncBurst:             getEnvInt("RATE_LIMIT_SYNC_BURST", 60),
			ScreenshotUploadRPS:   getEnvFloat("RATE_LIMIT_SCREENSHOT_RPS", 40),
			ScreenshotUploadBurst: getEnvInt("RATE_LIMIT_SCREENSHOT_BURST", 80),
			AdminSearchRPS:        getEnvFloat("RATE_LIMIT_ADMIN_RPS", 20),
			AdminSearchBurst:      getEnvInt("RATE_LIMIT_ADMIN_BURST", 40),
			DashboardCacheTTLSec:  getEnvInt("DASHBOARD_CACHE_TTL_SEC", 20),
			SessionSweepSec:       getEnvInt("SESSION_SWEEP_INTERVAL_SECONDS", 60),
		},
	}

	if cfg.Scale.SyncMaxConcurrent <= 0 {
		cfg.Scale.SyncMaxConcurrent = int(syncMaxFromPool(cfg.Database.MaxOpenConns))
	}
	if cfg.Scale.AsynqConcurrency < 1 {
		cfg.Scale.AsynqConcurrency = 4
	}
	if cfg.LiveStream.MaxStreams < 1 {
		cfg.LiveStream.MaxStreams = 100
	}

	if cfg.Database.Password == "" {
		return nil, fmt.Errorf("DB_PASSWORD is required")
	}
	if cfg.JWT.Secret == "" {
		return nil, fmt.Errorf("JWT_SECRET is required")
	}
	if cfg.Face.EmbeddingKeySecret == "" {
		cfg.Face.EmbeddingKeySecret = cfg.JWT.Secret
	}

	return cfg, nil
}

func syncMaxFromPool(maxOpenConns int) int64 {
	if maxOpenConns < 4 {
		return 2
	}
	n := int64(maxOpenConns) * 3 / 5
	if n < 2 {
		return 2
	}
	return n
}

func getEnvFloat(key string, fallback float64) float64 {
	if v := os.Getenv(key); v != "" {
		if f, err := strconv.ParseFloat(v, 64); err == nil {
			return f
		}
	}
	return fallback
}

func getEnv(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func getEnvInt(key string, fallback int) int {
	if v := os.Getenv(key); v != "" {
		if i, err := strconv.Atoi(v); err == nil {
			return i
		}
	}
	return fallback
}

func getEnvInt64(key string, fallback int64) int64 {
	if v := os.Getenv(key); v != "" {
		if i, err := strconv.ParseInt(v, 10, 64); err == nil {
			return i
		}
	}
	return fallback
}

func getEnvBool(key string, fallback bool) bool {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	switch strings.ToLower(strings.TrimSpace(v)) {
	case "1", "true", "yes", "on":
		return true
	case "0", "false", "no", "off":
		return false
	default:
		return fallback
	}
}

func getEnvDuration(key string, fallback time.Duration) time.Duration {
	if v := os.Getenv(key); v != "" {
		if d, err := time.ParseDuration(v); err == nil {
			return d
		}
	}
	return fallback
}

func getEnvSlice(key string, fallback []string) []string {
	if v := os.Getenv(key); v != "" {
		parts := splitAndTrim(v, ",")
		if len(parts) > 0 {
			return parts
		}
	}
	return fallback
}

func splitAndTrim(s, sep string) []string {
	var result []string
	for _, p := range split(s, sep) {
		p = trimSpace(p)
		if p != "" {
			result = append(result, p)
		}
	}
	return result
}

// Simple helpers to avoid importing strings package in config
func split(s, sep string) []string {
	var result []string
	start := 0
	for i := 0; i < len(s)-len(sep)+1; i++ {
		if s[i:i+len(sep)] == sep {
			result = append(result, s[start:i])
			start = i + len(sep)
		}
	}
	result = append(result, s[start:])
	return result
}

func trimSpace(s string) string {
	start, end := 0, len(s)
	for start < end && (s[start] == ' ' || s[start] == '\t' || s[start] == '\n' || s[start] == '\r') {
		start++
	}
	for end > start && (s[end-1] == ' ' || s[end-1] == '\t' || s[end-1] == '\n' || s[end-1] == '\r') {
		end--
	}
	return s[start:end]
}
