package redis

import (
	"context"
	"fmt"
	"time"

	goredis "github.com/redis/go-redis/v9"
)

const (
	SecretKeyPrefix = "employee_secret:"
	SecretTTL       = 5 * time.Minute
)

// Client wraps the Redis client for employee authentication secrets.
type Client struct {
	client *goredis.Client
}

// NewClient creates a new Redis client.
func NewClient(addr string, password string, db int) (*Client, error) {
	rdb := goredis.NewClient(&goredis.Options{
		Addr:     addr,
		Password: password,
		DB:       db,
	})

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	if err := rdb.Ping(ctx).Err(); err != nil {
		return nil, fmt.Errorf("redis ping: %w", err)
	}

	return &Client{client: rdb}, nil
}

// StoreSecret stores a login secret for an employee with a 5-minute TTL.
func (c *Client) StoreSecret(ctx context.Context, employeeID string, secret string) error {
	key := SecretKeyPrefix + employeeID
	return c.client.Set(ctx, key, secret, SecretTTL).Err()
}

// ValidateSecret checks if a secret is valid for an employee and removes it on success.
func (c *Client) ValidateSecret(ctx context.Context, employeeID string, secret string) (bool, error) {
	key := SecretKeyPrefix + employeeID
	stored, err := c.client.Get(ctx, key).Result()
	if err != nil {
		if err == goredis.Nil {
			return false, nil // key not found
		}
		return false, fmt.Errorf("redis get: %w", err)
	}

	if stored != secret {
		return false, nil
	}

	// Delete the secret after successful validation (one-time use)
	c.client.Del(ctx, key)
	return true, nil
}

// DeleteSecret removes a login secret for an employee.
func (c *Client) DeleteSecret(ctx context.Context, employeeID string) error {
	key := SecretKeyPrefix + employeeID
	return c.client.Del(ctx, key).Err()
}

// Close closes the Redis connection.
func (c *Client) Close() error {
	return c.client.Close()
}
