package redis

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	goredis "github.com/redis/go-redis/v9"
)

// ErrTooManyTickets is returned when the cluster-wide pending ticket cap is hit.
var ErrTooManyTickets = errors.New("too many pending watch tickets")

const (
	PresenceKeyPrefix  = "presence:emp:"
	PublisherKeyPrefix = "stream:pub:"
	WatchTicketPrefix  = "stream:ticket:"
	PresenceChannel    = "alpha:presence"
	StreamRouteChannel = "alpha:stream_route"

	PresenceTTL       = 90 * time.Second
	WatchTicketTTL    = 60 * time.Second
	PublisherTTL      = 24 * time.Hour // refreshed on register; cleared on unregister
	MaxClusterTickets = 2000
)

// PresenceEvent is published on PresenceChannel when an employee goes online/offline.
type PresenceEvent struct {
	Type       string `json:"type"` // "online" | "offline"
	EmployeeID string `json:"employeeId"`
	InstanceID string `json:"instanceId"`
}

// TicketPayload is stored under WatchTicketPrefix.
type TicketPayload struct {
	UserID     string `json:"userId"`
	EmployeeID string `json:"employeeId"`
}

// Underlying returns the raw go-redis client (for Pub/Sub subscribers).
func (c *Client) Underlying() *goredis.Client {
	if c == nil {
		return nil
	}
	return c.client
}

// SetPresence marks empID online on instanceID and publishes an online event.
func (c *Client) SetPresence(ctx context.Context, empID, instanceID string) error {
	if c == nil || empID == "" || instanceID == "" {
		return nil
	}
	key := PresenceKeyPrefix + empID
	if err := c.client.Set(ctx, key, instanceID, PresenceTTL).Err(); err != nil {
		return fmt.Errorf("redis set presence: %w", err)
	}
	_ = c.publishPresence(ctx, PresenceEvent{Type: "online", EmployeeID: empID, InstanceID: instanceID})
	return nil
}

// TouchPresence refreshes the presence TTL when the control socket is still alive.
func (c *Client) TouchPresence(ctx context.Context, empID, instanceID string) error {
	if c == nil || empID == "" {
		return nil
	}
	key := PresenceKeyPrefix + empID
	// Only refresh if we still own the key (or it is missing → re-claim).
	cur, err := c.client.Get(ctx, key).Result()
	if err == goredis.Nil {
		return c.SetPresence(ctx, empID, instanceID)
	}
	if err != nil {
		return err
	}
	if cur != instanceID {
		return nil // another instance owns presence
	}
	return c.client.Expire(ctx, key, PresenceTTL).Err()
}

// ClearPresence removes online state only when this instance still owns it.
func (c *Client) ClearPresence(ctx context.Context, empID, instanceID string) error {
	if c == nil || empID == "" {
		return nil
	}
	key := PresenceKeyPrefix + empID
	cur, err := c.client.Get(ctx, key).Result()
	if err == goredis.Nil {
		return nil
	}
	if err != nil {
		return err
	}
	if cur != instanceID {
		return nil
	}
	if err := c.client.Del(ctx, key).Err(); err != nil {
		return err
	}
	_ = c.publishPresence(ctx, PresenceEvent{Type: "offline", EmployeeID: empID, InstanceID: instanceID})
	return nil
}

// IsPresent reports whether empID has a non-expired presence key.
func (c *Client) IsPresent(ctx context.Context, empID string) (bool, error) {
	if c == nil || empID == "" {
		return false, nil
	}
	n, err := c.client.Exists(ctx, PresenceKeyPrefix+empID).Result()
	if err != nil {
		return false, err
	}
	return n > 0, nil
}

// SetPublisher records which instance holds the push socket / SFU room for empID.
func (c *Client) SetPublisher(ctx context.Context, empID, instanceID string) error {
	if c == nil || empID == "" || instanceID == "" {
		return nil
	}
	key := PublisherKeyPrefix + empID
	if err := c.client.Set(ctx, key, instanceID, PublisherTTL).Err(); err != nil {
		return fmt.Errorf("redis set publisher: %w", err)
	}
	payload, _ := json.Marshal(map[string]string{
		"type": "publisher_up", "employeeId": empID, "instanceId": instanceID,
	})
	_ = c.client.Publish(ctx, StreamRouteChannel, payload).Err()
	return nil
}

