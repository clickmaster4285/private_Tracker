package services

import (
	"net"
	"sort"
	"strings"
)

// NormalizeDomain strips scheme/path/port and lowercases a host for hosts-file matching.
// Keeps www./m. prefixes — those are distinct block entries.
func NormalizeDomain(raw string) string {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return ""
	}
	lower := strings.ToLower(raw)
	switch {
	case strings.HasPrefix(lower, "https://"):
		raw = raw[len("https://"):]
	case strings.HasPrefix(lower, "http://"):
		raw = raw[len("http://"):]
	case strings.HasPrefix(lower, "//"):
		raw = raw[2:]
	}
	if idx := strings.IndexAny(raw, "/?#"); idx >= 0 {
		raw = raw[:idx]
	}
	if host, _, err := net.SplitHostPort(raw); err == nil {
		raw = host
	}
	raw = strings.TrimSuffix(strings.TrimSpace(raw), ".")
	return strings.ToLower(raw)
}

// Curated related-domain packs keyed by registrable apex.
// Structural www./m. are always added when ExpandRelated is on; these packs
// cover CDNs / alternate apexes that hosts-file blocking must list explicitly
// (e.g. YouTube video CDN is googlevideo.com, not a youtube.com subdomain).
var relatedDomainPacks = map[string][]string{
	"youtube.com": {
		"www.youtube.com", "m.youtube.com", "youtu.be", "youtube-nocookie.com",
		"www.youtube-nocookie.com", "googlevideo.com", "ytimg.com", "yt.be",
	},
	"youtu.be": {
		"youtube.com", "www.youtube.com", "m.youtube.com", "youtube-nocookie.com",
		"googlevideo.com", "ytimg.com",
	},
	"google.com": {
		"www.google.com", "m.google.com", "googleusercontent.com", "gstatic.com",
		"googleapis.com", "ggpht.com",
	},
	"facebook.com": {
		"www.facebook.com", "m.facebook.com", "fb.com", "www.fb.com",
		"fbcdn.net", "facebook.net",
	},
	"instagram.com": {
		"www.instagram.com", "cdninstagram.com", "fbcdn.net",
	},
	"twitter.com": {
		"www.twitter.com", "mobile.twitter.com", "x.com", "www.x.com",
		"t.co", "twimg.com",
	},
	"x.com": {
		"twitter.com", "www.twitter.com", "mobile.twitter.com", "t.co", "twimg.com",
	},
	"tiktok.com": {
		"www.tiktok.com", "m.tiktok.com", "tiktokv.com", "tiktokcdn.com",
		"musical.ly",
	},
	"reddit.com": {
		"www.reddit.com", "old.reddit.com", "redd.it", "redditmedia.com",
		"redditstatic.com",
	},
	"netflix.com": {
		"www.netflix.com", "nflxvideo.net", "nflximg.net", "nflxso.net",
		"nflxext.com",
	},
	"twitch.tv": {
		"www.twitch.tv", "m.twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net",
	},
	"discord.com": {
		"www.discord.com", "discordapp.com", "discord.gg", "discordapp.net",
	},
	"spotify.com": {
		"www.spotify.com", "open.spotify.com", "scdn.co", "spotifycdn.com",
	},
	"amazon.com": {
		"www.amazon.com", "amazon.co.uk", "ssl-images-amazon.com",
		"media-amazon.com", "images-amazon.com",
	},
	"wikipedia.org": {
		"www.wikipedia.org", "en.wikipedia.org", "m.wikipedia.org", "wikimedia.org",
	},
	"linkedin.com": {
		"www.linkedin.com", "m.linkedin.com", "licdn.com",
	},
	"pinterest.com": {
		"www.pinterest.com", "pin.it", "pinimg.com",
	},
	"github.com": {
		"www.github.com", "gist.github.com", "githubusercontent.com", "github.io",
	},
	"chatgpt.com": {
		"www.chatgpt.com", "chat.openai.com", "openai.com", "oaiusercontent.com",
	},
	"openai.com": {
		"www.openai.com", "chat.openai.com", "chatgpt.com", "oaiusercontent.com",
	},
	"deepseek.com": {
		"www.deepseek.com", "chat.deepseek.com",
	},
	"whatsapp.com": {
		"www.whatsapp.com", "web.whatsapp.com", "wa.me", "whatsapp.net",
	},
	"telegram.org": {
		"www.telegram.org", "web.telegram.org", "t.me", "telegram.me",
	},
	"snapchat.com": {
		"www.snapchat.com", "snapkit.com", "sc-cdn.net",
	},
	"vimeo.com": {
		"www.vimeo.com", "player.vimeo.com", "vimeocdn.com",
	},
	"dailymotion.com": {
		"www.dailymotion.com", "dmcdn.net",
	},
	"bing.com": {
		"www.bing.com", "cn.bing.com",
	},
	"yahoo.com": {
		"www.yahoo.com", "m.yahoo.com", "yimg.com",
	},
	"duckduckgo.com": {
		"www.duckduckgo.com", "duck.com",
	},
	"microsoft.com": {
		"www.microsoft.com", "live.com", "office.com", "onedrive.com",
		"msn.com", "azure.com",
	},
	"apple.com": {
		"www.apple.com", "icloud.com", "apple-cloudkit.com",
	},
}

