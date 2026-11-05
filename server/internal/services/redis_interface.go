package services

import "context"

// RedisClientInterface defines the methods the auth handler needs from Redis.
type RedisClientInterface interface {
	StoreSecret(ctx context.Context, employeeID string, secret string) error
	ValidateSecret(ctx context.Context, employeeID string, secret string) (bool, error)
	DeleteSecret(ctx context.Context, employeeID string) error
}
