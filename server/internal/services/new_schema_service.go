package services

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

// NewSchemaService handles business logic for Phase 1 & Phase 2 tables.
type NewSchemaService struct {
	repo            *repository.NewSchemaRepo
	employeeRepo    *repository.EmployeeRepo
	geofenceService *GeofenceService
}

func NewNewSchemaService(repo *repository.NewSchemaRepo, employeeRepo *repository.EmployeeRepo, geofenceService *GeofenceService) *NewSchemaService {
	return &NewSchemaService{repo: repo, employeeRepo: employeeRepo, geofenceService: geofenceService}
}

// ── device_hardware_info ──

func (s *NewSchemaService) SyncDeviceHardware(ctx context.Context, req *dto.SyncDeviceHardwareRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	entries := make([]models.DeviceHardwareInfo, 0, len(req.Entries))
	for _, e := range req.Entries {
		ts, err := time.Parse(time.RFC3339, e.CollectedAt)
		if err != nil {
			ts = now
		}
		entries = append(entries, models.DeviceHardwareInfo{
			ID:             e.ID,
			EmployeeID:     req.EmployeeID,
			MachineID:      req.MachineID,
			MacAddress:     e.MacAddress,
			Hostname:       e.Hostname,
			OsName:         e.OsName,
			OsVersion:      e.OsVersion,
			CpuModel:       e.CpuModel,
			CpuCores:       e.CpuCores,
			RamTotalMB:     e.RamTotalMb,
			StorageDevices: e.StorageDevices,
			GpuModel:       e.GpuModel,
			GpuVramMB:      e.GpuVramMb,
			CollectedAt:    ts,
			SyncedAt:       &now,
		})
	}

	inserted, err := s.repo.BulkInsertDeviceHardware(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk insert device_hardware: %w", err)
	}
	// The hardware snapshot carries the real hostname: use it to give the device record a
	// human label (the client login may not send one) for the Device Specs dropdown.
	if req.MachineID != "" {
		for _, e := range req.Entries {
			if strings.TrimSpace(e.Hostname) != "" {
				_ = s.repo.TouchDeviceName(ctx, req.EmployeeID, req.MachineID, e.Hostname)
				break
			}
		}
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── installed_applications ──

// SyncInstalledApps ingests installed applications as a company-global catalog
// (deduplicated by app_fingerprint = desktop_id|binary_name) plus per-employee link rows
// holding install-specific metadata (version, path, install date).
func (s *NewSchemaService) SyncInstalledApps(ctx context.Context, req *dto.SyncInstalledAppsRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	inserted := 0

	// ONE transaction per request (2026-08-11) — the old code opened and committed a
	// separate transaction per entry (500 transactions for a 500-row batch), which was
	// the dominant server-side bottleneck under large-backlog syncs. Catalog upserts
	// conflict on app_fingerprint, so duplicate rows in one batch collapse to one
	// catalog row while every entry still gets its employee link.
	tx, err := s.repo.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	for _, e := range req.Entries {
		dets, _ := time.Parse(time.RFC3339, e.DetectedAt)
		var installDate *time.Time
		if e.InstallDate != nil {
			if t, err := time.Parse(time.RFC3339, *e.InstallDate); err == nil {
				installDate = &t
			}
		}

		cat := models.InstalledApplication{
			ID:              e.ID,
			EmployeeID:      req.EmployeeID,
			AppName:         e.AppName,
			AppVersion:      e.AppVersion,
			Publisher:       e.Publisher,
			InstallPath:     e.InstallPath,
			InstallDate:     installDate,
			UninstallString: e.UninstallString,
			ChangeType:      e.ChangeType,
			DetectedAt:      dets,
			SyncedAt:        &now,
			BinaryName:      e.BinaryName,
			IsBrowser:       e.IsBrowser,
			DesktopID:       e.DesktopID,
			Categories:      e.Categories,
			AppFingerprint:  appFingerprint(e.DesktopID, e.BinaryName),
		}

		catalogID, err := s.repo.UpsertApplicationCatalog(ctx, tx, cat)
		if err != nil {
			return nil, err
		}

		err = s.repo.UpsertEmployeeAppLink(ctx, tx, models.EmployeeInstalledApplication{
			EmployeeID:             req.EmployeeID,
			MachineID:              req.MachineID,
			InstalledApplicationID: catalogID,
			AppVersion:             e.AppVersion,
			Publisher:              e.Publisher,
			InstallPath:            e.InstallPath,
			InstallDate:            installDate,
		})
		if err != nil {
			return nil, err
		}

		inserted++
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit tx: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// appFingerprint builds the catalog natural key for applications.
func appFingerprint(desktopID, binaryName string) string {
	desktopID = strings.TrimSpace(desktopID)
	binaryName = strings.TrimSpace(binaryName)
	return desktopID + "|" + binaryName
}

// packageFingerprint builds the catalog natural key for packages.
func packageFingerprint(packageName, sourceManager string) string {
	return strings.TrimSpace(packageName) + "|" + strings.TrimSpace(sourceManager)
}

// ── installed_packages ──

// SyncInstalledPackages ingests installed packages as a company-global catalog
// (deduplicated by package_fingerprint = package_name|source_manager) plus per-employee
// link rows holding install-specific metadata (version, path, publisher).
func (s *NewSchemaService) SyncInstalledPackages(ctx context.Context, req *dto.SyncInstalledPackagesRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	inserted := 0

	// ONE transaction per request (2026-08-11) — same rationale as SyncInstalledApps:
	// the old per-entry Begin/Upsert/Commit pattern opened 500 transactions for a
	// 500-row batch.
	tx, err := s.repo.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	for _, e := range req.Entries {
		dets, _ := time.Parse(time.RFC3339, e.DetectedAt)

		cat := models.InstalledPackage{
			ID:                 e.ID,
			EmployeeID:         req.EmployeeID,
			PackageName:        e.PackageName,
			Version:            e.Version,
			Category:           e.Category,
			SourceManager:      e.SourceManager,
			InstallPath:        e.InstallPath,
			Publisher:          e.Publisher,
			Description:        e.Description,
			DetectedAt:         dets,
			SyncedAt:           &now,
			PackageFingerprint: packageFingerprint(e.PackageName, e.SourceManager),
		}

		catalogID, err := s.repo.UpsertPackageCatalog(ctx, tx, cat)
		if err != nil {
			return nil, err
		}

		err = s.repo.UpsertEmployeePackageLink(ctx, tx, models.EmployeeInstalledPackage{
			EmployeeID:         req.EmployeeID,
			MachineID:          req.MachineID,
			InstalledPackageID: catalogID,
			Version:            e.Version,
			Publisher:          e.Publisher,
			InstallPath:        e.InstallPath,
		})
		if err != nil {
			return nil, err
		}

		inserted++
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit tx: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── network_info ──

func (s *NewSchemaService) SyncNetworkInfo(ctx context.Context, req *dto.SyncNetworkInfoRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	entries := make([]models.NetworkInfo, 0, len(req.Entries))
	for _, e := range req.Entries {
		ts, _ := time.Parse(time.RFC3339, e.CollectedAt)
		entries = append(entries, models.NetworkInfo{
			ID:                   e.ID,
			EmployeeID:           req.EmployeeID,
			MachineID:            req.MachineID,
			PublicIP:             e.PublicIP,
			PrivateIP:            e.PrivateIP,
			MacAddress:           e.MacAddress,
			NetworkInterfaceName: e.NetworkInterfaceName,
			CollectedAt:          ts,
			SyncedAt:             &now,
		})
	}

	inserted, err := s.repo.BulkInsertNetworkInfo(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk insert network_info: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── session_events ──

func (s *NewSchemaService) SyncSessionEvents(ctx context.Context, req *dto.SyncSessionEventsRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	entries := make([]models.SessionEvent, 0, len(req.Entries))
	for _, e := range req.Entries {
		ts, err := time.Parse(time.RFC3339, e.EventAt)
		if err != nil {
			return nil, fmt.Errorf("invalid eventAt for event %q: %w", e.ID, err)
		}
		count := 1
		if e.Count != nil {
			count = *e.Count
		}
		if count < 1 {
			return nil, fmt.Errorf("event count must be greater than zero")
		}
		firstAt, lastAt := ts, ts
		if e.FirstAt != nil {
			firstAt, err = time.Parse(time.RFC3339, *e.FirstAt)
			if err != nil {
				return nil, fmt.Errorf("invalid firstAt for event %q: %w", e.ID, err)
			}
		}
		if e.LastAt != nil {
			lastAt, err = time.Parse(time.RFC3339, *e.LastAt)
			if err != nil {
				return nil, fmt.Errorf("invalid lastAt for event %q: %w", e.ID, err)
			}
		}
		if lastAt.Before(firstAt) {
			return nil, fmt.Errorf("lastAt must not be before firstAt")
		}
		entries = append(entries, models.SessionEvent{
			ID:         e.ID,
			EmployeeID: req.EmployeeID,
			MachineID:  req.MachineID,
			EventType:  e.EventType,
			OsUsername: e.OsUsername,
			EventAt:    ts,
			EventCount: count,
			FirstAt:    firstAt,
			LastAt:     lastAt,
			SyncedAt:   &now,
		})
	}

	inserted, err := s.repo.BulkInsertSessionEvents(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk insert session_events: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── app_sessions ──

func (s *NewSchemaService) SyncAppSessions(ctx context.Context, req *dto.SyncAppSessionsRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	entries := make([]models.AppSession, 0, len(req.Entries))
	for _, e := range req.Entries {
		started, _ := time.Parse(time.RFC3339, e.StartedAt)
		var ended *time.Time
		if e.EndedAt != nil {
			if t, err := time.Parse(time.RFC3339, *e.EndedAt); err == nil {
				ended = &t
			}
		}
		var lastActivity *time.Time
		if e.LastActivityAt != nil {
			if t, err := time.Parse(time.RFC3339, *e.LastActivityAt); err == nil {
				lastActivity = &t
			}
		}
		entries = append(entries, models.AppSession{
			ID:                 e.ID,
			EmployeeID:         req.EmployeeID,
			ProcessName:        e.ProcessName,
			AppDisplayName:     e.AppDisplayName,
			StartedAt:          started,
			EndedAt:            ended,
			MachineID:          e.MachineID,
			SessionID:          e.SessionID,
			Platform:           e.Platform,
			ProcessID:          e.ProcessID,
			ParentProcessID:    e.ParentProcessID,
			InstalledAppID:     e.InstalledAppID,
			InstalledPackageID: e.InstalledPackageID,
			GroupedBy:          e.GroupedBy,
			CgroupScope:        e.CgroupScope,
			ContextLabel:       e.ContextLabel,
			ForegroundSeconds:  e.ForegroundSeconds,
			BackgroundSeconds:  e.BackgroundSeconds,
			LastActivityAt:     lastActivity,
			SyncedAt:           &now,
		})
	}

	inserted, err := s.repo.BulkInsertAppSessions(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk insert app_sessions: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d sessions", inserted, len(req.Entries))}, nil
}

// ── app_items ──

func (s *NewSchemaService) SyncAppItems(ctx context.Context, req *dto.SyncAppItemsRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	entries := make([]models.AppItem, 0, len(req.Entries))
	for _, e := range req.Entries {
		opened, _ := time.Parse(time.RFC3339, e.OpenedAt)
		var closed *time.Time
		if e.ClosedAt != nil {
			if t, err := time.Parse(time.RFC3339, *e.ClosedAt); err == nil {
				closed = &t
			}
		}
		entries = append(entries, models.AppItem{
			ID:           e.ID,
			EmployeeID:   req.EmployeeID,
			MachineID:    req.MachineID,
			AppSessionID: e.AppSessionID,
			ParentItemID: e.ParentItemID,
			ItemType:     e.ItemType,
			Title:        e.Title,
			Identifier:   e.Identifier,
			Url:          e.Url,
			Domain:       e.Domain,
			OpenedAt:     opened,
			ClosedAt:     closed,
			ProcessID:    e.ProcessID,
			ObjectType:   e.ObjectType,
			Action:       e.Action,
			JourneyID:    e.JourneyID,
			Sequence:     e.Sequence,
			PreviousPath: e.PreviousPath,
			CurrentPath:  e.CurrentPath,
			WindowID:     e.WindowID,
			TabID:        e.TabID,
			MetadataJSON: e.MetadataJSON,
			SyncedAt:     &now,
		})
	}

	inserted, rejectedIDs, missingSessionIDs, err := s.repo.BulkInsertAppItems(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk insert app_items: %w", err)
	}
	// rejectedIDs is present only when the orphan preflight refused rows; the
	// client leaves exactly those rows unsent so they re-send after their parent
	// session lands (see SyncBatchResponse.RejectedIds).
	// missingSessionIDs tells the client which parent sessions the server does
	// NOT have — the client resets those to is_synced=0 so they re-send,
	// breaking the permanent orphan deadlock (Bug #9 follow-up).
	return &dto.SyncBatchResponse{
		Synced:            inserted,
		Message:           fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries)),
		RejectedIds:       rejectedIDs,
		MissingSessionIds: missingSessionIDs,
	}, nil
}

// ── List app_sessions (for web dashboard) ──

func (s *NewSchemaService) ListAppSessions(ctx context.Context, params repository.AppSessionListParams) (*dto.AppSessionListResponse, error) {
	result, err := s.repo.ListAppSessions(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list app sessions: %w", err)
	}

	sessions := make([]dto.AppSessionResponse, len(result.Sessions))
	for i, s := range result.Sessions {
		status := s.Status
		if status == "" {
			status = "ACTIVE" // backfill for pre-031 rows
		}
		sessions[i] = dto.AppSessionResponse{
			ID:                 s.ID,
			EmployeeID:         s.EmployeeID,
			ProcessName:        s.ProcessName,
			AppDisplayName:     s.AppDisplayName,
			StartedAt:          s.StartedAt,
			EndedAt:            s.EndedAt,
			MachineID:          s.MachineID,
			SessionID:          s.SessionID,
			Platform:           s.Platform,
			ProcessID:          s.ProcessID,
			ParentProcessID:    s.ParentProcessID,
			InstalledAppID:     s.InstalledAppID,
			InstalledPackageID: s.InstalledPackageID,
			GroupedBy:          s.GroupedBy,
			CgroupScope:        s.CgroupScope,
			ContextLabel:       s.ContextLabel,
			SyncedAt:           s.SyncedAt,
			Status:             status,
			LastActivityAt:     s.LastActivityAt,
			LastSyncAt:         s.LastSyncAt,
		}
	}

	return &dto.AppSessionListResponse{
		Data:       sessions,
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}

// ── App sessions usage (per-app aggregate for web dashboard) ──

func (s *NewSchemaService) ListAppSessionsUsage(ctx context.Context, params repository.AppSessionUsageListParams) (*dto.AppUsageListResponse, error) {
	result, err := s.repo.AggregateAppSessionsUsage(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("aggregate app sessions usage: %w", err)
	}

	rows := make([]dto.AppUsageRow, len(result.Rows))
	for i, r := range result.Rows {
		rows[i] = dto.AppUsageRow{
			AppDisplayName:       r.AppDisplayName,
			ProcessName:          r.ProcessName,
			SessionCount:         r.SessionCount,
			FirstOpenedAt:        r.FirstOpenedAt,
			LastClosedAt:         r.LastClosedAt,
			TotalDurationSeconds: r.TotalDurationSeconds,
			HasOpenSession:       r.HasOpenSession,
			LastActiveAt:         r.LastActiveAt,
		}
	}

	return &dto.AppUsageListResponse{
		Data:                 rows,
		Total:                result.Total,
		Page:                 result.Page,
		PerPage:              result.PerPage,
		TotalPages:           result.TotalPages,
		TotalDurationSeconds: result.TotalDurationSeconds,
		TotalSessionCount:    result.TotalSessionCount,
		OpenSessionCount:     result.OpenSessionCount,
	}, nil
}

// ── List app sessions for a specific app (per-app chevron expand) ──

func (s *NewSchemaService) ListAppSessionsForApp(ctx context.Context, params repository.AppSessionForAppListParams) (*dto.AppSessionListResponse, error) {
	result, err := s.repo.ListAppSessionsForApp(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list app sessions for app: %w", err)
	}

	sessions := make([]dto.AppSessionResponse, len(result.Sessions))
	for i, sess := range result.Sessions {
		status := sess.Status
		if status == "" {
			status = "ACTIVE"
		}
		sessions[i] = dto.AppSessionResponse{
			ID:                 sess.ID,
			EmployeeID:         sess.EmployeeID,
			ProcessName:        sess.ProcessName,
			AppDisplayName:     sess.AppDisplayName,
			StartedAt:          sess.StartedAt,
			EndedAt:            sess.EndedAt,
			MachineID:          sess.MachineID,
			SessionID:          sess.SessionID,
			Platform:           sess.Platform,
			ProcessID:          sess.ProcessID,
			ParentProcessID:    sess.ParentProcessID,
			InstalledAppID:     sess.InstalledAppID,
			InstalledPackageID: sess.InstalledPackageID,
			GroupedBy:          sess.GroupedBy,
			CgroupScope:        sess.CgroupScope,
			ContextLabel:       sess.ContextLabel,
			ForegroundSeconds:  sess.ForegroundSeconds,
			BackgroundSeconds:  sess.BackgroundSeconds,
			SyncedAt:           sess.SyncedAt,
			Status:             status,
			LastActivityAt:     sess.LastActivityAt,
			LastSyncAt:         sess.LastSyncAt,
		}
	}

	return &dto.AppSessionListResponse{
		Data:       sessions,
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}

// ── App items usage (per-domain aggregate for web dashboard) ──

func (s *NewSchemaService) ListAppItemsUsage(ctx context.Context, params repository.AppItemUsageListParams) (*dto.WebUsageListResponse, error) {
	result, err := s.repo.AggregateAppItemsUsage(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("aggregate app items usage: %w", err)
	}

	rows := make([]dto.WebUsageRow, len(result.Rows))
	for i, r := range result.Rows {
		rows[i] = dto.WebUsageRow{
			Domain:               r.Domain,
			VisitCount:           r.VisitCount,
			TotalDurationSeconds: r.TotalDurationSeconds,
			LastVisitedAt:        r.LastVisitedAt,
			FirstVisitedAt:       r.FirstVisitedAt,
		}
	}

	return &dto.WebUsageListResponse{
		Data:                 rows,
		Total:                result.Total,
		Page:                 result.Page,
		PerPage:              result.PerPage,
		TotalPages:           result.TotalPages,
		TotalDurationSeconds: result.TotalDurationSeconds,
		TotalVisitCount:      result.TotalVisitCount,
	}, nil
}

// ── List app items (for web dashboard — URLs, file paths, etc.) ──

func (s *NewSchemaService) ListAppItems(ctx context.Context, params repository.AppItemListParams) (*dto.AppItemListResponse, error) {
	result, err := s.repo.ListAppItems(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list app_items: %w", err)
	}

	items := make([]dto.AppItemResponse, len(result.Items))
	for i, item := range result.Items {
		var closedAt *time.Time
		if item.ClosedAt != nil {
			t := *item.ClosedAt
			closedAt = &t
		}
		var syncedAt *time.Time
		if item.SyncedAt != nil {
			t := *item.SyncedAt
			syncedAt = &t
		}
		var parentItemID *string
		if item.ParentItemID != nil {
			t := *item.ParentItemID
			parentItemID = &t
		}

		browserName := ""
		if item.MetadataJSON != "" {
			var meta map[string]interface{}
			if err := json.Unmarshal([]byte(item.MetadataJSON), &meta); err == nil {
				if processName, ok := meta["processName"].(string); ok && processName != "" {
					if meta["source"] != "webview" {
						resolved, _ := s.repo.GetBrowserNameByProcessName(ctx, processName)
						browserName = resolved
					} else {
						browserName = processName
					}
				}
			}
		}

		items[i] = dto.AppItemResponse{
			ID:           item.ID,
			EmployeeID:   item.EmployeeID,
			AppSessionID: item.AppSessionID,
			ParentItemID: parentItemID,
			ItemType:     item.ItemType,
			Title:        item.Title,
			Identifier:   item.Identifier,
			Url:          item.Url,
			Domain:       item.Domain,
			OpenedAt:     item.OpenedAt,
			ClosedAt:     closedAt,
			ProcessID:    item.ProcessID,
			ObjectType:   item.ObjectType,
			Action:       item.Action,
			JourneyID:    item.JourneyID,
			Sequence:     item.Sequence,
			PreviousPath: item.PreviousPath,
			CurrentPath:  item.CurrentPath,
			WindowID:     item.WindowID,
			TabID:        item.TabID,
			MetadataJSON: item.MetadataJSON,
			BrowserName:  browserName,
			SyncedAt:     syncedAt,
		}
	}

	return &dto.AppItemListResponse{
		Data:       items,
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}

// ── app_status (key/value status rows; natural key employee_id+key) ──

func (s *NewSchemaService) SyncAppStatus(ctx context.Context, req *dto.SyncAppStatusRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	tx, err := s.repo.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	inserted := 0
	for _, e := range req.Entries {
		updatedAt := now
		if t, err := time.Parse(time.RFC3339, e.UpdatedAt); err == nil {
			updatedAt = t
		}
		if err := s.repo.UpsertAppStatus(ctx, tx, models.AppStatus{
			EmployeeID: req.EmployeeID,
			MachineID:  req.MachineID,
			Key:        e.Key,
			Value:      e.Value,
			UpdatedAt:  updatedAt,
		}); err != nil {
			return nil, err
		}
		inserted++
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit tx: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── hardware_devices (USB / peripheral hotplug) ──

func (s *NewSchemaService) SyncHardwareDevices(ctx context.Context, req *dto.SyncHardwareDevicesRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	entries := make([]models.HardwareDevice, 0, len(req.Entries))
	for _, e := range req.Entries {
		plugged, _ := time.Parse(time.RFC3339, e.PluggedAt)
		var unplugged *time.Time
		if e.UnpluggedAt != nil {
			if t, err := time.Parse(time.RFC3339, *e.UnpluggedAt); err == nil {
				unplugged = &t
			}
		}
		entries = append(entries, models.HardwareDevice{
			ID:          e.ID,
			EmployeeID:  req.EmployeeID,
			MachineID:   req.MachineID,
			DeviceClass: e.DeviceClass,
			Vendor:      e.Vendor,
			Product:     e.Product,
			Serial:      e.Serial,
			BusPath:     e.BusPath,
			DeviceNode:  e.DeviceNode,
			PluggedAt:   plugged,
			UnpluggedAt: unplugged,
		})
	}

	inserted, err := s.repo.BulkUpsertHardwareDevices(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk upsert hardware_devices: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── permission_status (one row per permission method; keyed by check_id) ──

func (s *NewSchemaService) SyncPermissionStatus(ctx context.Context, req *dto.SyncPermissionStatusRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	entries := make([]models.PermissionStatus, 0, len(req.Entries))
	for _, e := range req.Entries {
		checkedAt, _ := time.Parse(time.RFC3339, e.CheckedAt)
		entries = append(entries, models.PermissionStatus{
			CheckID:     e.CheckID,
			EmployeeID:  req.EmployeeID,
			MachineID:   req.MachineID,
			SessionID:   e.SessionID,
			SessionType: e.SessionType,
			Platform:    e.Platform,
			CheckedAt:   checkedAt,
			Method:      e.Method,
			Works:       e.Works,
			Details:     e.Details,
		})
	}

	inserted, err := s.repo.BulkUpsertPermissionStatus(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk upsert permission_status: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── storage_devices (children of device_hardware_info) ──

func (s *NewSchemaService) SyncStorageDevices(ctx context.Context, req *dto.SyncStorageDevicesRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	entries := make([]models.StorageDevice, 0, len(req.Entries))
	for _, e := range req.Entries {
		entries = append(entries, models.StorageDevice{
			ID:               e.ID,
			EmployeeID:       req.EmployeeID,
			MachineID:        req.MachineID,
			DeviceHardwareID: e.DeviceHardwareID,
			DeviceType:       e.DeviceType,
			Model:            e.Model,
			CapacityMB:       e.CapacityMB,
		})
	}

	inserted, err := s.repo.BulkUpsertStorageDevices(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk upsert storage_devices: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── location_samples (Phase 3 GPS) ──

func (s *NewSchemaService) SyncLocationSamples(ctx context.Context, req *dto.SyncLocationSamplesRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	entries := make([]models.LocationSample, 0, len(req.Entries))
	for _, e := range req.Entries {
		capturedAt, err := time.Parse(time.RFC3339, e.CapturedAt)
		if err != nil {
			continue
		}
		if e.Latitude < -90 || e.Latitude > 90 || e.Longitude < -180 || e.Longitude > 180 {
			continue
		}
		source := e.Source
		if source == "" {
			source = "ip"
		}
		entries = append(entries, models.LocationSample{
			ID:         e.ID,
			EmployeeID: req.EmployeeID,
			MachineID:  req.MachineID,
			Latitude:   e.Latitude,
			Longitude:  e.Longitude,
			AccuracyM:  e.AccuracyM,
			AltitudeM:  e.AltitudeM,
			Source:     source,
			Address:    e.Address,
			CapturedAt: capturedAt,
		})
	}

	inserted, err := s.repo.BulkUpsertLocationSamples(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk upsert location_samples: %w", err)
	}
	if s.geofenceService != nil && len(entries) > 0 {
		if err := s.geofenceService.EvaluateSamplesOnIngest(ctx, req.EmployeeID, entries); err != nil {
			return nil, fmt.Errorf("geofence evaluation: %w", err)
		}
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

func (s *NewSchemaService) ListLocationSamples(ctx context.Context, params repository.LocationSampleListParams) (*dto.LocationSampleListResponse, error) {
	result, err := s.repo.ListLocationSamples(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list location_samples: %w", err)
	}

	items := make([]dto.LocationSampleResponse, len(result.Items))
	for i, item := range result.Items {
		var syncedAt *time.Time
		if item.SyncedAt != nil {
			t := *item.SyncedAt
			syncedAt = &t
		}
		geofenceStatus := "Outside"
		if s.geofenceService != nil {
			if label, err := s.geofenceService.GeofenceLabel(ctx, item.Latitude, item.Longitude); err == nil && label != "" {
				geofenceStatus = label
			}
		}
		items[i] = dto.LocationSampleResponse{
			ID:             item.ID,
			EmployeeID:     item.EmployeeID,
			EmployeeName:   item.EmployeeName,
			Latitude:       item.Latitude,
			Longitude:      item.Longitude,
			AccuracyM:      item.AccuracyM,
			AltitudeM:      item.AltitudeM,
			Source:         item.Source,
			Address:        item.Address,
			CapturedAt:     item.CapturedAt,
			SyncedAt:       syncedAt,
			GeofenceStatus: geofenceStatus,
		}
	}

	return &dto.LocationSampleListResponse{
		Data:       items,
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}

// ────────────────────────────────
// EMPLOYEE DETAIL (web dashboard — GET /employees/:id/detail)
// Aggregates every synced machine-data surface for one employee into a single response.
// ────────────────────────────────

// GetEmployeeDetail builds the full machine picture for an employee by UUID.
// The sync tables are keyed by employee_id (EMP-XXXXX), so the UUID from the route is
// resolved to the employee record first, then every read uses emp.EmployeeID.
// machineID = "" returns the employee-wide aggregate; a machine id scopes every
// device-level surface to that machine (Device Specs device dropdown).
func (s *NewSchemaService) GetEmployeeDetail(ctx context.Context, id, machineID string) (*dto.EmployeeDetailResponse, error) {
	emp, err := s.employeeRepo.GetByID(ctx, id)
	if err != nil {
		return nil, fmt.Errorf("get employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}

	employeeID := emp.EmployeeID
	resp := &dto.EmployeeDetailResponse{
		Employee:        employeeToResponse(emp),
		StorageDevices:  []dto.StorageDeviceDetail{},
		Applications:    []dto.InstalledApplicationDetail{},
		Packages:        []dto.InstalledPackageDetail{},
		HardwareDevices: []dto.HardwareDeviceDetail{},
		Permissions:     []dto.PermissionStatusDetail{},
		AppStatus:       map[string]string{},
	}

	// Latest device hardware snapshot.
	hw, err := s.repo.GetLatestDeviceHardware(ctx, employeeID, machineID)
	if err != nil {
		return nil, err
	}
	if hw != nil {
		resp.DeviceHardware = &dto.DeviceHardwareDetail{
			ID:             hw.ID,
			MacAddress:     hw.MacAddress,
			Hostname:       hw.Hostname,
			OsName:         hw.OsName,
			OsVersion:      hw.OsVersion,
			CpuModel:       hw.CpuModel,
			CpuCores:       hw.CpuCores,
			RamTotalMb:     hw.RamTotalMB,
			StorageDevices: hw.StorageDevices,
			GpuModel:       hw.GpuModel,
			GpuVramMb:      hw.GpuVramMB,
			CollectedAt:    hw.CollectedAt,
			SyncedAt:       hw.SyncedAt,
		}
	}

	// Storage devices (children of device_hardware_info).
	storage, err := s.repo.ListStorageDevices(ctx, employeeID, machineID)
	if err != nil {
		return nil, err
	}
	for _, d := range storage {
		resp.StorageDevices = append(resp.StorageDevices, dto.StorageDeviceDetail{
			ID:         d.ID,
			DeviceType: d.DeviceType,
			Model:      d.Model,
			CapacityMb: d.CapacityMB,
			CreatedAt:  d.CreatedAt,
		})
	}

	// Latest network snapshot.
	net, err := s.repo.GetLatestNetworkInfo(ctx, employeeID, machineID)
	if err != nil {
		return nil, err
	}
	if net != nil {
		resp.NetworkInfo = &dto.NetworkInfoResponse{
			ID:                   net.ID,
			EmployeeID:           net.EmployeeID,
			PublicIP:             net.PublicIP,
			PrivateIP:            net.PrivateIP,
			MacAddress:           net.MacAddress,
			NetworkInterfaceName: net.NetworkInterfaceName,
			CollectedAt:          net.CollectedAt,
			SyncedAt:             net.SyncedAt,
		}
	}

	// Currently-installed applications (active junction links).
	apps, err := s.repo.ListEmployeeApplications(ctx, employeeID, machineID)
	if err != nil {
		return nil, err
	}
	for _, a := range apps {
		resp.Applications = append(resp.Applications, dto.InstalledApplicationDetail{
			ID:          a.ID,
			AppName:     a.AppName,
			BinaryName:  a.BinaryName,
			Version:     a.Version,
			Publisher:   a.Publisher,
			InstallPath: a.InstallPath,
			InstallDate: a.InstallDate,
			IsBrowser:   a.IsBrowser,
			Categories:  a.Categories,
			DesktopID:   a.DesktopID,
			FirstSeenAt: a.FirstSeenAt,
			LastSeenAt:  a.LastSeenAt,
		})
	}

	// Currently-installed packages (active junction links).
	pkgs, err := s.repo.ListEmployeePackages(ctx, employeeID, machineID)
	if err != nil {
		return nil, err
	}
	for _, p := range pkgs {
		resp.Packages = append(resp.Packages, dto.InstalledPackageDetail{
			ID:            p.ID,
			PackageName:   p.PackageName,
			Version:       p.Version,
			Category:      p.Category,
			SourceManager: p.SourceManager,
			InstallPath:   p.InstallPath,
			Publisher:     p.Publisher,
			Description:   p.Description,
			FirstSeenAt:   p.FirstSeenAt,
			LastSeenAt:    p.LastSeenAt,
		})
	}

	// Peripherals (USB hotplug history).
	devices, err := s.repo.ListHardwareDevices(ctx, employeeID, machineID)
	if err != nil {
		return nil, err
	}
	for _, d := range devices {
		resp.HardwareDevices = append(resp.HardwareDevices, dto.HardwareDeviceDetail{
			ID:          d.ID,
			DeviceClass: d.DeviceClass,
			Vendor:      d.Vendor,
			Product:     d.Product,
			Serial:      d.Serial,
			BusPath:     d.BusPath,
			PluggedAt:   d.PluggedAt,
			UnpluggedAt: d.UnpluggedAt,
		})
	}

	// Key/value app status.
	statuses, err := s.repo.ListAppStatus(ctx, employeeID, machineID)
	if err != nil {
		return nil, err
	}
	for _, st := range statuses {
		resp.AppStatus[st.Key] = st.Value
	}

	// Permission-method checks.
	perms, err := s.repo.ListPermissionStatus(ctx, employeeID, machineID)
	if err != nil {
		return nil, err
	}
	for _, p := range perms {
		resp.Permissions = append(resp.Permissions, dto.PermissionStatusDetail{
			CheckID:     p.CheckID,
			SessionID:   p.SessionID,
			SessionType: p.SessionType,
			Platform:    p.Platform,
			CheckedAt:   p.CheckedAt,
			Method:      p.Method,
			Works:       p.Works,
			Details:     p.Details,
		})
	}

	// Activity stats over sessions/items.
	stats, err := s.repo.GetEmployeeActivityStats(ctx, employeeID)
	if err != nil {
		return nil, err
	}
	resp.Stats = dto.EmployeeActivityStats{
		TotalSessions:  stats.TotalSessions,
		OpenSessions:   stats.OpenSessions,
		TotalItems:     stats.TotalItems,
		LastActivityAt: stats.LastActivityAt,
	}

	// Machine list — the CANONICAL source is employee_machines (one row per physical
	// machine, migration 049). The auth-token registry (employee_devices) rotates a row
	// on every re-login, so driving the dropdown off it duplicated machines (G8). The
	// registry is still consulted for the newest platform/client version and the
	// token-level active flag of each machine.
	machineList, err := s.repo.ListEmployeeMachines(ctx, employeeID)
	if err != nil {
		return nil, err
	}
	deviceList, err := s.repo.ListEmployeeDevices(ctx, employeeID)
	if err != nil {
		return nil, err
	}
	hostnames, err := s.repo.LatestHostnamesByMachine(ctx, employeeID)
	if err != nil {
		return nil, err
	}

	// Newest device registration per machine (registry rows come back last_seen DESC).
	latestByMachine := make(map[string]models.EmployeeDevice, len(deviceList))
	for _, d := range deviceList {
		if _, ok := latestByMachine[d.MachineID]; !ok {
			latestByMachine[d.MachineID] = d
		}
	}

	for _, m := range machineList {
		machineID := m.MachineID
		hostname := strings.TrimSpace(hostnames[machineID])
		var reg models.EmployeeDevice
		var hasReg bool
		if r, ok := latestByMachine[machineID]; ok {
			reg, hasReg = r, true
		}

		deviceName := ""
		if hasReg {
			deviceName = strings.TrimSpace(reg.DeviceName)
		}
		// Never surface a nameless device: fall back to the hostname the machine
		// reported, and finally to the machine id so the dropdown is always readable.
		if deviceName == "" {
			deviceName = hostname
		}
		if deviceName == "" {
			deviceName = machineID
		}
		if hostname == "" {
			hostname = deviceName
		}

		dtoDev := dto.EmployeeDeviceDto{
			EmployeeID:      employeeID,
			MachineID:       machineID,
			DeviceName:      deviceName,
			Hostname:        hostname,
			Status:          m.Status,
			FirstTrackAt:    m.FirstTrackAt,
			LastTrackAt:     m.LastTrackAt,
			WipeRequestedAt: m.WipeRequestedAt,
			// The lifecycle row is authoritative for "is this machine current".
			IsActiveDevice: m.Status == models.MachineStatusActive,
		}
		if hasReg {
			dtoDev.ID = reg.ID
			dtoDev.Platform = reg.Platform
			dtoDev.ClientVersion = reg.ClientVersion
			dtoDev.LastSeenAt = reg.LastSeenAt
			dtoDev.RevokedAt = reg.RevokedAt
			dtoDev.RemoveAt = reg.RemoveAt
		}
		resp.Devices = append(resp.Devices, dtoDev)
	}
	return resp, nil
}

// RemoveAccount handles the client's "remove the account" signal (R4/R5). The calling
// machine has already drained its unsynced rows; this marks that machine 'old', advances
// last_track_at, revokes its device registrations, closes its open activity and flips the
// employee to untracked. Non-destructive: nothing is deleted.
func (s *NewSchemaService) RemoveAccount(ctx context.Context, employeeID, machineID string) (*dto.SelfRemoveAccountResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, employeeID)
	if err != nil {
		return nil, fmt.Errorf("get employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}

	if machineID != "" {
		if err := s.repo.MarkEmployeeMachineOld(ctx, employeeID, machineID, false); err != nil {
			return nil, err
		}
	}

	revoked, err := s.repo.RevokeEmployeeDevices(ctx, employeeID)
	if err != nil {
		return nil, err
	}
	_ = revoked

	sessions, items, err := s.repo.CloseOpenActivity(ctx, employeeID)
	if err != nil {
		return nil, err
	}
	_ = sessions
	_ = items

	if _, err := s.employeeRepo.Update(ctx, emp.ID, map[string]interface{}{
		"tracking_status": "untracked",
		"is_online":       false,
	}); err != nil {
		return nil, fmt.Errorf("update employee tracking status: %w", err)
	}

	// Read back the machine row for an honest lastTrackAt/status in the response.
	resp := &dto.SelfRemoveAccountResponse{
		Removed:        true,
		MachineID:      machineID,
		Status:         models.MachineStatusOld,
		LastTrackAt:    time.Now().UTC(),
		TrackingStatus: "untracked",
	}
	if machines, lerr := s.repo.ListEmployeeMachines(ctx, employeeID); lerr == nil {
		for _, m := range machines {
			if m.MachineID == machineID {
				resp.Status = m.Status
				resp.LastTrackAt = m.LastTrackAt
				break
			}
		}
	}
	return resp, nil
}

// RemoveEmployeeDevices detaches every machine from an employee ("Remove current device"
// on the employees page). It is deliberately non-destructive: device registrations are
// revoked (not deleted), open app sessions/items are closed (not removed) and the
// employee is flipped to untracked. All history stays queryable — Device Specs keeps
// showing the retired machine — and the next login on a new machine registers a fresh
// device and starts tracking again.
func (s *NewSchemaService) RemoveEmployeeDevices(ctx context.Context, id string) (*dto.RemoveEmployeeDeviceResponse, error) {
	emp, err := s.employeeRepo.GetByID(ctx, id)
	if err != nil {
		return nil, fmt.Errorf("get employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}

	revoked, err := s.repo.RevokeEmployeeDevices(ctx, emp.EmployeeID)
	if err != nil {
		return nil, err
	}

	// R6 trigger: every ACTIVE machine of this employee is now 'old' and flagged for a
	// wipe (machines already 'old' are untouched). The next sync from any of them
	// persists its outstanding rows (DeviceAuth accepts the revoked token) and is
	// answered with action=wipe_local.
	if err := s.repo.MarkAllEmployeeMachinesOldWithWipe(ctx, emp.EmployeeID); err != nil {
		return nil, err
	}

	sessions, items, err := s.repo.CloseOpenActivity(ctx, emp.EmployeeID)
	if err != nil {
		return nil, err
	}

	if _, err := s.employeeRepo.Update(ctx, emp.ID, map[string]interface{}{
		"tracking_status": "untracked",
		"is_online":       false,
	}); err != nil {
		return nil, fmt.Errorf("update employee tracking status: %w", err)
	}

	return &dto.RemoveEmployeeDeviceResponse{
		RevokedDevices: revoked,
		ClosedSessions: sessions,
		ClosedItems:    items,
		TrackingStatus: "untracked",
	}, nil
}

// ── Hours Insights ──

func (s *NewSchemaService) GetHoursInsights(ctx context.Context, params repository.HoursInsightsParams) (*dto.HoursInsightsResponse, error) {
	result, err := s.repo.GetHoursInsights(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("get hours insights: %w", err)
	}

	rangeLabel := presetLabel(params.Preset)

	topItems := make([]dto.HoursInsightsTopItem, len(result.TopItems))
	for i, t := range result.TopItems {
		topItems[i] = dto.HoursInsightsTopItem{
			Name:         t.Name,
			Kind:         t.Kind,
			Category:     t.Category,
			Type:         t.Type,
			Color:        t.Color,
			TotalSeconds: t.TotalSeconds,
			FocusScore:   t.FocusScore,
			IsBrowser:    t.IsBrowser,
		}
	}

	topApps := make([]dto.HoursInsightsAppMeta, len(result.TopApps))
	for i, a := range result.TopApps {
		topApps[i] = dto.HoursInsightsAppMeta{
			Name:         a.Name,
			TotalSeconds: a.TotalSeconds,
			Color:        a.Color,
			Category:     a.Category,
			Type:         a.Type,
			SessionCount: a.SessionCount,
		}
	}

	appChart := make([]dto.HoursInsightsAppBucket, len(result.AppChart))
	for i, ac := range result.AppChart {
		appChart[i] = dto.HoursInsightsAppBucket{
			Bucket: ac.Bucket,
			Apps:   ac.Apps,
		}
	}

	return &dto.HoursInsightsResponse{
		Employee: dto.HoursInsightsEmployee{
			EmployeeID: result.EmployeeID,
			Name:       result.EmployeeName,
			Department: result.Department,
		},
		Range: dto.HoursInsightsRange{
			From:  result.RangeFrom,
			To:    result.RangeTo,
			Label: rangeLabel,
		},
		Summary: dto.HoursInsightsSummary{
			TotalSeconds:        result.Summary.TotalSeconds,
			ProductiveSeconds:   result.Summary.ProductiveSeconds,
			UnproductiveSeconds: result.Summary.UnproductiveSeconds,
			NeutralSeconds:      result.Summary.NeutralSeconds,
			FocusScore:          result.Summary.FocusScore,
			AppCount:            result.Summary.AppCount,
			SiteCount:           result.Summary.SiteCount,
		},
		Chart: func() []dto.HoursInsightsBucket {
			chart := make([]dto.HoursInsightsBucket, len(result.Chart))
			for i, c := range result.Chart {
				chart[i] = dto.HoursInsightsBucket{
					Bucket:       c.Bucket,
					Productive:   c.Productive,
					Unproductive: c.Unproductive,
					Neutral:      c.Neutral,
				}
			}
			return chart
		}(),
		AppChart: appChart,
		TopApps:  topApps,
		TopItems: topItems,
	}, nil
}

func presetLabel(preset string) string {
	switch preset {
	case "today":
		return "Today"
	case "yesterday":
		return "Yesterday"
	case "7d":
		return "Last 7 days"
	case "30d":
		return "Last 30 days"
	default:
		return "Custom"
	}
}
