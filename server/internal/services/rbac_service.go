package services

import (
	"context"
	"fmt"
	"log"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

// SystemRoleName is the seeded full-access role every company admin account gets.
const SystemRoleName = "company_admin"

// submodulesSeed describes one permission key: key, label, route.
type submodulesSeed struct {
	Key   string
	Name  string
	Route string
}

// moduleSeed is a navigation group with its submodule keys.
type moduleSeed struct {
	Key        string
	Name       string
	SortOrder  int
	Submodules []submodulesSeed
}

// rbacCatalog is the source of truth for modules/submodules — mirrored from the
// web dashboard's sidebar so role permissions map 1:1 onto navigation guards.
var rbacCatalog = []moduleSeed{
	{
		Key: "general", Name: "General", SortOrder: 1,
		Submodules: []submodulesSeed{
			{Key: "dashboard", Name: "Dashboard", Route: "/dashboard"},
		},
	},
	{
		Key: "hr", Name: "HR", SortOrder: 2,
		Submodules: []submodulesSeed{
			{Key: "users", Name: "Employees", Route: "/employees"},
			{Key: "departments", Name: "Departments", Route: "/departments"},
			{Key: "roles", Name: "Roles", Route: "/roles"},
			{Key: "kpis", Name: "KPIs & KRAs", Route: "/kpis"},
			{Key: "onboarding", Name: "Onboarding", Route: "/onboarding"},
			{Key: "shifts", Name: "Shift Management", Route: "/shifts"},
		},
	},
	{
		Key: "time-attendance", Name: "Time & Attendance", SortOrder: 3,
		Submodules: []submodulesSeed{
			{Key: "timesheets", Name: "Timesheets", Route: "/timesheets"},
			{Key: "attendance", Name: "Attendance Log", Route: "/attendance"},
			{Key: "gps-location", Name: "GPS & Location", Route: "/gps-location"},
			{Key: "hours-insights", Name: "Hours Insights", Route: "/hours-insights"},
		},
	},
	{
		Key: "productivity", Name: "Productivity", SortOrder: 4,
		Submodules: []submodulesSeed{
			{Key: "productivity-scoring", Name: "Score Card", Route: "/productivity-scoring"},
			{Key: "goals", Name: "Goals & OKRs", Route: "/goals"},
		},
	},
	{
		Key: "monitoring", Name: "Monitoring", SortOrder: 5,
		Submodules: []submodulesSeed{
			{Key: "employee-journey", Name: "Employee Journey", Route: "/employee-journey/timeline"},
			{Key: "device-specs", Name: "Device Specs", Route: "/device-specs"},
			{Key: "screenshots", Name: "Screenshots", Route: "/screenshots"},
			{Key: "logs", Name: "Logs", Route: "/logs/comprehensive"},
			{Key: "charts", Name: "Charts", Route: "/charts/productivity"},
			{Key: "live-stream", Name: "Live Stream", Route: "/live-stream"},
		},
	},
	{
		Key: "reports-analytics", Name: "Reports & Analytics", SortOrder: 6,
		Submodules: []submodulesSeed{
			{Key: "reports", Name: "Reports", Route: "/reports"},
			{Key: "audit-log", Name: "Audit Log", Route: "/audit-log"},
			{Key: "executive-dashboard", Name: "Executive Dashboard", Route: "/executive-dashboard"},
		},
	},
	{
		Key: "security-dlp", Name: "Security & DLP", SortOrder: 7,
		Submodules: []submodulesSeed{
			{Key: "dlp-alerts", Name: "DLP Alerts", Route: "/dlp-alerts"},
			{Key: "dlp-rules", Name: "DLP Rules", Route: "/dlp-rules"},
			{Key: "web-blocker", Name: "Web Blocker", Route: "/web-blocker"},
			{Key: "shadow-it", Name: "Shadow IT", Route: "/shadow-it"},
		},
	},
	{
		Key: "allowlists", Name: "Allowlists", SortOrder: 8,
		Submodules: []submodulesSeed{
			{Key: "allowlists/web", Name: "Web Allow Lists", Route: "/allowlists/web"},
			{Key: "allowlists/apps", Name: "App Allow List", Route: "/allowlists/apps"},
		},
	},
	{
		Key: "configuration", Name: "Configuration", SortOrder: 9,
		Submodules: []submodulesSeed{
			{Key: "configuration/apps", Name: "Applications Classification", Route: "/configuration/apps"},
			{Key: "configuration/websites", Name: "Websites Classification", Route: "/configuration/websites"},
			{Key: "configuration/categories", Name: "Categories & Types", Route: "/configuration/categories"},
			{Key: "configuration/productivity-rules", Name: "Productivity Rules", Route: "/configuration/productivity-rules"},
		},
	},
	{
		Key: "communication", Name: "Communication", SortOrder: 10,
		Submodules: []submodulesSeed{
			{Key: "emails", Name: "Emails & Alerts", Route: "/emails"},
			{Key: "projects", Name: "Projects", Route: "/projects"},
			{Key: "ai-summary", Name: "AI Summary", Route: "/ai-summary"},
		},
	},
	{
		Key: "settings-module", Name: "Settings", SortOrder: 11,
		Submodules: []submodulesSeed{
			{Key: "settings", Name: "General Settings", Route: "/settings"},
			{Key: "settings/tracking", Name: "Tracking Settings", Route: "/settings/tracking"},
			{Key: "settings/user-management", Name: "User Management", Route: "/settings/user-management"},
			{Key: "settings/notifications", Name: "Notification Config", Route: "/settings/notifications"},
			{Key: "settings/billing", Name: "Billing & Subscription", Route: "/settings/billing"},
			{Key: "settings/compliance", Name: "GDPR & Compliance", Route: "/settings/compliance"},
			{Key: "settings/security", Name: "Security Settings", Route: "/settings/security"},
		},
	},
}

// RBACService orchestrates roles, the module catalog and permission resolution.
type RBACService struct {
	repo *repository.RBACRepo
}

// NewRBACService creates a new RBACService.
func NewRBACService(repo *repository.RBACRepo) *RBACService {
	return &RBACService{repo: repo}
}

// SeedCatalog upserts the module/submodule catalog and guarantees the system
// role exists with every submodule granted. Safe to run on EVERY server start.
func (s *RBACService) SeedCatalog(ctx context.Context) error {
	for _, mod := range rbacCatalog {
		moduleID, err := s.repo.UpsertModule(ctx, mod.Key, mod.Name, mod.SortOrder)
		if err != nil {
			return err
		}
		for i, sub := range mod.Submodules {
			if _, err := s.repo.UpsertSubmodule(ctx, moduleID, sub.Key, sub.Name, sub.Route, i+1); err != nil {
				return err
			}
		}
	}

	// Drop permissions for routes that left the product. Idempotent on every boot.
	for _, key := range []string{"employee-portal", "users/activity", "settings/terms-and-conditions", "configuration/device-configuration"} {
		if err := s.repo.DeleteSubmoduleByKey(ctx, key); err != nil {
			return err
		}
	}

	roleID, err := s.repo.EnsureRole(ctx, SystemRoleName, "Full access to every module and submodule", true)
	if err != nil {
		return err
	}
	if err := s.repo.GrantAllPermissions(ctx, roleID); err != nil {
		return err
	}

	log.Printf("[rbac] catalog seeded (%d modules) — '%s' has full access", len(rbacCatalog), SystemRoleName)
	return nil
}

// ModuleTree returns the whole catalog for the roles UI and navigation guards.
func (s *RBACService) ModuleTree(ctx context.Context) (*dto.ModuleTreeResponse, error) {
	modules, err := s.repo.ListModules(ctx)
	if err != nil {
		return nil, err
	}
	nodes := make([]dto.ModuleNode, len(modules))
	total := 0
	for i, m := range modules {
		subs := make([]dto.SubmoduleNode, len(m.Submodules))
		for j, sub := range m.Submodules {
			subs[j] = dto.SubmoduleNode{
				ID: sub.ID, ModuleID: sub.ModuleID, Key: sub.Key,
				Name: sub.Name, RoutePath: sub.RoutePath,
			}
		}
		nodes[i] = dto.ModuleNode{ID: m.ID, Key: m.Key, Name: m.Name, SortOrder: m.SortOrder, Submodules: subs}
		total += len(subs)
	}
	return &dto.ModuleTreeResponse{Modules: nodes, Total: total}, nil
}

// List returns all roles as API responses.
func (s *RBACService) List(ctx context.Context) (*dto.RoleListResponse, error) {
	roles, err := s.repo.ListRoles(ctx)
	if err != nil {
		return nil, err
	}
	out := make([]dto.RoleResponse, len(roles))
	for i, r := range roles {
		out[i] = roleToResponse(&r)
	}
	return &dto.RoleListResponse{Roles: out, Total: len(out)}, nil
}

// Create adds a new role with its granted submodules.
func (s *RBACService) Create(ctx context.Context, req *dto.CreateRoleRequest) (*dto.RoleResponse, error) {
	subIDs := req.SubmoduleIDs
	if subIDs == nil {
		subIDs = []int{}
	}

	role, err := s.repo.CreateRole(ctx, req.Name, req.Description)
	if err != nil {
		return nil, err
	}
	if err := s.repo.ReplacePermissions(ctx, role.ID, subIDs); err != nil {
		return nil, err
	}
	role.SubmoduleIDs = subIDs
	resp := roleToResponse(role)
	return &resp, nil
}

// Update changes role metadata and/or the granted submodule set.
func (s *RBACService) Update(ctx context.Context, id int, req *dto.UpdateRoleRequest) (*dto.RoleResponse, error) {
	existing, err := s.repo.GetRoleByID(ctx, id)
	if err != nil {
		return nil, err
	}
	if existing == nil {
		return nil, fmt.Errorf("role not found")
	}
	if existing.IsSystem && (req.Name != nil || req.Description != nil || req.SubmoduleIDs != nil) {
		return nil, fmt.Errorf("system role cannot be modified")
	}

	if _, err := s.repo.UpdateRole(ctx, id, req.Name, req.Description); err != nil {
		return nil, err
	}
	if req.SubmoduleIDs != nil {
		if err := s.repo.ReplacePermissions(ctx, id, *req.SubmoduleIDs); err != nil {
			return nil, err
		}
	}

	fresh, err := s.repo.GetRoleByIDWithPerms(ctx, id)
	if err != nil {
		return nil, err
	}
	if fresh == nil {
		return nil, fmt.Errorf("role not found")
	}
	resp := roleToResponse(fresh)
	return &resp, nil
}

// Delete soft-deletes a role after guarding system roles and attached users.
func (s *RBACService) Delete(ctx context.Context, id int) error {
	existing, err := s.repo.GetRoleByID(ctx, id)
	if err != nil {
		return err
	}
	if existing == nil {
		return fmt.Errorf("role not found")
	}
	if existing.IsSystem {
		return fmt.Errorf("system role cannot be deleted")
	}
	hasUsers, err := s.repo.RoleHasUsers(ctx, id)
	if err != nil {
		return err
	}
	if hasUsers {
		return fmt.Errorf("role is still assigned to users")
	}
	return s.repo.DeleteRole(ctx, id)
}

func roleToResponse(r *models.Role) dto.RoleResponse {
	perms := r.Permissions
	if perms == nil {
		perms = []string{}
	}
	subIDs := r.SubmoduleIDs
	if subIDs == nil {
		subIDs = []int{}
	}
	return dto.RoleResponse{
		ID:           r.ID,
		Name:         r.Name,
		Description:  r.Description,
		IsSystem:     r.IsSystem,
		UserCount:    r.UserCount,
		SubmoduleIds: subIDs,
		Permissions:  perms,
	}
}
