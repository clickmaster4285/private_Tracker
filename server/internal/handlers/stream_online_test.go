package handlers

import "testing"

func TestEmployeeLiveOnline(t *testing.T) {
	t.Parallel()
	cases := []struct {
		name            string
		presenceEnabled bool
		wsConnected     bool
		hbOnline        bool
		want            bool
	}{
		{name: "presence on + ws up", presenceEnabled: true, wsConnected: true, hbOnline: false, want: true},
		{name: "presence on + ws down ignores heartbeat", presenceEnabled: true, wsConnected: false, hbOnline: true, want: false},
		{name: "presence on + both down", presenceEnabled: true, wsConnected: false, hbOnline: false, want: false},
		{name: "presence off + heartbeat fresh", presenceEnabled: false, wsConnected: false, hbOnline: true, want: true},
		{name: "presence off + heartbeat stale", presenceEnabled: false, wsConnected: false, hbOnline: false, want: false},
		{name: "presence off ignores stray ws flag", presenceEnabled: false, wsConnected: true, hbOnline: false, want: false},
	}
	for _, tc := range cases {
		tc := tc
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			got := employeeLiveOnline(tc.presenceEnabled, tc.wsConnected, tc.hbOnline)
			if got != tc.want {
				t.Fatalf("employeeLiveOnline(%v,%v,%v)=%v want %v",
					tc.presenceEnabled, tc.wsConnected, tc.hbOnline, got, tc.want)
			}
		})
	}
}
