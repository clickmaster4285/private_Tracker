package scale

import "testing"

func TestResolveInstanceID_configured(t *testing.T) {
	got := ResolveInstanceID("api-a")
	if got != "api-a" {
		t.Fatalf("got %q want api-a", got)
	}
}

func TestResolveInstanceID_auto(t *testing.T) {
	got := ResolveInstanceID("")
	if got == "" {
		t.Fatal("expected non-empty auto instance id")
	}
	if ResolveInstanceID("") == "api-a" {
		t.Fatal("auto id should not equal a configured name by chance in this assert")
	}
}
