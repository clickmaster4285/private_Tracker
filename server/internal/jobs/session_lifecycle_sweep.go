package jobs

import (
	"context"
	"log"
	"os"
	"strconv"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// SessionLifecycleSweep flips app_sessions through the 4-state lifecycle
// in PER-MACHINE windows, not per-session. The distinction matters:
//
//   - Closing a single app (e.g. the user quits Chrome at 10:00 but the
//     tracker keeps running) must NOT mark the whole machine offline — the
//     tracker is still alive and reporting other apps. That is handled by
//     the client setting `ended_at` on the Chrome row; the upsert flips
//     that single session to CLOSED immediately.
//   - Killing the client process (network drop, PC sleep, uninstall) takes
//     the WHOLE machine offline. No sync arrives for any session, so the
//     machine's last_sync_at goes quiet. The sweep detects "no row for
//     this machine in the last N minutes" and flips ALL of that machine's
//     ACTIVE sessions through OFFLINE → STALE → CLOSED together.
//
// States:
//
//	ACTIVE  = some row for this machine has synced within OFFLINE_AFTER (default 10m)
//	OFFLINE = no row for this machine for OFFLINE_AFTER..STALE_AFTER (default 10m..1h)
//	STALE   = no row for this machine for STALE_AFTER..CLOSE_AFTER (default 1h..24h)
//	CLOSED  = terminal; no row for this machine for CLOSE_AFTER+ (default 24h+),
//	          OR the client sent a non-NULL ended_at (handled in the upsert)
//
// A live client re-uploading any row with ended_at=NULL for a machine
// that has been quiet promotes OFFLINE/STALE/CLOSED back to ACTIVE
// (in BulkInsertAppSessions).
//
// Env knobs:
//
//	SESSION_OFFLINE_AFTER_MINUTES   default 10
//	SESSION_STALE_AFTER_MINUTES     default 60
//	SESSION_CLOSE_AFTER_HOURS       default 24
//	SESSION_SWEEP_INTERVAL_SECONDS   default 60
type SessionLifecycleSweep struct {
	pool         *pgxpool.Pool
	offlineAfter time.Duration
	staleAfter   time.Duration
	closeAfter   time.Duration
	interval     time.Duration
}

func NewSessionLifecycleSweep(pool *pgxpool.Pool) *SessionLifecycleSweep {
	offlineMin := 10
	if v := os.Getenv("SESSION_OFFLINE_AFTER_MINUTES"); v != "" {
		if parsed, err := strconv.Atoi(v); err == nil && parsed > 0 {
			offlineMin = parsed
		}
	}
	staleMin := 60
	if v := os.Getenv("SESSION_STALE_AFTER_MINUTES"); v != "" {
		if parsed, err := strconv.Atoi(v); err == nil && parsed > 0 {
			staleMin = parsed
		}
	}
	// Stale window must be strictly greater than the offline window.
	if staleMin <= offlineMin {
		staleMin = offlineMin * 6
	}
	closeHr := 24
	if v := os.Getenv("SESSION_CLOSE_AFTER_HOURS"); v != "" {
		if parsed, err := strconv.Atoi(v); err == nil && parsed > 0 {
			closeHr = parsed
		}
	}
	intervalSec := 60
	if v := os.Getenv("SESSION_SWEEP_INTERVAL_SECONDS"); v != "" {
		if parsed, err := strconv.Atoi(v); err == nil && parsed > 0 {
			intervalSec = parsed
		}
	}
	return &SessionLifecycleSweep{
		pool:         pool,
		offlineAfter: time.Duration(offlineMin) * time.Minute,
		staleAfter:   time.Duration(staleMin) * time.Minute,
		closeAfter:   time.Duration(closeHr) * time.Hour,
		interval:     time.Duration(intervalSec) * time.Second,
	}
}

// Start launches the background loop until ctx is cancelled.
func (s *SessionLifecycleSweep) Start(ctx context.Context) {
	log.Printf("[session-lifecycle] starting (offline_after=%v, stale_after=%v, close_after=%v, interval=%v)",
		s.offlineAfter, s.staleAfter, s.closeAfter, s.interval)

	// Run once shortly after start so the dashboard reflects the new
	// status column within ~30s of boot, not after the first interval.
	select {
	case <-time.After(30 * time.Second):
		s.RunOnce(ctx)
	case <-ctx.Done():
		return
	}

	ticker := time.NewTicker(s.interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			log.Printf("[session-lifecycle] stopping")
			return
		case <-ticker.C:
			s.RunOnce(ctx)
		}
	}
}

