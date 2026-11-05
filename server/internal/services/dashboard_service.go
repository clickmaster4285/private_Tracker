package services

import (
	"context"
	"fmt"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/stream"
	"github.com/alpha-ai-tracker/server/internal/ws"
	"golang.org/x/sync/errgroup"
)

const (
	dashboardLiveOnlineWindow = 3 * time.Minute
	dashboardDefaultTopN      = 8
	dashboardMaxTopN          = 20
	dashboardDefaultRecent    = 10
	dashboardMaxRecent        = 25
)

// DashboardService builds the home-page summary aggregate.
type DashboardService struct {
	repo             *repository.DashboardRepo
	employeeRepo     *repository.EmployeeRepo
	termsConsentRepo *repository.TermsConsentRepo
	taRepo           *repository.TimeAttendanceRepo
	taService        *TimeAttendanceService
	streamHub        *stream.Hub
	presence         *ws.Hub // optional
}

// NewDashboardService constructs a DashboardService. presence may be nil.
func NewDashboardService(
	repo *repository.DashboardRepo,
	employeeRepo *repository.EmployeeRepo,
	termsConsentRepo *repository.TermsConsentRepo,
	taRepo *repository.TimeAttendanceRepo,
	taService *TimeAttendanceService,
	streamHub *stream.Hub,
	presence *ws.Hub,
) *DashboardService {
	return &DashboardService{
		repo:             repo,
		employeeRepo:     employeeRepo,
		termsConsentRepo: termsConsentRepo,
		taRepo:           taRepo,
		taService:        taService,
		streamHub:        streamHub,
		presence:         presence,
	}
}

// SummaryParams are validated filter inputs for GetSummary.
type SummaryParams struct {
	From         time.Time
	To           time.Time
	DepartmentID *int
	TopN         int
	RecentLimit  int
}

