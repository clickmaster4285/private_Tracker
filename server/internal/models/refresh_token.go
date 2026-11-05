package models

import "time"

// RefreshToken is one issued web-admin refresh token (only its SHA-256 hash is stored).
// Rotation semantics: each successful refresh revokes the presented row and mints a new one.
type RefreshToken struct {
	ID        int64      `json:"id" db:"id"`
	UserID    string     `json:"userId" db:"user_id"`
	TokenHash string     `json:"-" db:"token_hash"`
	ExpiresAt time.Time  `json:"expiresAt" db:"expires_at"`
	RevokedAt *time.Time `json:"revokedAt,omitempty" db:"revoked_at"`
	CreatedAt time.Time  `json:"createdAt" db:"created_at"`
	DeletedAt *time.Time `json:"-" db:"deleted_at"`
}
