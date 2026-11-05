package jobs

import (
	"context"
	"log"
	"os"
	"strconv"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

type RetentionWorker struct {
	pool          *pgxpool.Pool
	retentionDays int
	interval      time.Duration
}

func NewRetentionWorker(pool *pgxpool.Pool) *RetentionWorker {
	retentionDays := 30
	if envVal := os.Getenv("RETENTION_DAYS"); envVal != "" {
		if parsed, err := strconv.Atoi(envVal); err == nil && parsed > 0 {
			retentionDays = parsed
		}
	}

	return &RetentionWorker{
		pool:          pool,
		retentionDays: retentionDays,
		interval:      1 * time.Hour,
	}
}

func (w *RetentionWorker) Start(ctx context.Context) {
	log.Printf("[retention] Starting activity retention sweep worker (retention_days=%d, interval=%v)", w.retentionDays, w.interval)

	// Run initial sweep on boot after 30 seconds
	select {
	case <-time.After(30 * time.Second):
		w.runSweep(ctx)
	case <-ctx.Done():
		return
	}

	ticker := time.NewTicker(w.interval)
	defer ticker.Stop()

	for {
		select {
		case <-ticker.C:
			w.runSweep(ctx)
		case <-ctx.Done():
			log.Printf("[retention] Stopping retention worker")
			return
		}
	}
}

func (w *RetentionWorker) runSweep(ctx context.Context) {
	startTime := time.Now()
	cutoff := time.Now().AddDate(0, 0, -w.retentionDays)

	// 1. Purge old app items linked to expired sessions
	itemsQuery := `
		DELETE FROM app_items
		WHERE opened_at < $1
		  AND id IN (
			  SELECT id FROM app_items WHERE opened_at < $1 LIMIT 5000
		  )
	`
	itemsTag, err := w.pool.Exec(ctx, itemsQuery, cutoff)
	var deletedItems int64
	if err == nil {
		deletedItems = itemsTag.RowsAffected()
	} else {
		log.Printf("[retention] Error purging expired app_items: %v", err)
	}

	// 2. Purge old closed app sessions
	sessionsQuery := `
		DELETE FROM app_sessions
		WHERE ended_at IS NOT NULL
		  AND ended_at < $1
		  AND id IN (
			  SELECT id FROM app_sessions WHERE ended_at IS NOT NULL AND ended_at < $1 LIMIT 1000
		  )
	`
	sessionsTag, err := w.pool.Exec(ctx, sessionsQuery, cutoff)
	var deletedSessions int64
	if err == nil {
		deletedSessions = sessionsTag.RowsAffected()
	} else {
		log.Printf("[retention] Error purging expired app_sessions: %v", err)
	}

	if deletedItems > 0 || deletedSessions > 0 {
		log.Printf("[retention] Retention sweep completed in %v: purged %d app_items, %d app_sessions (cutoff: %s)",
			time.Since(startTime), deletedItems, deletedSessions, cutoff.Format(time.RFC3339))
	}
}
