package services

import (
	"slices"
	"testing"
)

func TestNormalizeAllowHost_KeepsExplicitPort(t *testing.T) {
	cases := map[string]string{
		"192.168.88.35":                    "192.168.88.35",
		"http://192.168.88.35:3000/login":  "192.168.88.35:3000",
		"Intranet.Local:8080":              "intranet.local:8080",
		"https://contoso.sharepoint.com/x": "contoso.sharepoint.com",
		"192.168.88.35:443":                "192.168.88.35",
		"192.168.88.35:80":                 "192.168.88.35",
		"[fd00::1]:8443":                   "[fd00::1]:8443",
	}
	for in, want := range cases {
		if got := NormalizeAllowHost(in); got != want {
			t.Errorf("NormalizeAllowHost(%q)=%q want %q", in, got, want)
		}
	}
}

func TestExpandRelatedDomains_DoesNotMangleIpOrPort(t *testing.T) {
	got := ExpandRelatedDomains([]string{"192.168.88.35", "192.168.88.35:3000"})
	want := []string{"192.168.88.35", "192.168.88.35:3000"}
	if !slices.Equal(got, want) {
		t.Fatalf("got %v want %v", got, want)
	}
	for _, bad := range []string{"88.35", "www.88.35", "m.88.35"} {
		if slices.Contains(got, bad) {
			t.Fatalf("IP literal expanded into %q", bad)
		}
	}
}
