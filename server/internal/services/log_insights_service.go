package services

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"golang.org/x/sync/errgroup"
)

const (
	logInsightsDefaultTopN      = 8
	logInsightsMaxTopN          = 20
	logInsightsDefaultOutliers  = 5
	logInsightsMaxOutliers      = 10
)

// LogInsightsService builds the /logs/insights digest.
type LogInsightsService struct {
	repo *repository.LogInsightsRepo
}

// NewLogInsightsService constructs a LogInsightsService.
func NewLogInsightsService(repo *repository.LogInsightsRepo) *LogInsightsService {
	return &LogInsightsService{repo: repo}
}

// LogInsightsParams are validated filter inputs.
type LogInsightsParams struct {
	From         time.Time
	To           time.Time
	DepartmentID *int
	EmployeeID   string
	TopN         int
	OutlierLimit int
}

// GetInsights returns metrics, lists, and rule-based highlights.
func (s *LogInsightsService) GetInsights(ctx context.Context, p LogInsightsParams) (*dto.LogInsightsResponse, error) {
	if p.From.IsZero() || p.To.IsZero() {
		return nil, fmt.Errorf("from and to are required")
	}
	if !p.To.After(p.From) {
		return nil, fmt.Errorf("to must be after from")
	}
	if p.TopN < 1 {
		p.TopN = logInsightsDefaultTopN
	}
	if p.TopN > logInsightsMaxTopN {
		p.TopN = logInsightsMaxTopN
	}
	if p.OutlierLimit < 1 {
		p.OutlierLimit = logInsightsDefaultOutliers
	}
	if p.OutlierLimit > logInsightsMaxOutliers {
		p.OutlierLimit = logInsightsMaxOutliers
	}
	p.EmployeeID = strings.TrimSpace(p.EmployeeID)

	repoParams := repository.LogInsightsParams{
		From:         p.From,
		To:           p.To,
		DepartmentID: p.DepartmentID,
		EmployeeID:   p.EmployeeID,
		TopN:         p.TopN,
		OutlierLimit: p.OutlierLimit,
	}

	var (
		activity     repository.DashboardActivityCounts
		activeEmps   int
		productivity repository.LogInsightsProductivityRow
		idle         repository.LogInsightsIdleStats
		topApps      []repository.DashboardTopAppRow
		topDomains   []repository.DashboardTopDomainRow
		idleOutliers []repository.LogInsightsOutlierRow
		sessOutliers []repository.LogInsightsOutlierRow
	)

	g, gctx := errgroup.WithContext(ctx)
	g.Go(func() error {
		var err error
		activity, err = s.repo.ActivityCounts(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		activeEmps, err = s.repo.ActiveEmployees(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		productivity, err = s.repo.ProductivityTotals(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		idle, err = s.repo.IdleStats(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		topApps, err = s.repo.TopApps(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		topDomains, err = s.repo.TopDomains(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		idleOutliers, err = s.repo.TopIdleEmployees(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		sessOutliers, err = s.repo.TopSessionEmployees(gctx, repoParams)
		return err
	})
	if err := g.Wait(); err != nil {
		return nil, err
	}

	prod := dto.LogInsightsProductivity{
		TotalSeconds:        productivity.TotalSeconds,
		ProductiveSeconds:   productivity.ProductiveSeconds,
		UnproductiveSeconds: productivity.UnproductiveSeconds,
		NeutralSeconds:      productivity.NeutralSeconds,
	}
	if productivity.TotalSeconds > 0 {
		prod.ProductivePct = int((productivity.ProductiveSeconds * 100) / productivity.TotalSeconds)
		prod.UnproductivePct = int((productivity.UnproductiveSeconds * 100) / productivity.TotalSeconds)
		prod.NeutralPct = 100 - prod.ProductivePct - prod.UnproductivePct
		if prod.NeutralPct < 0 {
			prod.NeutralPct = 0
		}
	}

	apps := make([]dto.DashboardTopApp, 0, len(topApps))
	for _, a := range topApps {
		apps = append(apps, dto.DashboardTopApp{
			AppDisplayName: a.AppDisplayName,
			ProcessName:    a.ProcessName,
			SessionCount:   a.SessionCount,
			OpenNow:        a.OpenNow,
		})
	}
	domains := make([]dto.DashboardTopDomain, 0, len(topDomains))
	for _, d := range topDomains {
		domains = append(domains, dto.DashboardTopDomain{Domain: d.Domain, Visits: d.Visits})
	}

	outliers := make([]dto.LogInsightsOutlier, 0, len(idleOutliers)+len(sessOutliers))
	for _, o := range idleOutliers {
		if o.Value < 1 {
			continue
		}
		outliers = append(outliers, dto.LogInsightsOutlier{
			EmployeeID: o.EmployeeID, EmployeeName: o.EmployeeName,
			Kind: "idle", Value: o.Value,
			Label: fmt.Sprintf("%d idle starts", o.Value),
		})
	}
	for _, o := range sessOutliers {
		if o.Value < 1 {
			continue
		}
		// Skip if already listed as top idle to keep the digest short.
		dup := false
		for _, existing := range outliers {
			if existing.EmployeeID == o.EmployeeID && existing.Kind == "idle" {
				dup = true
				break
			}
		}
		if dup {
			continue
		}
		outliers = append(outliers, dto.LogInsightsOutlier{
			EmployeeID: o.EmployeeID, EmployeeName: o.EmployeeName,
			Kind: "sessions", Value: o.Value,
			Label: fmt.Sprintf("%d app sessions", o.Value),
		})
	}
	if len(outliers) > p.OutlierLimit {
		outliers = outliers[:p.OutlierLimit]
	}

	metrics := dto.LogInsightsMetrics{
		Sessions:          activity.Sessions,
		WebPages:          activity.WebPages,
		ActiveEmployees:   activeEmps,
		OpenSessions:      activity.OpenSessions,
		StaleSessions:     activity.StaleSessions,
		IdleEvents:        idle.IdleEvents,
		EmployeesWithIdle: idle.EmployeesWithIdle,
	}

	return &dto.LogInsightsResponse{
		Range: dto.DashboardRange{
			From: p.From.UTC().Format(time.RFC3339),
			To:   p.To.UTC().Format(time.RFC3339),
		},
		Metrics:      metrics,
		Productivity: prod,
		TopApps:      apps,
		TopDomains:   domains,
		Outliers:     outliers,
		Highlights:   buildLogInsightsHighlights(metrics, prod, apps, domains, idleOutliers),
	}, nil
}

func buildLogInsightsHighlights(
	m dto.LogInsightsMetrics,
	p dto.LogInsightsProductivity,
	apps []dto.DashboardTopApp,
	domains []dto.DashboardTopDomain,
	idleOutliers []repository.LogInsightsOutlierRow,
) []dto.LogInsightsHighlight {
	out := make([]dto.LogInsightsHighlight, 0, 8)

	if m.Sessions == 0 && m.WebPages == 0 {
		out = append(out, dto.LogInsightsHighlight{
			Severity: "info",
			Title:    "No activity in this range",
			Detail:   "No app sessions or web pages were recorded. Try a wider date range or check that clients are syncing.",
		})
		return out
	}

	out = append(out, dto.LogInsightsHighlight{
		Severity: "info",
		Title:    "Activity volume",
		Detail: fmt.Sprintf(
			"%d app sessions and %d web pages across %d active employee%s.",
			m.Sessions, m.WebPages, m.ActiveEmployees, plural(m.ActiveEmployees),
		),
	})

	if p.TotalSeconds > 0 {
		sev := "success"
		if p.UnproductivePct >= 30 {
			sev = "warning"
		}
		out = append(out, dto.LogInsightsHighlight{
			Severity: sev,
			Title:    "Productivity mix",
			Detail: fmt.Sprintf(
				"%d%% productive · %d%% unproductive · %d%% neutral (classified app time).",
				p.ProductivePct, p.UnproductivePct, p.NeutralPct,
			),
		})
	}

	if len(apps) > 0 {
		out = append(out, dto.LogInsightsHighlight{
			Severity: "info",
			Title:    "Top application",
			Detail: fmt.Sprintf(
				"%s led with %d session%s.",
				apps[0].AppDisplayName, apps[0].SessionCount, plural(apps[0].SessionCount),
			),
		})
	}

	if len(domains) > 0 {
		out = append(out, dto.LogInsightsHighlight{
			Severity: "info",
			Title:    "Top website",
			Detail: fmt.Sprintf(
				"%s had %d visit%s.",
				domains[0].Domain, domains[0].Visits, plural(domains[0].Visits),
			),
		})
	}

	if m.IdleEvents > 0 {
		sev := "info"
		if m.EmployeesWithIdle >= 3 || m.IdleEvents >= 20 {
			sev = "warning"
		}
		detail := fmt.Sprintf(
			"%d idle start%s across %d employee%s.",
			m.IdleEvents, plural(m.IdleEvents), m.EmployeesWithIdle, plural(m.EmployeesWithIdle),
		)
		if len(idleOutliers) > 0 && idleOutliers[0].Value > 0 {
			detail += fmt.Sprintf(" Highest: %s (%d).", idleOutliers[0].EmployeeName, idleOutliers[0].Value)
		}
		out = append(out, dto.LogInsightsHighlight{
			Severity: sev,
			Title:    "Idle signals",
			Detail:   detail,
		})
	}

	if m.StaleSessions > 0 {
		out = append(out, dto.LogInsightsHighlight{
			Severity: "warning",
			Title:    "Stale sessions",
			Detail: fmt.Sprintf(
				"%d session%s currently marked STALE — clients may be offline or not syncing.",
				m.StaleSessions, plural(m.StaleSessions),
			),
		})
	}

	if m.OpenSessions > 0 {
		out = append(out, dto.LogInsightsHighlight{
			Severity: "info",
			Title:    "Open right now",
			Detail: fmt.Sprintf(
				"%d session%s still ACTIVE with no end time.",
				m.OpenSessions, plural(m.OpenSessions),
			),
		})
	}

	return out
}

func plural(n int) string {
	if n == 1 {
		return ""
	}
	return "s"
}
