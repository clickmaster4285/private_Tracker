package dto

import "time"

// ────────────────────────────────
// Request DTOs
// ────────────────────────────────

// CreateUserRequest is the payload for creating a new user.
type CreateUserRequest struct {
	Name            string `json:"name" validate:"required,min=2,max=255"`
	Email           string `json:"email" validate:"required,email"`
	Password        string `json:"password,omitempty" validate:"omitempty,min=6"`
	EmployeeID      string `json:"employeeId,omitempty"`
	RoleID          int    `json:"roleId" validate:"required"`
	Shift           string `json:"shift,omitempty" validate:"omitempty"`
	TrackingEnabled *bool  `json:"trackingEnabled"`
}

// UpdateUserRequest is the payload for updating a user.
type UpdateUserRequest struct {
	Name            *string `json:"name,omitempty" validate:"omitempty,min=2,max=255"`
	Email           *string `json:"email,omitempty" validate:"omitempty,email"`
	RoleID          *int    `json:"roleId,omitempty"`
	Password        *string `json:"password,omitempty" validate:"omitempty,min=6"`
	Shift           *string `json:"shift,omitempty" validate:"omitempty"`
	TrackingEnabled *bool   `json:"trackingEnabled"`
	TrackingStatus  *string `json:"trackingStatus,omitempty"`
	IsOnline        *bool   `json:"isOnline,omitempty"`
	Status          *string `json:"status,omitempty"` // ACCOUNT status: active | suspended
}

// LoginRequest is the payload for user authentication.
type LoginRequest struct {
	Email    string `json:"email" validate:"required,email"`
	Password string `json:"password" validate:"required,min=1"`
}

// ────────────────────────────────
// Response DTOs
// ────────────────────────────────

// UserResponse is the public API response for a user.
type UserResponse struct {
	ID              string      `json:"id"`
	EmployeeID      string      `json:"employeeId"`
	Name            string      `json:"name"`
	Email           string      `json:"email"`
	RoleID          int         `json:"roleId"`
	Role            string      `json:"role"` // role NAME resolved from roles table
	Shift           string      `json:"shift"`
	TrackingEnabled bool        `json:"trackingEnabled"`
	TrackingStatus  string      `json:"trackingStatus"`
	Status          string      `json:"status"` // account status: active | suspended
	IsOnline        bool        `json:"isOnline"`
	Avatar          string      `json:"avatar"`
	AvatarColor     string      `json:"avatarColor"`
	Permissions     []string    `json:"permissions,omitempty"`
	CreatedAt       time.Time   `json:"createdAt"`
	UpdatedAt       time.Time   `json:"updatedAt"`
	DeletedAt       *time.Time  `json:"deletedAt"`
}

// UserListResponse is a paginated list response.
type UserListResponse struct {
	Data       []UserResponse `json:"data"`
	Total      int            `json:"total"`
	Page       int            `json:"page"`
	PerPage    int            `json:"perPage"`
	TotalPages int            `json:"totalPages"`
}

// LoginResponse is returned on successful login.
type LoginResponse struct {
	User  UserResponse `json:"user"`
	Token string       `json:"token,omitempty"` // Token is returned for reference; actual auth is via cookie

	// Refresh-token fields are transport-only (the handler turns them into an
	// httpOnly cookie) and are deliberately excluded from JSON bodies.
	RefreshToken     string    `json:"-"`
	RefreshExpiresAt time.Time `json:"-"`
}

// AuthCheckResponse is returned when checking current auth status.
type AuthCheckResponse struct {
	Authenticated bool        `json:"authenticated"`
	User          interface{} `json:"user,omitempty"`
}

// APIError is a standard error response.
type APIError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Detail  string `json:"detail,omitempty"`
}

// HealthResponse is the health check endpoint response.
type HealthResponse struct {
	Status    string `json:"status"`
	Timestamp string `json:"timestamp"`
	Database  string `json:"database"`
}

// ────────────────────────────────
// Self-service profile (web → GET /api/v1/auth/profile)
// ────────────────────────────────

// ProfileModule is a navigation module the user can see, with the count of
// granted submodules under it. Drives the "Modules you can access" view on
// the /settings/profile page (no hardcoded module names — derived from the
// RBAC catalog joined with the granted permission keys).
type ProfileModule struct {
	ID              int    `json:"id"`
	Key             string `json:"key"`
	Name            string `json:"name"`
	GrantedCount    int    `json:"grantedCount"`
	SubmoduleCount  int    `json:"submoduleCount"`
}

// ProfilePermissions is the read-only RBAC view attached to the profile:
// the granted submodule keys, the list of navigation modules the user can
// reach (and how many submodules are granted inside each), plus a
// isSystemAdmin convenience flag used by the UI to render the company_admin
// lock state.
type ProfilePermissions struct {
	SubmoduleKeys []string        `json:"submoduleKeys"`
	Modules       []ProfileModule `json:"modules"`
	IsSystemAdmin bool            `json:"isSystemAdmin"`
}

// ProfileResponse is the aggregate read-only payload returned by
// GET /api/v1/auth/profile. The web `/settings/profile` page renders the
// User, Role, Permissions, and Employee blocks directly from this shape
// (and falls back to /auth/me if it 404s on older server builds).
type ProfileResponse struct {
	User        UserResponse        `json:"user"`
	Role        *RoleResponse       `json:"role,omitempty"`
	Permissions ProfilePermissions  `json:"permissions"`
	Employee    *EmployeeResponse   `json:"employee,omitempty"`
}
