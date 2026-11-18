package services

import (
	"encoding/json"
	"testing"
)

func TestValidateCompleteBundle_RequiresAllFields(t *testing.T) {
	incomplete := json.RawMessage(`{"policyVersion":1,"rules":[]}`)
	if err := validateCompleteBundle(incomplete); err == nil {
		t.Fatal("expected incomplete bundle to fail")
	}

	complete := json.RawMessage(`{
		"policyVersion": 1,
		"lockdown": true,
		"platformAllow": {"hosts": []},
		"allow": {"hosts": [], "apps": []},
		"rules": [],
		"shadowDeny": {"domains": []},
		"deviceControl": []
	}`)
	if err := validateCompleteBundle(complete); err != nil {
		t.Fatalf("complete bundle rejected: %v", err)
	}
}

func TestValidateCompleteBundle_RejectsEmptyObject(t *testing.T) {
	if err := validateCompleteBundle(json.RawMessage(`{}`)); err == nil {
		t.Fatal("empty object must fail")
	}
}
