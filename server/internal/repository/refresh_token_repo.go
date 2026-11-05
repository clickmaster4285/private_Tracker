package repository

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

// RefreshTokenRepo handles persistence of web-admin refresh tokens (hashed at rest).
type RefreshTokenRepo struct {
	pool *pgxpool.Pool
}

// NewRefreshTokenRepo creates a new RefreshTokenRepo.
func NewRefreshTokenRepo(pool *pgxpool.Pool) *RefreshTokenRepo {
	return &RefreshTokenRepo{pool: pool}
}

const refreshTokenColumns = `id, user_id, token_hash, expires_at, revoked_at, created_at, deleted_at`

func scanRefreshToken(row pgx.Row) (*models.RefreshToken, error) {
	var t models.RefreshToken
	err := row.Scan(&t.ID, &t.UserID, &t.TokenHash, &t.ExpiresAt, &t.RevokedAt, &t.CreatedAt, &t.DeletedAt)
	if err != nil {
		return nil, err
	}
	return &t, nil
}

// Create stores a new refresh token hash for the user.
func (r *RefreshTokenRepo) Create(ctx context.Context, userID, tokenHash string, expiresAt time.Time) error {
	_, err := r.pool.Exec(ctx,
		`INSERT INTO refresh_tokens (user_id, token_hash, expires_at) VALUES ($1, $2, $3)`,
		userID, tokenHash, expiresAt)
	if err != nil {
		return fmt.Errorf("insert refresh token: %w", err)
	}
	return nil
}

// GetValidByHash returns the token only when it exists, is not revoked/deleted and not expired.
func (r *RefreshTokenRepo) GetValidByHash(ctx context.Context, tokenHash string) (*models.RefreshToken, error) {
	row := r.pool.QueryRow(ctx,
		`SELECT `+refreshTokenColumns+`
		 FROM refresh_tokens
		 WHERE token_hash = $1 AND deleted_at IS NULL AND revoked_at IS NULL AND expires_at > NOW()`,
		tokenHash)
	t, err := scanRefreshToken(row)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("get refresh token: %w", err)
	}
	return t, nil
}

// RevokeByHash marks the token rotated/used. Returns whether a live row was found.
func (r *RefreshTokenRepo) RevokeByHash(ctx context.Context, tokenHash string) (bool, error) {
	tag, err := r.pool.Exec(ctx,
		`UPDATE refresh_tokens SET revoked_at = NOW()
		 WHERE token_hash = $1 AND deleted_at IS NULL AND revoked_at IS NULL`,
		tokenHash)
	if err != nil {
		return false, fmt.Errorf("revoke refresh token: %w", err)
	}
	return tag.RowsAffected() > 0, nil
}

// DeleteExpired purges tokens that expired before cutoff (retention hook).
func (r *RefreshTokenRepo) DeleteExpired(ctx context.Context, cutoff time.Time) (int64, error) {
	tag, err := r.pool.Exec(ctx,
		`DELETE FROM refresh_tokens WHERE expires_at < $1`, cutoff)
	if err != nil {
		return 0, fmt.Errorf("delete expired refresh tokens: %w", err)
	}
	return tag.RowsAffected(), nil
}
