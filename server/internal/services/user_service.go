package services

import (
	"context"
	"fmt"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"golang.org/x/crypto/bcrypt"
)

// UserService handles business logic for user operations.
type UserService struct {
	repo        *repository.UserRepo
	rbacRepo    *repository.RBACRepo
	employeeRepo *repository.EmployeeRepo
}

// NewUserService creates a new UserService.
func NewUserService(repo *repository.UserRepo, rbacRepo *repository.RBACRepo, employeeRepo *repository.EmployeeRepo) *UserService {
	return &UserService{repo: repo, rbacRepo: rbacRepo, employeeRepo: employeeRepo}
}

// List returns a paginated list of users (as response DTOs).
func (s *UserService) List(ctx context.Context, params repository.ListParams) (*dto.UserListResponse, error) {
	result, err := s.repo.List(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list users: %w", err)
	}

	users := make([]dto.UserResponse, len(result.Users))
	for i, u := range result.Users {
		users[i] = userToResponse(&u)
	}

	return &dto.UserListResponse{
		Data:       users,
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}

// GetByID returns a single user by ID.
func (s *UserService) GetByID(ctx context.Context, id string) (*dto.UserResponse, error) {
	user, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return nil, fmt.Errorf("get user: %w", err)
	}
	if user == nil {
		return nil, nil
	}
	resp := userToResponse(user)
	return &resp, nil
}

// Create creates a new user with bcrypt-hashed password, attached to a role.
func (s *UserService) Create(ctx context.Context, req *dto.CreateUserRequest) (*dto.UserResponse, error) {
	// Check email uniqueness
	unique, err := s.repo.IsUniqueEmail(ctx, req.Email, "")
	if err != nil {
		return nil, err
	}
	if !unique {
		return nil, fmt.Errorf("email already exists")
	}
	if req.RoleID <= 0 {
		return nil, fmt.Errorf("a valid role is required")
	}

	// Default password if not provided
	password := req.Password
	if password == "" {
		password = "employee@123" // default password
	}

	hashedPassword, err := bcrypt.GenerateFromPassword([]byte(password), bcrypt.DefaultCost)
	if err != nil {
		return nil, fmt.Errorf("hash password: %w", err)
	}

	shift := req.Shift
	if shift == "" {
		shift = "Day"
	}

	trackingEnabled := true
	if req.TrackingEnabled != nil {
		trackingEnabled = *req.TrackingEnabled
	}

	user := &models.User{
		Name:            req.Name,
		Email:           req.Email,
		EmployeeID:      req.EmployeeID,
		PasswordHash:    string(hashedPassword),
		RoleID:          req.RoleID,
		Shift:           shift,
		TrackingEnabled: trackingEnabled,
		TrackingStatus:  "untracked",
		IsOnline:        false,
	}

	created, err := s.repo.Create(ctx, user)
	if err != nil {
		return nil, fmt.Errorf("create user: %w", err)
	}

	resp := userToResponse(created)
	return &resp, nil
}

// Update partial updates a user.
func (s *UserService) Update(ctx context.Context, id string, req *dto.UpdateUserRequest) (*dto.UserResponse, error) {
	// Check user exists
	existing, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return nil, err
	}
	if existing == nil {
		return nil, nil
	}

	updates := make(map[string]interface{})

	if req.Name != nil {
		updates["name"] = *req.Name
	}
	if req.Email != nil {
		// Check email uniqueness
		unique, err := s.repo.IsUniqueEmail(ctx, *req.Email, id)
		if err != nil {
			return nil, err
		}
		if !unique {
			return nil, fmt.Errorf("email already exists")
		}
		updates["email"] = *req.Email
	}
	if req.RoleID != nil {
		if *req.RoleID <= 0 {
			return nil, fmt.Errorf("a valid role is required")
		}
		updates["role_id"] = *req.RoleID
	}
	if req.Password != nil && *req.Password != "" {
		hashedPassword, err := bcrypt.GenerateFromPassword([]byte(*req.Password), bcrypt.DefaultCost)
		if err != nil {
			return nil, fmt.Errorf("hash password: %w", err)
		}
		passwordHash := string(hashedPassword)
		updates["password_hash"] = passwordHash
	}
	if req.Shift != nil {
		updates["shift"] = *req.Shift
	}
	if req.TrackingEnabled != nil {
		updates["tracking_enabled"] = *req.TrackingEnabled
	}
	if req.TrackingStatus != nil {
		updates["tracking_status"] = *req.TrackingStatus
	}
	if req.IsOnline != nil {
		updates["is_online"] = *req.IsOnline
	}
	if req.Status != nil {
		status := strings.TrimSpace(*req.Status)
		if status != "active" && status != "suspended" {
			return nil, fmt.Errorf("invalid status")
		}
		// Suspending the system company_admin would lock every admin out of the
		// dashboard — the same guard Delete applies. Reactivating always works.
		if status == "suspended" {
			isSystem, err := s.repo.IsUserRoleSystem(ctx, id)
			if err != nil {
				return nil, err
			}
			if isSystem {
				return nil, fmt.Errorf("cannot suspend company admin")
			}
		}
		updates["status"] = status
	}

	updated, err := s.repo.Update(ctx, id, updates)
	if err != nil {
		return nil, fmt.Errorf("update user: %w", err)
	}
	if updated == nil {
		return nil, nil
	}

	resp := userToResponse(updated)
	return &resp, nil
}