// RunOnce performs one transition pass. Three per-machine UPDATEs:
//
//  1. ACTIVE → OFFLINE: this machine has not synced anything for OFFLINE_AFTER.
//  2. OFFLINE → STALE:  this machine has not synced anything for STALE_AFTER.
//  3. STALE → CLOSED:   this machine has not synced anything for CLOSE_AFTER;
//     freeze ended_at at last_activity_at so the duration reflects real usage.
//
// Each subquery in the WHERE clause hits the (employee_id, started_at DESC)
// index on app_sessions; the body of the UPDATE is a sequential scan over
// only the affected rows. Total work per pass is small (milliseconds on
// the dev DB).
func (s *SessionLifecycleSweep) RunOnce(ctx context.Context) {
	if s.pool == nil {
		return
	}
	start := time.Now()

	// 1. ACTIVE → OFFLINE. A machine is "alive" if any of its rows have
	// synced within OFFLINE_AFTER. Machines with no recent row are silent,
	// and all of their ACTIVE sessions flip to OFFLINE together.
	offlineTag, err := s.pool.Exec(ctx, `
		UPDATE app_sessions
		   SET status = 'OFFLINE'
		 WHERE deleted_at IS NULL
		   AND status = 'ACTIVE'
		   AND machine_id <> ''
		   AND machine_id NOT IN (
			   SELECT DISTINCT machine_id
			     FROM app_sessions
			    WHERE deleted_at IS NULL
			      AND last_sync_at IS NOT NULL
			      AND last_sync_at > NOW() - make_interval(secs => $1)
		   )
	`, int64(s.offlineAfter.Seconds()))
	if err != nil {
		log.Printf("[session-lifecycle] ACTIVE→OFFLINE error: %v", err)
		return
	}

	// 2. OFFLINE → STALE. Same per-machine check, longer window.
	staleTag, err := s.pool.Exec(ctx, `
		UPDATE app_sessions
		   SET status = 'STALE'
		 WHERE deleted_at IS NULL
		   AND status = 'OFFLINE'
		   AND machine_id <> ''
		   AND machine_id NOT IN (
			   SELECT DISTINCT machine_id
			     FROM app_sessions
			    WHERE deleted_at IS NULL
			      AND last_sync_at IS NOT NULL
			      AND last_sync_at > NOW() - make_interval(secs => $1)
		   )
	`, int64(s.staleAfter.Seconds()))
	if err != nil {
		log.Printf("[session-lifecycle] OFFLINE→STALE error: %v", err)
		return
	}

	// 3. STALE → CLOSED. Terminal. Freeze ended_at at last_activity_at
	// so the duration reflects real usage, not the sweep moment.
	closeTag, err := s.pool.Exec(ctx, `
		UPDATE app_sessions
		   SET status   = 'CLOSED',
		       ended_at = COALESCE(last_activity_at, last_sync_at, started_at)
		 WHERE deleted_at IS NULL
		   AND status = 'STALE'
		   AND machine_id <> ''
		   AND machine_id NOT IN (
			   SELECT DISTINCT machine_id
			     FROM app_sessions
			    WHERE deleted_at IS NULL
			      AND last_sync_at IS NOT NULL
			      AND last_sync_at > NOW() - make_interval(secs => $1)
		   )
	`, int64(s.closeAfter.Seconds()))
	if err != nil {
		log.Printf("[session-lifecycle] STALE→CLOSED error: %v", err)
		return
	}

	// 4. Per-row Stagnation Sweep. A row whose own last_sync is >= CLOSE_AFTER old
	// is never coming back, even if another row on the same machine is still active.
	// This prevents rows from being stranded in OFFLINE/STALE indefinitely.
	stagnationTag, err := s.pool.Exec(ctx, `
		UPDATE app_sessions
		   SET status   = 'CLOSED',
		       ended_at = COALESCE(last_activity_at, last_sync_at, started_at)
		 WHERE deleted_at IS NULL
		   AND ended_at IS NULL
		   AND status IN ('OFFLINE','STALE')
		   AND last_sync_at < NOW() - make_interval(secs => $1)
	`, int64(s.closeAfter.Seconds()))
	if err != nil {
		log.Printf("[session-lifecycle] Stagnation Sweep error: %v", err)
		return
	}

	if offlineTag.RowsAffected() > 0 || staleTag.RowsAffected() > 0 || closeTag.RowsAffected() > 0 || stagnationTag.RowsAffected() > 0 {
		log.Printf("[session-lifecycle] sweep in %v: %d ACTIVE→OFFLINE, %d OFFLINE→STALE, %d STALE→CLOSED, %d STAGNANT→CLOSED",
			time.Since(start), offlineTag.RowsAffected(), staleTag.RowsAffected(), closeTag.RowsAffected(), stagnationTag.RowsAffected())
	}
}
