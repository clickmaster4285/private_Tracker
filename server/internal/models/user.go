package models

import "time"

// User represents a web-dashboard login account (admin or staff).
// The role is referenced by role_id — the roles table is the single source of truth.
type User struct {
	ID              string      `json:"id" db:"id"`
	EmployeeID      string      `json:"employeeId" db:"employee_id"`
	Name            string      `json:"name" db:"name"`
	Email           string      `json:"email" db:"email"`
	PasswordHash    string      `json:"-" db:"password_hash"`
	RoleID          int         `json:"roleId" db:"role_id"`
	RoleName        string      `json:"roleName" db:"role_name"` // derived via JOIN at read time
	Shift           string      `json:"shift" db:"shift"`
	TrackingEnabled bool        `json:"trackingEnabled" db:"tracking_enabled"`
	TrackingStatus  string      `json:"trackingStatus" db:"tracking_status"`
	Status          string      `json:"status" db:"status"` // ACCOUNT status: active | suspended
	IsOnline        bool        `json:"isOnline" db:"is_online"`
	Avatar          string      `json:"avatar" db:"avatar"`
	AvatarColor     string      `json:"avatarColor" db:"avatar_color"`
	CreatedAt       time.Time   `json:"createdAt" db:"created_at"`
	UpdatedAt       time.Time   `json:"updatedAt" db:"updated_at"`
	DeletedAt       *time.Time  `json:"deletedAt" db:"deleted_at"`
}

// UserPublic is the public-facing user info (no password hash, safe for API responses).
type UserPublic struct {
	ID              string    `json:"id"`
	EmployeeID      string    `json:"employeeId"`
	Name            string    `json:"name"`
	Email           string    `json:"email"`
	RoleID          int       `json:"roleId"`
	RoleName        string    `json:"roleName"`
	Shift           string    `json:"shift"`
	TrackingEnabled bool      `json:"trackingEnabled"`
	TrackingStatus  string    `json:"trackingStatus"`
	Status          string    `json:"status"`
	IsOnline        bool      `json:"isOnline"`
	Avatar          string    `json:"avatar"`
	AvatarColor     string    `json:"avatarColor"`
	Permissions     []string  `json:"permissions,omitempty"`
	CreatedAt       time.Time `json:"createdAt"`
	UpdatedAt       time.Time `json:"updatedAt"`
}

// ToPublic converts a User to UserPublic (safe for API responses).
func (u *User) ToPublic() UserPublic {
	return UserPublic{
		ID:              u.ID,
		EmployeeID:      u.EmployeeID,
		Name:            u.Name,
		Email:           u.Email,
		RoleID:          u.RoleID,
		RoleName:        u.RoleName,
		Shift:           u.Shift,
		TrackingEnabled: u.TrackingEnabled,
		TrackingStatus:  u.TrackingStatus,
		Status:          u.Status,
		IsOnline:        u.IsOnline,
		Avatar:          u.Avatar,
		AvatarColor:     u.AvatarColor,
		CreatedAt:       u.CreatedAt,
		UpdatedAt:       u.UpdatedAt,
	}
}
