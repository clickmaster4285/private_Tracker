package dto

// ────────────────────────────────
// Request DTOs
// ────────────────────────────────

// CreateRoleRequest is the payload for creating a role with its granted submodules.
type CreateRoleRequest struct {
	Name         string `json:"name" validate:"required,min=2,max=100"`
	Description  string `json:"description,omitempty"`
	SubmoduleIDs []int  `json:"submoduleIds"`
}

// UpdateRoleRequest partially updates a role (nil fields are left untouched).
type UpdateRoleRequest struct {
	Name         *string `json:"name,omitempty" validate:"omitempty,min=2,max=100"`
	Description  *string `json:"description,omitempty"`
	SubmoduleIDs *[]int  `json:"submoduleIds,omitempty"`
}

// ────────────────────────────────
// Response DTOs
// ────────────────────────────────

// SubmoduleNode is one selectable permission key inside a module.
type SubmoduleNode struct {
	ID        int    `json:"id"`
	ModuleID  int    `json:"moduleId"`
	Key       string `json:"key"`
	Name      string `json:"name"`
	RoutePath string `json:"routePath"`
}

// ModuleNode is a module group with its submodules.
type ModuleNode struct {
	ID         int             `json:"id"`
	Key        string          `json:"key"`
	Name       string          `json:"name"`
	SortOrder  int             `json:"sortOrder"`
	Submodules []SubmoduleNode `json:"submodules"`
}

// ModuleTreeResponse is the full module/submodule catalog for the roles UI + nav guards.
type ModuleTreeResponse struct {
	Modules []ModuleNode `json:"modules"`
	Total   int          `json:"total"`
}

// RoleResponse is the API shape of a role, including its grants.
type RoleResponse struct {
	ID           int      `json:"id"`
	Name         string   `json:"name"`
	Description  string   `json:"description"`
	IsSystem     bool     `json:"isSystem"`
	UserCount    int      `json:"userCount"`
	SubmoduleIds []int    `json:"submoduleIds"`
	Permissions  []string `json:"permissions"` // submodule KEYS — mirrors the auth payload
}

// RoleListResponse wraps the roles list.
type RoleListResponse struct {
	Roles []RoleResponse `json:"roles"`
	Total int            `json:"total"`
}
