package services

import (
	"net"
	"net/url"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/config"
)

// CompilePlatformAllowHosts builds the read-only platform host list from server
// public config (API / TURN / STUN). Snapshotted into each lockdown publish.
func CompilePlatformAllowHosts(cfg *config.Config) []string {
	if cfg == nil {
		return []string{}
	}
	seen := make(map[string]struct{})
	out := make([]string, 0, 8)
	add := func(raw string) {
		host := hostFromURLOrHost(raw)
		if host == "" {
			return
		}
		if _, ok := seen[host]; ok {
			return
		}
		seen[host] = struct{}{}
		out = append(out, host)
	}
	add(cfg.Server.InstancePublicURL)
	for _, u := range cfg.LiveStream.TURNURLs {
		add(u)
	}
	for _, u := range cfg.LiveStream.STUNURLs {
		add(u)
	}
	return out
}

func hostFromURLOrHost(raw string) string {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return ""
	}
	lower := strings.ToLower(raw)
	// stun:host:port / turn:host:port / turns:…
	for _, scheme := range []string{"stuns:", "turns:", "stun:", "turn:"} {
		if strings.HasPrefix(lower, scheme) {
			raw = raw[len(scheme):]
			break
		}
	}
	if strings.Contains(raw, "://") {
		if u, err := url.Parse(raw); err == nil {
			raw = u.Host
		}
	}
	if host, _, err := net.SplitHostPort(raw); err == nil {
		raw = host
	} else if i := strings.IndexByte(raw, ':'); i > 0 && !strings.Contains(raw, "]") {
		// host:port without brackets
		raw = raw[:i]
	}
	return NormalizeDomain(raw)
}
