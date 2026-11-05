package services

import (
	"context"
	"os"
	"testing"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/jackc/pgx/v5/pgxpool"
)

// Smoke test against the live lab DB when DLP_SMOKE=1 (not run in CI by default).
func TestDlpPublishAndPullSmoke(t *testing.T) {
	if os.Getenv("DLP_SMOKE") != "1" {
		t.Skip("set DLP_SMOKE=1 to run against lab Postgres")
	}
	dsn := os.Getenv("DATABASE_URL")
	if dsn == "" {
		dsn = "postgres://alpha_monitoring:123456@localhost:5432/alpha_ai_tracker?sslmode=disable"
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	pool, err := pgxpool.New(ctx, dsn)
	if err != nil {
		t.Fatalf("pool: %v", err)
	}
	defer pool.Close()

	repo := repository.NewDlpRepo(pool)
	svc := NewDlpService(repo, 60)

	rule, err := svc.CreateRule(ctx, dto.CreateDlpRuleRequest{
		Name:       "Smoke USB Block",
		Trigger:    "usb_storage",
		Action:     "alert_block",
		Severity:   "critical",
		Extensions: []string{".xlsx"},
	})
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	t.Cleanup(func() {
		_ = repo.SoftDeleteRule(context.Background(), rule.ID)
	})

	pub, err := svc.Publish(ctx, nil)
	if err != nil {
		t.Fatalf("publish: %v", err)
	}
	if pub.PolicyVersion < 1 {
		t.Fatalf("expected version >= 1, got %d", pub.PolicyVersion)
	}

	pull, err := svc.PullPolicy(ctx, 0)
	if err != nil {
		t.Fatalf("pull: %v", err)
	}
	if pull.PolicyVersion != pub.PolicyVersion {
		t.Fatalf("pull version %d != published %d", pull.PolicyVersion, pub.PolicyVersion)
	}
	if pull.Unchanged {
		t.Fatal("expected changed bundle on sinceVersion=0")
	}
	t.Logf("published v%d hash=%s rules=%d", pub.PolicyVersion, pub.Hash, pub.RuleCount)
}