// GetSummary returns the dashboard aggregate. from is inclusive, to is exclusive.
func (s *DashboardService) GetSummary(ctx context.Context, p SummaryParams) (*dto.DashboardSummaryResponse, error) {
	if p.From.IsZero() || p.To.IsZero() {
		return nil, fmt.Errorf("from and to are required")
	}
	if !p.To.After(p.From) {
		return nil, fmt.Errorf("to must be after from")
	}
	if p.TopN < 1 {
		p.TopN = dashboardDefaultTopN
	}
	if p.TopN > dashboardMaxTopN {
		p.TopN = dashboardMaxTopN
	}
	if p.RecentLimit < 1 {
		p.RecentLimit = dashboardDefaultRecent
	}
	if p.RecentLimit > dashboardMaxRecent {
		p.RecentLimit = dashboardMaxRecent
	}

	repoParams := repository.DashboardSummaryParams{
		From:         p.From,
		To:           p.To,
		DepartmentID: p.DepartmentID,
		TopN:         p.TopN,
		RecentLimit:  p.RecentLimit,
	}

	var (
		employees  repository.DashboardEmployeeCounts
		activity   repository.DashboardActivityCounts
		monitoring repository.DashboardMonitoringCounts
		devices    repository.DashboardDeviceStats
		attendance dto.DashboardAttendance
		topApps    []repository.DashboardTopAppRow
		topDomains []repository.DashboardTopDomainRow
		recent     []repository.DashboardRecentSessionRow
		live       dto.DashboardLive
	)

	g, gctx := errgroup.WithContext(ctx)

	g.Go(func() error {
		var err error
		employees, err = s.repo.EmployeeCounts(gctx, p.DepartmentID)
		return err
	})
	g.Go(func() error {
		var err error
		activity, err = s.repo.ActivityCounts(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		monitoring, err = s.repo.MonitoringCounts(gctx)
		return err
	})
	g.Go(func() error {
		var err error
		devices, err = s.repo.DeviceStats(gctx, p.DepartmentID)
		return err
	})
	g.Go(func() error {
		// Today's attendance for the department-filtered employee set. Reuses the
		// time-attendance computation so the card matches GET /attendance/today.
		emps, err := s.employeeRepo.ListAll(gctx)
		if err != nil {
			return err
		}
		ids := make([]string, 0, len(emps))
		for _, e := range emps {
			if p.DepartmentID != nil && e.DepartmentID != *p.DepartmentID {
				continue
			}
			ids = append(ids, e.EmployeeID)
		}
		attendance, err = s.taService.AttendanceDayCounts(gctx, ids)
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
		recent, err = s.repo.RecentSessions(gctx, repoParams)
		return err
	})
	g.Go(func() error {
		var err error
		live, err = s.liveCounts(gctx, p.DepartmentID)
		return err
	})

	if err := g.Wait(); err != nil {
		return nil, err
	}

	versionRows := make([]dto.DashboardVersionRow, 0, len(devices.Versions))
	for _, v := range devices.Versions {
		versionRows = append(versionRows, dto.DashboardVersionRow{Version: v.Version, Count: v.Count})
	}
	appRows := make([]dto.DashboardTopApp, 0, len(topApps))
	for _, a := range topApps {
		appRows = append(appRows, dto.DashboardTopApp{
			AppDisplayName: a.AppDisplayName,
			ProcessName:    a.ProcessName,
			SessionCount:   a.SessionCount,
			OpenNow:        a.OpenNow,
		})
	}
	domainRows := make([]dto.DashboardTopDomain, 0, len(topDomains))
	for _, d := range topDomains {
		domainRows = append(domainRows, dto.DashboardTopDomain{Domain: d.Domain, Visits: d.Visits})
	}
	recentRows := make([]dto.DashboardRecentSession, 0, len(recent))
	for _, srow := range recent {
		var endedAt, lastSync *string
		if srow.EndedAt != nil {
			v := srow.EndedAt.Format(time.RFC3339Nano)
			endedAt = &v
		}
		if srow.LastSyncAt != nil {
			v := srow.LastSyncAt.Format(time.RFC3339Nano)
			lastSync = &v
		}
		recentRows = append(recentRows, dto.DashboardRecentSession{
			ID:             srow.ID,
			EmployeeID:     srow.EmployeeID,
			EmployeeName:   srow.EmployeeName,
			AppDisplayName: srow.AppDisplayName,
			ProcessName:    srow.ProcessName,
			Status:         srow.Status,
			StartedAt:      srow.StartedAt.Format(time.RFC3339Nano),
			EndedAt:        endedAt,
			LastSyncAt:     lastSync,
		})
	}

	return &dto.DashboardSummaryResponse{
		Range: dto.DashboardRange{
			From: p.From.Format(time.RFC3339Nano),
			To:   p.To.Format(time.RFC3339Nano),
		},
		Employees: dto.DashboardEmployees{
			Total: employees.Total, Tracked: employees.Tracked, Untracked: employees.Untracked,
		},
		Activity: dto.DashboardActivity{
			Sessions: activity.Sessions, WebPages: activity.WebPages,
			OpenSessions: activity.OpenSessions, StaleSessions: activity.StaleSessions,
		},
		Monitoring: dto.DashboardMonitoring{
			UnclassifiedApps: monitoring.UnclassifiedApps, UnclassifiedSites: monitoring.UnclassifiedSites,
		},
		Devices: dto.DashboardDevices{
			Active: devices.Active, Seen15m: devices.Seen15m, Seen24h: devices.Seen24h,
			Stale7d: devices.Stale7d, Versions: versionRows,
		},
		Attendance:     attendance,
		Live:           live,
		TopApps:        appRows,
		TopDomains:     domainRows,
		RecentSessions: recentRows,
	}, nil
}

// liveCounts mirrors StreamHandler.ListEmployees online rules but returns integers only.
func (s *DashboardService) liveCounts(ctx context.Context, departmentID *int) (dto.DashboardLive, error) {
	out := dto.DashboardLive{PresenceAvailable: false}
	if s.streamHub == nil || !s.streamHub.Config().Enabled {
		return out, nil
	}
	out.PresenceAvailable = true

	employees, err := s.employeeRepo.ListAll(ctx)
	if err != nil {
		return out, fmt.Errorf("dashboard live employees: %w", err)
	}
	heartbeats, err := s.taRepo.ListLastHeartbeats(ctx)
	if err != nil {
		return out, fmt.Errorf("dashboard live heartbeats: %w", err)
	}
	accepted, err := s.termsConsentRepo.ListAcceptedEmployeeIDs(ctx, stream.FeatureID)
	if err != nil {
		return out, fmt.Errorf("dashboard live consent: %w", err)
	}

	now := time.Now().UTC()
	presenceOn := s.presence != nil && s.presence.Config().Enabled
	for _, e := range employees {
		if departmentID != nil && e.DepartmentID != *departmentID {
			continue
		}
		snap := s.streamHub.Snapshot(e.EmployeeID)
		wsConnected := presenceOn && s.presence.IsConnected(e.EmployeeID)
		hb, hasHB := heartbeats[e.EmployeeID]
		hbOnline := hasHB && now.Sub(hb.UTC()) <= dashboardLiveOnlineWindow
		online := dashboardEmployeeLiveOnline(presenceOn, wsConnected, hbOnline)
		if online {
			out.Online++
		}
		if snap.Streaming {
			out.Streaming++
		}
		if !accepted[e.EmployeeID] {
			out.ConsentMissing++
		}
	}
	return out, nil
}

func dashboardEmployeeLiveOnline(presenceEnabled, wsConnected, hbOnline bool) bool {
	if presenceEnabled {
		return wsConnected
	}
	return hbOnline
}