// ExpandRelatedDomains returns the input domains plus structural subdomains
// (www., m.) and any curated CDN / alternate-apex pack for known sites.
// Unknown sites still get www. and m. variants of their apex — hosts cannot
// invent arbitrary CDNs without a curated pack.
func ExpandRelatedDomains(domains []string) []string {
	seen := map[string]struct{}{}
	var out []string
	add := func(d string) {
		d = NormalizeDomain(d)
		if d == "" {
			return
		}
		if _, ok := seen[d]; ok {
			return
		}
		seen[d] = struct{}{}
		out = append(out, d)
	}

	for _, raw := range domains {
		d := NormalizeDomain(raw)
		if d == "" {
			continue
		}
		add(d)
		apex := DomainApex(d)
		add(apex)
		add("www." + apex)
		add("m." + apex)

		if pack, ok := relatedDomainPacks[apex]; ok {
			for _, p := range pack {
				add(p)
			}
		}
		// User typed an alternate apex that belongs to a curated pack (e.g. youtu.be).
		for key, pack := range relatedDomainPacks {
			if apex == key {
				continue
			}
			matched := false
			for _, p := range pack {
				if NormalizeDomain(p) == d || DomainApex(p) == apex {
					matched = true
					break
				}
			}
			if !matched {
				continue
			}
			add(key)
			add("www." + key)
			add("m." + key)
			for _, x := range pack {
				add(x)
			}
		}
	}

	sort.Strings(out)
	return out
}

// DomainApex returns a best-effort registrable domain (last two labels).
// Multi-part public suffixes (co.uk, com.au, …) keep three labels.
func DomainApex(host string) string {
	host = NormalizeDomain(host)
	if host == "" {
		return ""
	}
	parts := strings.Split(host, ".")
	if len(parts) <= 2 {
		return host
	}
	last2 := parts[len(parts)-2] + "." + parts[len(parts)-1]
	if isMultiPartPublicSuffix(last2) && len(parts) >= 3 {
		return parts[len(parts)-3] + "." + last2
	}
	return last2
}

func isMultiPartPublicSuffix(s string) bool {
	switch s {
	case "co.uk", "org.uk", "ac.uk", "gov.uk",
		"com.au", "net.au", "org.au",
		"co.nz", "co.jp", "co.kr", "com.br", "com.mx",
		"co.in", "com.cn", "com.tw", "com.hk":
		return true
	default:
		return false
	}
}

// SuggestRelatedDomains is the same as ExpandRelatedDomains but returns only
// domains that were NOT already in the input (for the UI preview).
func SuggestRelatedDomains(domains []string) []string {
	have := map[string]struct{}{}
	for _, d := range domains {
		n := NormalizeDomain(d)
		if n != "" {
			have[n] = struct{}{}
		}
	}
	expanded := ExpandRelatedDomains(domains)
	var extra []string
	for _, d := range expanded {
		if _, ok := have[d]; !ok {
			extra = append(extra, d)
		}
	}
	return extra
}
