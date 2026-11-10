package queue

import (
	"context"
	"fmt"
	"log"
	"time"

	"github.com/alpha-ai-tracker/server/internal/jobs"
	"github.com/hibiken/asynq"
	"github.com/jackc/pgx/v5/pgxpool"
)

// WorkerDeps holds job runners shared with the Asynq mux.
type WorkerDeps struct {
	Session  *jobs.SessionLifecycleSweep
	Retention *jobs.RetentionWorker
	Stale    *jobs.StalenessSweep
}

// NewMux registers bounded job handlers.
func NewMux(deps WorkerDeps) *asynq.ServeMux {
	mux := asynq.NewServeMux()
	mux.HandleFunc(TaskSessionLifecycle, func(ctx context.Context, _ *asynq.Task) error {
		if deps.Session == nil {
			return nil
		}
		deps.Session.RunOnce(ctx)
		return nil
	})
	mux.HandleFunc(TaskRetention, func(ctx context.Context, _ *asynq.Task) error {
		if deps.Retention == nil {
			return nil
		}
		deps.Retention.RunOnce(ctx)
		return nil
	})
	mux.HandleFunc(TaskStaleness, func(ctx context.Context, _ *asynq.Task) error {
		if deps.Stale == nil {
			return nil
		}
		return deps.Stale.Run(ctx)
	})
	return mux
}

// RedisOpt builds asynq Redis client opt from host/port/password/db.
func RedisOpt(addr, password string, db int) asynq.RedisClientOpt {
	return asynq.RedisClientOpt{
		Addr:     addr,
		Password: password,
		DB:       db,
	}
}

// RunWorker starts an Asynq server with a hard concurrency cap until ctx cancels.
func RunWorker(ctx context.Context, redisOpt asynq.RedisClientOpt, concurrency int, deps WorkerDeps) error {
	if concurrency < 1 {
		concurrency = 4
	}
	srv := asynq.NewServer(redisOpt, asynq.Config{
		Concurrency: concurrency,
		Queues: map[string]int{
			QueueLow: 1,
		},
		// Never spawn unbounded handlers — Concurrency is the hard cap.
	})
	mux := NewMux(deps)
	errCh := make(chan error, 1)
	go func() {
		log.Printf("[asynq] worker starting (concurrency=%d)", concurrency)
		errCh <- srv.Run(mux)
	}()
	select {
	case <-ctx.Done():
		srv.Shutdown()
		return nil
	case err := <-errCh:
		return err
	}
}

// SchedulePeriodic enqueues cron-like periodic tasks via asynq Scheduler.
func SchedulePeriodic(ctx context.Context, redisOpt asynq.RedisClientOpt, sessionEvery, retentionEvery, staleEvery time.Duration) (*asynq.Scheduler, error) {
	scheduler := asynq.NewScheduler(redisOpt, nil)

	if sessionEvery < time.Second {
		sessionEvery = time.Minute
	}
	if retentionEvery < time.Minute {
		retentionEvery = time.Hour
	}
	if staleEvery < time.Minute {
		staleEvery = time.Hour
	}

	// asynq periodic uses cron; convert durations to @every.
	if _, err := scheduler.Register(
		"@every "+durCron(sessionEvery),
		asynq.NewTask(TaskSessionLifecycle, nil),
		asynq.Queue(QueueLow),
	); err != nil {
		return nil, fmt.Errorf("register session lifecycle: %w", err)
	}
	if _, err := scheduler.Register(
		"@every "+durCron(retentionEvery),
		asynq.NewTask(TaskRetention, nil),
		asynq.Queue(QueueLow),
	); err != nil {
		return nil, fmt.Errorf("register retention: %w", err)
	}
	if _, err := scheduler.Register(
		"@every "+durCron(staleEvery),
		asynq.NewTask(TaskStaleness, nil),
		asynq.Queue(QueueLow),
	); err != nil {
		return nil, fmt.Errorf("register staleness: %w", err)
	}

	go func() {
		<-ctx.Done()
		scheduler.Shutdown()
	}()
	if err := scheduler.Start(); err != nil {
		return nil, err
	}
	log.Printf("[asynq] scheduler started (session=%s retention=%s stale=%s)",
		sessionEvery, retentionEvery, staleEvery)
	return scheduler, nil
}

func durCron(d time.Duration) string {
	// asynq @every accepts Go duration strings like 60s, 1h.
	return d.String()
}

// NewJobDeps builds job runners from a pool (same constructors as legacy tickers).
func NewJobDeps(pool *pgxpool.Pool, linkStaleDays int) WorkerDeps {
	return WorkerDeps{
		Session:   jobs.NewSessionLifecycleSweep(pool),
		Retention: jobs.NewRetentionWorker(pool),
		Stale:     jobs.NewStalenessSweep(pool, linkStaleDays),
	}
}
