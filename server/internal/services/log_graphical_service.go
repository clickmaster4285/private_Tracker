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
	logGraphicalDefaultTopN = 8
	logGraphicalMaxTopN     = 20
)

// LogGraphicalService builds the /logs/graphical chart payload.
type LogGraphicalService struct {
	repo *repository.LogGraphicalRepo
}

// NewLogGraphicalService constructs a LogGraphicalService.
func NewLogGraphicalService(repo *repository.LogGraphicalRepo) *LogGraphicalService {
	return &LogGraphicalService{repo: repo}
}

// LogGraphicalParams are validated filter inputs.
type LogGraphicalParams struct {
	From         time.Time
	To           time.Time
	DepartmentID *int
	EmployeeID   string
	TopN         int
}

// GetGraphical returns filled activity + productivity time series.
func (s *LogGraphicalService) GetGraphical(ctx context.Context, p LogGraphicalParams) (*dto.LogGraphicalResponse, error) {
	if p.From.IsZero() || p.To.IsZero() {
		return nil, fmt.Errorf("from and to are required")
	}
	if !p.To.After(p.From) {
		return nil, fmt.Errorf("to must be after from")
	}
	if p.TopN < 1 {
		p.TopN = logGraphicalDefaultTopN
	}
	if p.TopN > logGraphicalMaxTopN {
		p.TopN = logGraphicalMaxTopN
	}
	p.EmployeeID = strings.TrimSpace(p.EmployeeID)

	trunc := "day"
	if p.To.Sub(p.From) <= 72*time.Hour {
		trunc = "hour"
	}

	repoParams := repository.LogGraphicalParams{
		From:         p.From,
		To:           p.To,
		DepartmentID: p.DepartmentID,
		EmployeeID:   p.EmployeeID,
		Trunc:        trunc,
		TopN:         p.TopN,
	}

	var (
		sessions   []repository.LogGraphicalBucketRow
		web        []repository.LogGraphicalBucketRow
		idle       []repository.LogGraphicalBucketRow
		prodSeries []repository.LogGraphicalProdBucketRow
		topApps    []repository.DashboardTopAppRow
		topDomains []repository.DashboardTopDomainRow
	)

	g, gctx := errgroup.WithContext(ctx)
	g.Go(func() error {
		var err error
		sessions, err = s.repo.SessionSeries(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		web, err = s.repo.WebSeries(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		idle, err = s.repo.IdleSeries(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		prodSeries, err = s.repo.ProductivitySeries(gctx, repoParams)
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
	if err := g.Wait(); err != nil {
		return nil, err
	}

	step := 24 * time.Hour
	if trunc == "hour" {
		step = time.Hour
	}
	starts := bucketStarts(p.From.UTC(), p.To.UTC(), step)

	sessMap := bucketMap(sessions)
	webMap := bucketMap(web)
	idleMap := bucketMap(idle)
	prodMap := make(map[int64]repository.LogGraphicalProdBucketRow, len(prodSeries))
	for _, row := range prodSeries {
		prodMap[row.At.UTC().Unix()] = row
	}

	activity := make([]dto.LogGraphicalActivityPt, 0, len(starts))
	productivity := make([]dto.LogGraphicalProductivityPt, 0, len(starts))
	var summary dto.LogGraphicalSummary

	for _, at := range starts {
		key := at.Unix()
		label := formatBucketLabel(at, trunc)
		sCount := sessMap[key]
		wCount := webMap[key]
		iCount := idleMap[key]
		activity = append(activity, dto.LogGraphicalActivityPt{
			Bucket: label, At: at.Format(time.RFC3339),
			Sessions: sCount, WebPages: wCount, IdleEvents: iCount,
		})
		summary.Sessions += sCount
		summary.WebPages += wCount
		summary.IdleEvents += iCount

		pr := prodMap[key]
		productivity = append(productivity, dto.LogGraphicalProductivityPt{
			Bucket: label, At: at.Format(time.RFC3339),
			ProductiveSeconds:   pr.ProductiveSeconds,
			UnproductiveSeconds: pr.UnproductiveSeconds,
			NeutralSeconds:      pr.NeutralSeconds,
		})
		summary.ProductiveSeconds += pr.ProductiveSeconds
		summary.UnproductiveSeconds += pr.UnproductiveSeconds
		summary.NeutralSeconds += pr.NeutralSeconds
	}
	summary.TotalSeconds = summary.ProductiveSeconds + summary.UnproductiveSeconds + summary.NeutralSeconds

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

	return &dto.LogGraphicalResponse{
		Range: dto.DashboardRange{
			From: p.From.UTC().Format(time.RFC3339),
			To:   p.To.UTC().Format(time.RFC3339),
		},
		Bucket:       trunc,
		Summary:      summary,
		Activity:     activity,
		Productivity: productivity,
		TopApps:      apps,
		TopDomains:   domains,
	}, nil
}

func bucketMap(rows []repository.LogGraphicalBucketRow) map[int64]int {
	m := make(map[int64]int, len(rows))
	for _, row := range rows {
		m[row.At.UTC().Unix()] = row.Count
	}
	return m
}

func bucketStarts(from, to time.Time, step time.Duration) []time.Time {
	start := from.UTC().Truncate(step)
	if start.Before(from.UTC()) {
		start = start.Add(step)
	}
	// Include the truncated start of `from` so partial first bucket appears.
	start = from.UTC().Truncate(step)
	out := make([]time.Time, 0, 64)
	for t := start; t.Before(to); t = t.Add(step) {
		out = append(out, t)
		if len(out) > 400 {
			break // hard cap — pathological ranges
		}
	}
	return out
}

func formatBucketLabel(at time.Time, trunc string) string {
	if trunc == "hour" {
		return at.UTC().Format("Jan 2 15:00")
	}
	return at.UTC().Format("Jan 2")
}
