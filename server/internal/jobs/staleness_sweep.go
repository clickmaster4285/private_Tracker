package jobs

import (
	"context"
	"log"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// StalenessSweep deactivates employee↔app and employee↔package link rows whose
// last_seen_at is older than the configured stale window. Because clients re-sync each
// detected app/package (refreshing last_seen_at and is_active=true), any link that stops
// appearing for `staleDays` is treated as uninstalled and deactivated.
type StalenessSweep struct {
	pool      *pgxpool.Pool
	staleDays int
}

func NewStalenessSweep(pool *pgxpool.Pool, staleDays int) *StalenessSweep {
	if staleDays <= 0 {
		staleDays = 7
	}
	return &StalenessSweep{pool: pool, staleDays: staleDays}
}

// Run performs a single deactivation pass over both junction tables.
func (s *StalenessSweep) Run(ctx context.Context) error {
	if s.pool == nil {
		return nil
	}
	cutoff := time.Now().Add(-time.Duration(s.staleDays) * 24 * time.Hour)

	var apps, pkgs int64
	if err := s.pool.QueryRow(ctx,
		`UPDATE employee_installed_applications
		   SET is_active = false
		 WHERE is_active AND last_seen_at < $1`,
		cutoff,
	).Scan(&apps); err != nil {
		return err
	}
	if err := s.pool.QueryRow(ctx,
		`UPDATE employee_installed_packages
		   SET is_active = false
		 WHERE is_active AND last_seen_at < $1`,
		cutoff,
	).Scan(&pkgs); err != nil {
		return err
	}

	if apps > 0 || pkgs > 0 {
		log.Printf("[staleness-sweep] deactivated %d app links, %d package links (cutoff %s)", apps, pkgs, cutoff.Format(time.RFC3339))
	}
	return nil
}

// Start launches the hourly background loop until ctx is cancelled.
func (s *StalenessSweep) Start(ctx context.Context) {
	go func() {
		ticker := time.NewTicker(time.Hour)
		defer ticker.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-ticker.C:
				if err := s.Run(ctx); err != nil {
					log.Printf("[staleness-sweep] error: %v", err)
				}
			}
		}
	}()
}
