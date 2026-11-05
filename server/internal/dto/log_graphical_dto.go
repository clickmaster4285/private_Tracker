package dto

// LogGraphicalResponse is the JWT-protected chart payload for /logs/graphical.
type LogGraphicalResponse struct {
	Range       DashboardRange            `json:"range"`
	Bucket      string                    `json:"bucket"` // hour | day
	Summary     LogGraphicalSummary       `json:"summary"`
	Activity    []LogGraphicalActivityPt  `json:"activity"`
	Productivity []LogGraphicalProductivityPt `json:"productivity"`
	TopApps     []DashboardTopApp         `json:"topApps"`
	TopDomains  []DashboardTopDomain      `json:"topDomains"`
}

type LogGraphicalSummary struct {
	Sessions            int   `json:"sessions"`
	WebPages            int   `json:"webPages"`
	IdleEvents          int   `json:"idleEvents"`
	TotalSeconds        int64 `json:"totalSeconds"`
	ProductiveSeconds   int64 `json:"productiveSeconds"`
	UnproductiveSeconds int64 `json:"unproductiveSeconds"`
	NeutralSeconds      int64 `json:"neutralSeconds"`
}

type LogGraphicalActivityPt struct {
	Bucket    string `json:"bucket"` // display label
	At        string `json:"at"`     // RFC3339 bucket start
	Sessions  int    `json:"sessions"`
	WebPages  int    `json:"webPages"`
	IdleEvents int   `json:"idleEvents"`
}

type LogGraphicalProductivityPt struct {
	Bucket              string `json:"bucket"`
	At                  string `json:"at"`
	ProductiveSeconds   int64  `json:"productiveSeconds"`
	UnproductiveSeconds int64  `json:"unproductiveSeconds"`
	NeutralSeconds      int64  `json:"neutralSeconds"`
}