// Delete removes a user by ID. Prevents deleting users on system roles (company_admin).
func (s *UserService) Delete(ctx context.Context, id string) error {
	user, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return err
	}
	if user == nil {
		return fmt.Errorf("user not found")
	}
	isSystem, err := s.repo.IsUserRoleSystem(ctx, id)
	if err != nil {
		return err
	}
	if isSystem {
		return fmt.Errorf("cannot delete company admin")
	}
	return s.repo.Delete(ctx, id)
}

// GetProfile returns the aggregate profile payload (user + role + RBAC view
// + linked employee) for the /settings/profile page. It is a single-shot
// composition so the web only needs one request to render the entire view.
func (s *UserService) GetProfile(ctx context.Context, userID string) (*dto.ProfileResponse, error) {
	user, err := s.repo.GetByID(ctx, userID)
	if err != nil {
		return nil, fmt.Errorf("get user: %w", err)
	}
	if user == nil {
		return nil, nil
	}
	resp := &dto.ProfileResponse{User: userToResponse(user)}

	// Role details (id, name, description, isSystem). `company_admin` is the
	// only system role; users on it are locked from the admin edit surface
	// but can still use the self-service profile page.
	if s.rbacRepo != nil {
		role, err := s.rbacRepo.GetRoleByIDWithPerms(ctx, user.RoleID)
		if err == nil && role != nil {
			r := roleToResponse(role)
			resp.Role = &r
		}
	}

	// Granted submodule keys + a per-module breakdown of "how many submodules
	// you can reach inside this navigation section". Derived from the RBAC
	// catalog joined with PermissionKeysForUser — no hardcoded module names.
	granted := map[string]bool{}
	if s.rbacRepo != nil {
		if keys, err := s.rbacRepo.PermissionKeysForUser(ctx, user.ID); err == nil {
			for _, k := range keys {
				granted[k] = true
			}
		}
		modules, err := s.rbacRepo.ListModules(ctx)
		if err == nil {
			profileMods := make([]dto.ProfileModule, 0, len(modules))
			for _, m := range modules {
				grantedCount := 0
				for _, sub := range m.Submodules {
					if granted[sub.Key] {
						grantedCount++
					}
				}
				profileMods = append(profileMods, dto.ProfileModule{
					ID:             m.ID,
					Key:            m.Key,
					Name:           m.Name,
					GrantedCount:   grantedCount,
					SubmoduleCount: len(m.Submodules),
				})
			}
			resp.Permissions.Modules = profileMods
		}
	}
	if resp.Permissions.SubmoduleKeys == nil {
		resp.Permissions.SubmoduleKeys = []string{}
	}
	// isSystemAdmin is the only company-wide bypass — the role has every
	// submodule granted at seed time. The flag drives the company_admin lock
	// messaging in the profile UI.
	isSystemAdmin := resp.Role != nil && resp.Role.IsSystem
	resp.Permissions.IsSystemAdmin = isSystemAdmin

	// Linked employee record (department, shift, hasUserLogin). Resolved by
	// the public employee_id (e.g. EMP-00001) — the same join key the user
	// rows carry. Nil when the user has no employee link.
	if s.employeeRepo != nil && user.EmployeeID != "" {
		emp, err := s.employeeRepo.GetByEmployeeID(ctx, user.EmployeeID)
		if err == nil && emp != nil {
			er := employeeToResponse(emp)
			resp.Employee = &er
		}
	}

	return resp, nil
}

func userToResponse(u *models.User) dto.UserResponse {
	return dto.UserResponse{
		ID:              u.ID,
		EmployeeID:      u.EmployeeID,
		Name:            u.Name,
		Email:           u.Email,
		RoleID:          u.RoleID,
		Role:            u.RoleName,
		Shift:           u.Shift,
		TrackingEnabled: u.TrackingEnabled,
		TrackingStatus:  u.TrackingStatus,
		Status:          u.Status,
		IsOnline:        u.IsOnline,
		Avatar:          u.Avatar,
		AvatarColor:     u.AvatarColor,
		CreatedAt:       u.CreatedAt,
		UpdatedAt:       u.UpdatedAt,
		DeletedAt:       u.DeletedAt,
	}
}
