package cache

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	goredis "github.com/redis/go-redis/v9"
	"golang.org/x/sync/singleflight"
)

// Cache is a thin Redis JSON cache with singleflight coalescing.
// Nil-safe: when redis is nil, GetOrLoad always calls load.
type Cache struct {
	rdb *goredis.Client
	sf  singleflight.Group
}

// New returns a Cache. rdb may be nil (passthrough).
func New(rdb *goredis.Client) *Cache {
	return &Cache{rdb: rdb}
}

// GetOrLoad returns cached JSON for key, or runs load once (singleflight) and stores it for ttl.
func (c *Cache) GetOrLoad(ctx context.Context, key string, ttl time.Duration, dest any, load func(context.Context) (any, error)) error {
	if c == nil || c.rdb == nil {
		v, err := load(ctx)
		if err != nil {
			return err
		}
		return copyJSON(v, dest)
	}

	if b, err := c.rdb.Get(ctx, key).Bytes(); err == nil {
		return json.Unmarshal(b, dest)
	}

	v, err, _ := c.sf.Do(key, func() (any, error) {
		// Double-check after winning the flight.
		if b, err := c.rdb.Get(ctx, key).Bytes(); err == nil {
			var raw json.RawMessage = b
			return raw, nil
		}
		val, err := load(ctx)
		if err != nil {
			return nil, err
		}
		payload, err := json.Marshal(val)
		if err != nil {
			return nil, err
		}
		_ = c.rdb.Set(ctx, key, payload, ttl).Err()
		return json.RawMessage(payload), nil
	})
	if err != nil {
		return err
	}
	switch x := v.(type) {
	case json.RawMessage:
		return json.Unmarshal(x, dest)
	default:
		return copyJSON(x, dest)
	}
}

// Invalidate deletes a key (best-effort).
func (c *Cache) Invalidate(ctx context.Context, key string) {
	if c == nil || c.rdb == nil {
		return
	}
	_ = c.rdb.Del(ctx, key).Err()
}

func copyJSON(src any, dest any) error {
	b, err := json.Marshal(src)
	if err != nil {
		return err
	}
	return json.Unmarshal(b, dest)
}

// DashboardKey builds a cache key for GET /dashboard/summary.
func DashboardKey(from, to time.Time, departmentID *int, topN, recentLimit int) string {
	dept := 0
	if departmentID != nil {
		dept = *departmentID
	}
	return fmt.Sprintf("cache:dash:summary:%d:%d:%d:%d:%d",
		from.Unix(), to.Unix(), dept, topN, recentLimit)
}
