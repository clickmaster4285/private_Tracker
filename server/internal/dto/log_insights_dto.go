package dto

// LogInsightsResponse is the JWT-protected digest for /logs/insights.
// Highlights are rule-based (no LLM). Arrays are server-bounded.
type LogInsightsResponse struct {
	Range       DashboardRange           `json:"range"`
	Metrics     LogInsightsMetrics       `json:"metrics"`
	Productivity LogInsightsProductivity `json:"productivity"`
	TopApps     []DashboardTopApp        `json:"topApps"`
	TopDomains  []DashboardTopDomain     `json:"topDomains"`
	Outliers    []LogInsightsOutlier     `json:"outliers"`
	Highlights  []LogInsightsHighlight   `json:"highlights"`
}

type LogInsightsMetrics struct {
	Sessions         int `json:"sessions"`
	WebPages         int `json:"webPages"`
	ActiveEmployees  int `json:"activeEmployees"`
	OpenSessions     int `json:"openSessions"`
	StaleSessions    int `json:"staleSessions"`
	IdleEvents       int `json:"idleEvents"`
	EmployeesWithIdle int `json:"employeesWithIdle"`
}

type LogInsightsProductivity struct {
	TotalSeconds        int64 `json:"totalSeconds"`
	ProductiveSeconds   int64 `json:"productiveSeconds"`
	UnproductiveSeconds int64 `json:"unproductiveSeconds"`
	NeutralSeconds      int64 `json:"neutralSeconds"`
	ProductivePct       int   `json:"productivePct"`
	UnproductivePct     int   `json:"unproductivePct"`
	NeutralPct          int   `json:"neutralPct"`
}

type LogInsightsOutlier struct {
	EmployeeID   string `json:"employeeId"`
	EmployeeName string `json:"employeeName"`
	Kind         string `json:"kind"` // idle | sessions | web
	Value        int    `json:"value"`
	Label        string `json:"label"`
}

type LogInsightsHighlight struct {
	Severity string `json:"severity"` // info | warning | success
	Title    string `json:"title"`
	Detail   string `json:"detail"`
}
