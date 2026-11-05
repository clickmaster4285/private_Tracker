package services

import "testing"

func TestDashboardEmployeeLiveOnline(t *testing.T) {
	cases := []struct {
		presence, ws, hb, want bool
	}{
		{true, true, false, true},
		{true, false, true, false},
		{false, false, true, true},
		{false, false, false, false},
	}
	for _, c := range cases {
		got := dashboardEmployeeLiveOnline(c.presence, c.ws, c.hb)
		if got != c.want {
			t.Fatalf("presence=%v ws=%v hb=%v: got %v want %v", c.presence, c.ws, c.hb, got, c.want)
		}
	}
}

func TestSummaryTopNClamp(t *testing.T) {
	if dashboardDefaultTopN < 1 || dashboardMaxTopN < dashboardDefaultTopN {
		t.Fatal("invalid topN defaults")
	}
	if dashboardDefaultRecent < 1 || dashboardMaxRecent < dashboardDefaultRecent {
		t.Fatal("invalid recent defaults")
	}
}
