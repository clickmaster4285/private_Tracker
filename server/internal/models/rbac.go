package models

import "time"

// Role is an RBAC role referencing a set of granted submodules.
type Role struct {
	ID           int       `json:"id" db:"id"`
	Name         string    `json:"name" db:"name"`
	Description  string    `json:"description" db:"description"`
	IsSystem     bool      `json:"isSystem" db:"is_system"`
	SubmoduleIDs []int     `json:"-"`
	Permissions  []string  `json:"-"`
	UserCount    int       `json:"-"`
	CreatedAt    time.Time `json:"createdAt" db:"created_at"`
	UpdatedAt    time.Time `json:"updatedAt" db:"updated_at"`
}

// Submodule is a concrete permission key under a module (e.g. settings/user-management).
type Submodule struct {
	ID        int    `json:"id" db:"id"`
	ModuleID  int    `json:"moduleId" db:"module_id"`
	Key       string `json:"key" db:"key"`
	Name      string `json:"name" db:"name"`
	RoutePath string `json:"routePath" db:"route_path"`
}

// Module groups submodules into a navigation section (HR, Monitoring, Settings, ...).
type Module struct {
	ID         int         `json:"id" db:"id"`
	Key        string      `json:"key" db:"key"`
	Name       string      `json:"name" db:"name"`
	SortOrder  int         `json:"sortOrder" db:"sort_order"`
	Submodules []Submodule `json:"submodules"`
}
