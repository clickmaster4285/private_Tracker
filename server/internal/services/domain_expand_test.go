package services

import (
	"testing"
)

func TestExpandRelatedDomainsYouTube(t *testing.T) {
	got := ExpandRelatedDomains([]string{"youtube.com"})
	need := []string{
		"youtube.com", "www.youtube.com", "m.youtube.com",
		"youtu.be", "googlevideo.com", "youtube-nocookie.com",
	}
	have := map[string]bool{}
	for _, d := range got {
		have[d] = true
	}
	for _, d := range need {
		if !have[d] {
			t.Fatalf("missing %q in %v", d, got)
		}
	}
}

func TestExpandRelatedDomainsUnknownGetsWwwM(t *testing.T) {
	got := ExpandRelatedDomains([]string{"acme-widgets.example"})
	have := map[string]bool{}
	for _, d := range got {
		have[d] = true
	}
	for _, d := range []string{"acme-widgets.example", "www.acme-widgets.example", "m.acme-widgets.example"} {
		if !have[d] {
			t.Fatalf("missing %q in %v", d, got)
		}
	}
}

func TestSuggestRelatedDomainsOmitsInput(t *testing.T) {
	extra := SuggestRelatedDomains([]string{"youtube.com", "www.youtube.com"})
	for _, d := range extra {
		if d == "youtube.com" || d == "www.youtube.com" {
			t.Fatalf("suggested should omit input domain %q", d)
		}
	}
	if len(extra) == 0 {
		t.Fatal("expected curated suggestions for youtube")
	}
}

func TestNormalizeDomain(t *testing.T) {
	if got := NormalizeDomain("HTTPS://WWW.Example.COM/path?q=1"); got != "www.example.com" {
		t.Fatalf("got %q", got)
	}
	if got := NormalizeDomain("example.com:443"); got != "example.com" {
		t.Fatalf("got %q", got)
	}
}

func TestDomainApexMultiPart(t *testing.T) {
	if got := DomainApex("shop.example.co.uk"); got != "example.co.uk" {
		t.Fatalf("got %q", got)
	}
	if got := DomainApex("www.youtube.com"); got != "youtube.com" {
		t.Fatalf("got %q", got)
	}
}
