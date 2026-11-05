package main

import (
	"context"
	"log"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"github.com/alpha-ai-tracker/server/internal/config"
	"github.com/alpha-ai-tracker/server/internal/database"
	"github.com/alpha-ai-tracker/server/internal/handlers"
	"github.com/alpha-ai-tracker/server/internal/jobs"
	goredis "github.com/alpha-ai-tracker/server/internal/redis"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/router"
	"github.com/alpha-ai-tracker/server/internal/scale"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/alpha-ai-tracker/server/internal/stream"
	"github.com/alpha-ai-tracker/server/internal/ws"
	"github.com/labstack/echo/v4"
)

func main() {
	log.SetFlags(log.LstdFlags | log.Lshortfile)
	log.Println("[server] Alpha AI Tracker — Starting...")

	// ────────────────
	// Load Config
	// ────────────────
	cfg, err := config.Load()
	if err != nil {
		log.Fatalf("[server] failed to load config: %v", err)
	}
	log.Printf("[server] configured for %s", cfg.ServerAddr())

	// ────────────────
	// Database
	// ────────────────
	pool, err := database.NewPool(cfg.Database)
	if err != nil {
		log.Fatalf("[server] database connection failed: %v", err)
	}
	defer pool.Close()

	// Run migrations
	migrationsDir := findMigrationsDir()
	if err := database.RunMigrations(pool, migrationsDir); err != nil {
		log.Fatalf("[server] migration failed: %v", err)
	}

	// ────────────────
	// Redis
	// ────────────────
	redisAddr := cfg.Redis.Host + ":" + itoa(cfg.Redis.Port)
	redisClient, err := goredis.NewClient(redisAddr, cfg.Redis.Password, cfg.Redis.DB)
	if err != nil {
		log.Printf("[server] WARNING: Redis connection failed: %v — employee secrets will not work", err)
		redisClient = nil
	}
	if redisClient != nil {
		defer redisClient.Close()
		log.Printf("[server] connected to Redis at %s", redisAddr)
	}

	// ────────────────
	// Dependencies (DI)
	// ────────────────
	userRepo := repository.NewUserRepo(pool)
	employeeRepo := repository.NewEmployeeRepo(pool)
	deviceRepo := repository.NewDeviceRepo(pool)
	departmentRepo := repository.NewDepartmentRepo(pool)
	newSchemaRepo := repository.NewNewSchemaRepo(pool)
	monitoringRepo := repository.NewMonitoringRepo(pool)
	dlpRepo := repository.NewDlpRepo(pool)
	rbacRepo := repository.NewRBACRepo(pool)
	refreshTokenRepo := repository.NewRefreshTokenRepo(pool)
	shiftRepo := repository.NewShiftRepo(pool)
	timeAttendanceRepo := repository.NewTimeAttendanceRepo(pool)
	termsConsentRepo := repository.NewTermsConsentRepo(pool)
	termsContentRepo := repository.NewTermsContentRepo(pool)

	// Seed featured Terms & Conditions (idempotent — safe on every startup)
	termsSeeder := services.NewTermsContentSeeder(termsContentRepo)
	if err := termsSeeder.SeedFeaturedTerms(context.Background()); err != nil {
		log.Printf("[server] WARNING: terms seeder error: %v", err)
	}

	authService := services.NewAuthService(userRepo, rbacRepo, refreshTokenRepo, cfg.JWT, cfg.Admin)
	userService := services.NewUserService(userRepo, rbacRepo, employeeRepo)
	employeeService := services.NewEmployeeService(employeeRepo, shiftRepo, redisClient)
	departmentService := services.NewDepartmentService(departmentRepo, employeeRepo)
	geofenceRepo := repository.NewGeofenceRepo(pool)
	geofenceService := services.NewGeofenceService(geofenceRepo)
	trackingControlRepo := repository.NewTrackingControlRepo(pool)
	trackingControlService := services.NewTrackingControlService(trackingControlRepo)
	newSchemaService := services.NewNewSchemaService(newSchemaRepo, employeeRepo, geofenceService).WithTrackingControl(trackingControlService)
	monitoringService := services.NewMonitoringService(monitoringRepo)
	dlpService := services.NewDlpService(dlpRepo, cfg.DlpRemovalCodeTTLMin)
	rbacService := services.NewRBACService(rbacRepo)
	shiftService := services.NewShiftService(shiftRepo, cfg.DefaultShiftTimezone)
	timeAttendanceService := services.NewTimeAttendanceService(timeAttendanceRepo)

	// Cast Redis client to interface
	var redisInterface services.RedisClientInterface
	if redisClient != nil {
		redisInterface = redisClient
	}

	authHandler := handlers.NewAuthHandler(authService, userService, employeeRepo, deviceRepo, redisInterface, cfg.JWT)
	userHandler := handlers.NewUserHandler(userService)
	employeeHandler := handlers.NewEmployeeHandler(employeeService)
	departmentHandler := handlers.NewDepartmentHandler(departmentService)
	newSchemaHandler := handlers.NewNewSchemaHandler(newSchemaService, authService).WithTrackingControl(trackingControlService)
	trackingControlHandler := handlers.NewTrackingControlHandler(trackingControlService, employeeRepo)
	monitoringHandler := handlers.NewMonitoringHandler(monitoringService)
	dlpHandler := handlers.NewDlpHandler(dlpService)
	rbacHandler := handlers.NewRBACHandler(rbacService)
	shiftHandler := handlers.NewShiftHandler(shiftService)
	timeAttendanceHandler := handlers.NewTimeAttendanceHandler(timeAttendanceService)
	geofenceHandler := handlers.NewGeofenceHandler(geofenceService)
	termsConsentHandler := handlers.NewTermsConsentHandler(termsConsentRepo)
	termsContentHandler := handlers.NewTermsContentHandler(termsContentRepo)

	iceServers := make([]stream.ICEServerConfig, 0, 2)
	if len(cfg.LiveStream.STUNURLs) > 0 {
		iceServers = append(iceServers, stream.ICEServerConfig{URLs: cfg.LiveStream.STUNURLs})
	}
	if len(cfg.LiveStream.TURNURLs) > 0 {
		iceServers = append(iceServers, stream.ICEServerConfig{
			URLs:       cfg.LiveStream.TURNURLs,
			Username:   cfg.LiveStream.TURNUser,
			Credential: cfg.LiveStream.TURNPass,
		})
	}
	instanceID := scale.ResolveInstanceID(cfg.Server.InstanceID)
	log.Printf("[server] instance id=%s publicURL=%q", instanceID, cfg.Server.InstancePublicURL)

	streamHub := stream.NewHub(stream.Config{
		Enabled:                cfg.LiveStream.Enabled,
		MaxStreams:             cfg.LiveStream.MaxStreams,
		MaxWatchersPerEmployee: cfg.LiveStream.MaxWatchersPerEmployee,
		IdleSec:                cfg.LiveStream.IdleSec,
		MaxBitrateKbps:         cfg.LiveStream.MaxBitrateKbps,
		ICEServers:             iceServers,
		InstanceID:             instanceID,
		InstancePublicURL:      cfg.Server.InstancePublicURL,
	})
	defer streamHub.Close()

	presenceHub := ws.NewHub(ws.Config{
		Enabled:        cfg.PresenceWS.Enabled,
		MaxConnections: cfg.PresenceWS.MaxConnections,
		InstanceID:     instanceID,
	})
	defer presenceHub.Close()

	// Phase 2: when Redis is up, share presence + watch tickets + publisher routing.
	if redisClient != nil {
		presenceHub.SetBackend(redisClient)
		streamHub.SetCluster(redisClient)
		log.Println("[server] live-stream cluster: Redis presence + tickets + publisher registry enabled")
	} else {
		log.Println("[server] live-stream cluster: Redis unavailable — single-instance local hubs only")
	}

	wsHandler := handlers.NewWsHandler(presenceHub, deviceRepo, cfg.CORS.AllowedOrigins)
	if cfg.PresenceWS.Enabled {
		log.Printf("[server] presence ws enabled (maxConnections=%d)", cfg.PresenceWS.MaxConnections)
	} else {
		log.Println("[server] presence ws disabled")
	}

	if len(cfg.LiveStream.TURNURLs) == 0 {
		log.Println("[server] WARNING: WEBRTC_TURN_URLS is empty — remote clients behind CGNAT/firewall will fail ICE. Provision coturn (or equivalent) on/near this VPS domain before calling live-stream production. TURN doubles media bandwidth on the relayed path.")
	} else {
		log.Printf("[server] webrtc TURN configured (%d url(s))", len(cfg.LiveStream.TURNURLs))
	}

	streamHandler := handlers.NewStreamHandler(
		streamHub, presenceHub, employeeRepo, termsConsentRepo, timeAttendanceRepo, deviceRepo, cfg.CORS.AllowedOrigins,
	)
	if cfg.LiveStream.Enabled {
		log.Printf("[server] live-stream webrtc sfu enabled (maxStreams=%d bitrate=%dkbps)", cfg.LiveStream.MaxStreams, cfg.LiveStream.MaxBitrateKbps)
	} else {
		log.Println("[server] live-stream hub disabled")
	}

	dashboardRepo := repository.NewDashboardRepo(pool)
	dashboardService := services.NewDashboardService(
		dashboardRepo, employeeRepo, termsConsentRepo, timeAttendanceRepo, timeAttendanceService, streamHub, presenceHub,
	)
	dashboardHandler := handlers.NewDashboardHandler(dashboardService)

	logInsightsRepo := repository.NewLogInsightsRepo(pool)
	logInsightsService := services.NewLogInsightsService(logInsightsRepo)
	logInsightsHandler := handlers.NewLogInsightsHandler(logInsightsService)

	logGraphicalRepo := repository.NewLogGraphicalRepo(pool)
	logGraphicalService := services.NewLogGraphicalService(logGraphicalRepo)
	logGraphicalHandler := handlers.NewLogGraphicalHandler(logGraphicalService)

	githubReleaseService := services.NewGitHubReleaseService(cfg.GitHub)
	releaseHandler := handlers.NewReleaseHandler(githubReleaseService)
	if githubReleaseService.Configured() {
		log.Printf("[server] github releases bridge enabled (owner=%s repo=%s cache=%s)",
			cfg.GitHub.Owner, cfg.GitHub.Repo, cfg.GitHub.CacheTTL)
	} else {
		log.Println("[server] WARNING: GITHUB_TOKEN unset — public /api/v1/releases and /api/v1/downloads will return 503")
	}

	// ────────────────
	// Seed RBAC catalog (modules, submodules, system role) — idempotent
	// ────────────────
	ctx := context.Background()
	if err := rbacService.SeedCatalog(ctx); err != nil {
		log.Fatalf("[server] failed to seed RBAC catalog: %v", err)
	}

	// ────────────────
	// Auto-initialize Company Admin
	// ────────────────
	if err := authService.EnsureCompanyAdmin(ctx); err != nil {
		log.Fatalf("[server] failed to ensure company admin: %v", err)
	}

	shiftTzUpdated, err := shiftService.ApplyDefaultTimezone(ctx)
	if err != nil {
		log.Fatalf("[server] failed to apply default shift timezone: %v", err)
	}
	if shiftTzUpdated > 0 {
		log.Printf("[server] applied DEFAULT_SHIFT_TIMEZONE to %d shift(s) still on UTC", shiftTzUpdated)
	}

	// ────────────────
	// Background jobs
	// ────────────────
	sweepCtx, sweepCancel := context.WithCancel(context.Background())
	defer sweepCancel()
	stalenessSweep := jobs.NewStalenessSweep(pool, cfg.LinkStaleDays)
	stalenessSweep.Start(sweepCtx)
	log.Printf("[server] staleness sweep started (stale window: %d days)", cfg.LinkStaleDays)

	retentionWorker := jobs.NewRetentionWorker(pool)
	go retentionWorker.Start(sweepCtx)

	sessionLifecycleSweep := jobs.NewSessionLifecycleSweep(pool)
	go sessionLifecycleSweep.Start(sweepCtx)

	// ────────────────
	// Setup Echo
	// ────────────────
	e := echo.New()
	e.HideBanner = true
	e.HidePort = true

	router.Setup(e, cfg, authService, deviceRepo, userRepo, authHandler, userHandler, employeeHandler, departmentHandler, newSchemaHandler, monitoringHandler, rbacHandler, shiftHandler, timeAttendanceHandler, geofenceHandler, termsConsentHandler, termsContentHandler, streamHandler, wsHandler, dashboardHandler, logInsightsHandler, logGraphicalHandler, dlpHandler, releaseHandler, trackingControlHandler)

	// ────────────────
	// Graceful Shutdown
	// ────────────────
	go func() {
		log.Printf("[server] listening on %s", cfg.ServerAddr())
		if err := e.Start(cfg.ServerAddr()); err != nil {
			log.Printf("[server] server stopped: %v", err)
		}
	}()

	quit := make(chan os.Signal, 1)
	signal.Notify(quit, syscall.SIGINT, syscall.SIGTERM)
	<-quit

	log.Println("[server] shutting down gracefully...")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	if err := e.Shutdown(shutdownCtx); err != nil {
		log.Fatalf("[server] forced shutdown: %v", err)
	}
	log.Println("[server] stopped")
}

// findMigrationsDir locates the migrations directory relative to the binary.
func findMigrationsDir() string {
	candidates := []string{
		"migrations",
		"../../migrations",
		"../migrations",
		filepath.Join("server", "migrations"),
	}

	if exe, err := os.Executable(); err == nil {
		candidates = append(candidates, filepath.Join(filepath.Dir(exe), "migrations"))
		candidates = append(candidates, filepath.Join(filepath.Dir(exe), "..", "..", "migrations"))
	}

	for _, dir := range candidates {
		absDir, err := filepath.Abs(dir)
		if err != nil {
			continue
		}
		if info, err := os.Stat(absDir); err == nil && info.IsDir() {
			return absDir
		}
	}

	log.Println("[server] WARNING: migrations directory not found, using 'migrations'")
	return "migrations"
}

func itoa(i int) string {
	if i == 0 {
		return "0"
	}
	var buf [20]byte
	pos := len(buf)
	neg := i < 0
	if neg {
		i = -i
	}
	for i > 0 {
		pos--
		buf[pos] = byte('0' + i%10)
		i /= 10
	}
	if neg {
		pos--
		buf[pos] = '-'
	}
	return string(buf[pos:])
}
