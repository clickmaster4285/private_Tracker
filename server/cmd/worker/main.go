package main

import (
	"context"
	"log"
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"time"

	"github.com/alpha-ai-tracker/server/internal/config"
	"github.com/alpha-ai-tracker/server/internal/database"
	"github.com/alpha-ai-tracker/server/internal/queue"
)

func main() {
	log.SetFlags(log.LstdFlags | log.Lshortfile)
	log.Println("[worker] Alpha AI Tracker — Asynq worker starting...")

	cfg, err := config.Load()
	if err != nil {
		log.Fatalf("[worker] config: %v", err)
	}

	pool, err := database.NewPool(cfg.Database)
	if err != nil {
		log.Fatalf("[worker] database: %v", err)
	}
	defer pool.Close()

	redisAddr := cfg.Redis.Host + ":" + strconv.Itoa(cfg.Redis.Port)
	opt := queue.RedisOpt(redisAddr, cfg.Redis.Password, cfg.Redis.DB)
	deps := queue.NewJobDeps(pool, cfg.LinkStaleDays)

	concurrency := cfg.Scale.AsynqConcurrency
	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer cancel()

	sessionEvery := time.Duration(cfg.Scale.SessionSweepSec) * time.Second
	if sessionEvery <= 0 {
		sessionEvery = time.Minute
	}
	if _, err := queue.SchedulePeriodic(ctx, opt, sessionEvery, time.Hour, time.Hour); err != nil {
		log.Fatalf("[worker] scheduler: %v", err)
	}

	if err := queue.RunWorker(ctx, opt, concurrency, deps); err != nil {
		log.Printf("[worker] stopped: %v", err)
		os.Exit(1)
	}
	log.Println("[worker] stopped")
}
