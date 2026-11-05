package dto

import (
	"encoding/json"
	"testing"
	"time"
)

func TestEvidenceMap_NestedArray(t *testing.T) {
	raw := []byte(`{"action":"hosts_dns_sink","domains":["youtube.com","www.youtube.com"],"count":2}`)
	var m EvidenceMap
	if err := json.Unmarshal(raw, &m); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if m["action"] != "hosts_dns_sink" {
		t.Fatalf("action=%q", m["action"])
	}
	if m["domains"] != `["youtube.com","www.youtube.com"]` {
		t.Fatalf("domains=%q", m["domains"])
	}
	if m["count"] != "2" {
		t.Fatalf("count=%q", m["count"])
	}
}

func TestEvidenceMap_FlatStrings(t *testing.T) {
	raw := []byte(`{"action":"hosts_dns_sink","domains":"youtube.com,www.youtube.com"}`)
	var m EvidenceMap
	if err := json.Unmarshal(raw, &m); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if m["domains"] != "youtube.com,www.youtube.com" {
		t.Fatalf("domains=%q", m["domains"])
	}
}

func TestFlexibleTime_Zoneless(t *testing.T) {
	var ft FlexibleTime
	if err := json.Unmarshal([]byte(`"2026-10-23T08:37:22.1234567"`), &ft); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if ft.Time.IsZero() || ft.Time.Location() != time.UTC {
		t.Fatalf("got %v", ft.Time)
	}
}

func TestSyncDlpAlertsRequest_NestedEvidenceBinds(t *testing.T) {
	body := []byte(`{
	  "alerts":[{
	    "id":"11111111-1111-1111-1111-111111111111",
	    "policyVersion":26,
	    "type":"web_navigate",
	    "severity":"high",
	    "actionTaken":"blocked",
	    "occurredAt":"2026-10-23T08:37:22.1234567",
	    "evidence":{"action":"hosts_dns_sink","domains":["youtube.com"]}
	  }]
	}`)
	var req SyncDlpAlertsRequest
	if err := json.Unmarshal(body, &req); err != nil {
		t.Fatalf("bind would fail with: %v", err)
	}
	if len(req.Alerts) != 1 {
		t.Fatalf("alerts=%d", len(req.Alerts))
	}
	if req.Alerts[0].Evidence["domains"] != `["youtube.com"]` {
		t.Fatalf("evidence domains=%q", req.Alerts[0].Evidence["domains"])
	}
}
