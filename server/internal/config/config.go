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

	// GitHub configures the private-repo releases metadata + download bridge.
	// Empty Token disables public /releases API endpoints (503) without failing boot.
	GitHub GitHubConfig
}

// GitHubConfig holds read-only credentials for the private Releases API bridge.
type GitHubConfig struct {
	Owner      string
	Repo       string
	Token      string
	APIVersion string
	CacheTTL   time.Duration
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

	cfg := &Config{
		Server: ServerConfig{
			Host:              getEnv("SERVER_HOST", "0.0.0.0"),
			Port:              getEnvInt("SERVER_PORT", 8080),
			InstanceID:        strings.TrimSpace(getEnv("INSTANCE_ID", "")),
			InstancePublicURL: strings.TrimRight(strings.TrimSpace(getEnv("INSTANCE_PUBLIC_URL", "")), "/"),
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
			MaxStreams:             getEnvInt("LIVE_STREAM_MAX_STREAMS", 25),
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

		GitHub: GitHubConfig{
			Owner:      strings.TrimSpace(getEnv("GITHUB_OWNER", "clickmaster4285")),
			Repo:       strings.TrimSpace(getEnv("GITHUB_REPO", "Alpha-AI-Tracker")),
			Token:      strings.TrimSpace(getEnv("GITHUB_TOKEN", "")),
			APIVersion: strings.TrimSpace(getEnv("GITHUB_API_VERSION", "2022-11-28")),
			CacheTTL:   getEnvDuration("GITHUB_RELEASES_CACHE_TTL", 5*time.Minute),
		},
	}

	if cfg.Database.Password == "" {
		return nil, fmt.Errorf("DB_PASSWORD is required")
	}
	if cfg.JWT.Secret == "" {
		return nil, fmt.Errorf("JWT_SECRET is required")
	}

	return cfg, nil
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
