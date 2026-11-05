package dto

// DashboardSummaryResponse is the JWT-protected home-page aggregate payload.
// All arrays are server-bounded (topN / recentLimit). Live counts are integers
// only — the Online Now name list uses GET /live-stream/employees separately.
type DashboardSummaryResponse struct {
	Range           DashboardRange           `json:"range"`
	Employees       DashboardEmployees       `json:"employees"`
	Activity        DashboardActivity        `json:"activity"`
	Monitoring      DashboardMonitoring      `json:"monitoring"`
	Devices         DashboardDevices         `json:"devices"`
	Attendance      DashboardAttendance      `json:"attendance"`
	Live            DashboardLive            `json:"live"`
	TopApps         []DashboardTopApp        `json:"topApps"`
	TopDomains      []DashboardTopDomain     `json:"topDomains"`
	RecentSessions  []DashboardRecentSession `json:"recentSessions"`
}

type DashboardRange struct {
	From string `json:"from"`
	To   string `json:"to"`
}

type DashboardEmployees struct {
	Total     int `json:"total"`
	Tracked   int `json:"tracked"`
	Untracked int `json:"untracked"`
}

type DashboardActivity struct {
	Sessions      int `json:"sessions"`
	WebPages      int `json:"webPages"`
	OpenSessions  int `json:"openSessions"`
	StaleSessions int `json:"staleSessions"`
}

type DashboardMonitoring struct {
	UnclassifiedApps  int `json:"unclassifiedApps"`
	UnclassifiedSites int `json:"unclassifiedSites"`
}

type DashboardDevices struct {
	Active   int                    `json:"active"`
	Seen15m  int                    `json:"seen15m"`
	Seen24h  int                    `json:"seen24h"`
	Stale7d  int                    `json:"stale7d"`
	Versions []DashboardVersionRow  `json:"versions"`
}

type DashboardVersionRow struct {
	Version string `json:"version"`
	Count   int    `json:"count"`
}

// DashboardAttendance is today's attendance status breakdown across the
// department-filtered employee set. The day is resolved per employee from their
// shift timezone (same rule as GET /attendance/today), so Date is the server's
// reference date only.
type DashboardAttendance struct {
	Date     string `json:"date"`
	Total    int    `json:"total"`
	Present  int    `json:"present"`
	Late     int    `json:"late"`
	Absent   int    `json:"absent"`
	HalfDay  int    `json:"halfDay"`
	OffShift int    `json:"offShift"`
	Unknown  int    `json:"unknown"`
}

type DashboardLive struct {
	Online             int  `json:"online"`
	Streaming          int  `json:"streaming"`
	ConsentMissing     int  `json:"consentMissing"`
	PresenceAvailable  bool `json:"presenceAvailable"`
}

type DashboardTopApp struct {
	AppDisplayName string `json:"appDisplayName"`
	ProcessName    string `json:"processName"`
	SessionCount   int    `json:"sessionCount"`
	OpenNow        int    `json:"openNow"`
}

type DashboardTopDomain struct {
	Domain string `json:"domain"`
	Visits int    `json:"visits"`
}

type DashboardRecentSession struct {
	ID             string  `json:"id"`
	EmployeeID     string  `json:"employeeId"`
	EmployeeName   string  `json:"employeeName"`
	AppDisplayName string  `json:"appDisplayName"`
	ProcessName    string  `json:"processName"`
	Status         string  `json:"status"`
	StartedAt      string  `json:"startedAt"`
	EndedAt        *string `json:"endedAt"`
	LastSyncAt     *string `json:"lastSyncAt"`
}