// ClearPublisher removes the publisher registry entry when owned by instanceID.
func (c *Client) ClearPublisher(ctx context.Context, empID, instanceID string) error {
	if c == nil || empID == "" {
		return nil
	}
	key := PublisherKeyPrefix + empID
	cur, err := c.client.Get(ctx, key).Result()
	if err == goredis.Nil {
		return nil
	}
	if err != nil {
		return err
	}
	if cur != instanceID {
		return nil
	}
	if err := c.client.Del(ctx, key).Err(); err != nil {
		return err
	}
	payload, _ := json.Marshal(map[string]string{
		"type": "publisher_down", "employeeId": empID, "instanceId": instanceID,
	})
	_ = c.client.Publish(ctx, StreamRouteChannel, payload).Err()
	return nil
}

// LookupPublisher returns the instanceID holding empID's publisher, if any.
func (c *Client) LookupPublisher(ctx context.Context, empID string) (instanceID string, ok bool, err error) {
	if c == nil || empID == "" {
		return "", false, nil
	}
	v, err := c.client.Get(ctx, PublisherKeyPrefix+empID).Result()
	if err == goredis.Nil {
		return "", false, nil
	}
	if err != nil {
		return "", false, err
	}
	return v, v != "", nil
}

// InstancePublicURL looks up a peer instance's advertised base URL.
// Key: stream:instance:{instanceID} → public URL string.
func (c *Client) SetInstanceURL(ctx context.Context, instanceID, publicURL string) error {
	if c == nil || instanceID == "" || publicURL == "" {
		return nil
	}
	return c.client.Set(ctx, "stream:instance:"+instanceID, publicURL, PublisherTTL).Err()
}

// GetInstanceURL returns the public base URL for an instance (for watch redirects).
func (c *Client) GetInstanceURL(ctx context.Context, instanceID string) (string, error) {
	if c == nil || instanceID == "" {
		return "", nil
	}
	v, err := c.client.Get(ctx, "stream:instance:"+instanceID).Result()
	if err == goredis.Nil {
		return "", nil
	}
	return v, err
}

// StoreWatchTicket writes a one-shot watch ticket into Redis.
func (c *Client) StoreWatchTicket(ctx context.Context, ticket, userID, employeeID string) error {
	if c == nil {
		return fmt.Errorf("redis unavailable")
	}
	n, err := c.countWatchTickets(ctx)
	if err != nil {
		return err
	}
	if n >= MaxClusterTickets {
		return ErrTooManyTickets
	}
	raw, err := json.Marshal(TicketPayload{UserID: userID, EmployeeID: employeeID})
	if err != nil {
		return err
	}
	return c.client.Set(ctx, WatchTicketPrefix+ticket, raw, WatchTicketTTL).Err()
}

// ConsumeWatchTicket validates and burns a Redis watch ticket.
func (c *Client) ConsumeWatchTicket(ctx context.Context, ticket, employeeID string) (userID string, err error) {
	if c == nil {
		return "", fmt.Errorf("redis unavailable")
	}
	key := WatchTicketPrefix + ticket
	// GET+DEL (not GETDEL) — Redis < 6.2 is common on Windows/dev boxes.
	raw, err := c.client.Get(ctx, key).Bytes()
	if err == goredis.Nil {
		return "", fmt.Errorf("invalid or expired watch ticket")
	}
	if err != nil {
		return "", err
	}
	_ = c.client.Del(ctx, key).Err()
	var p TicketPayload
	if err := json.Unmarshal(raw, &p); err != nil {
		return "", fmt.Errorf("invalid or expired watch ticket")
	}
	if p.EmployeeID != employeeID {
		return "", fmt.Errorf("invalid or expired watch ticket")
	}
	return p.UserID, nil
}

func (c *Client) countWatchTickets(ctx context.Context) (int, error) {
	var cursor uint64
	total := 0
	for {
		keys, next, err := c.client.Scan(ctx, cursor, WatchTicketPrefix+"*", 200).Result()
		if err != nil {
			return 0, err
		}
		total += len(keys)
		cursor = next
		if cursor == 0 {
			break
		}
		if total >= MaxClusterTickets {
			break
		}
	}
	return total, nil
}

func (c *Client) publishPresence(ctx context.Context, ev PresenceEvent) error {
	raw, err := json.Marshal(ev)
	if err != nil {
		return err
	}
	return c.client.Publish(ctx, PresenceChannel, raw).Err()
}
