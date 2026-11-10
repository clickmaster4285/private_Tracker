package router

import (
	"strings"

	"github.com/alpha-ai-tracker/server/internal/admit"
	"github.com/alpha-ai-tracker/server/internal/config"
	"github.com/alpha-ai-tracker/server/internal/handlers"
	appMiddleware "github.com/alpha-ai-tracker/server/internal/middleware"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
	"github.com/labstack/echo/v4/middleware"
)

// Setup configures all routes and middleware on the Echo instance.
func Setup(
	e *echo.Echo,
	cfg *config.Config,
	authService *services.AuthService,
	deviceRepo *repository.DeviceRepo,
	userRepo *repository.UserRepo,
	authHandler *handlers.AuthHandler,
	userHandler *handlers.UserHandler,
	employeeHandler *handlers.EmployeeHandler,
	departmentHandler *handlers.DepartmentHandler,
	newSchemaHandler *handlers.NewSchemaHandler,
	monitoringHandler *handlers.MonitoringHandler,
	rbacHandler *handlers.RBACHandler,
	shiftHandler *handlers.ShiftHandler,
	timeAttendanceHandler *handlers.TimeAttendanceHandler,
	geofenceHandler *handlers.GeofenceHandler,
	streamHandler *handlers.StreamHandler,
	wsHandler *handlers.WsHandler,
	dashboardHandler *handlers.DashboardHandler,
	logInsightsHandler *handlers.LogInsightsHandler,
	logGraphicalHandler *handlers.LogGraphicalHandler,
	dlpHandler *handlers.DlpHandler,
	releaseHandler *handlers.ReleaseHandler,
) {
	// ─────────────────────────────
	// Global Middleware
	// ─────────────────────────────
	e.Use(middleware.LoggerWithConfig(middleware.LoggerConfig{
		Format: `{"time":"${time_rfc3339}","method":"${method}","uri":"${uri}","status":${status},"latency":"${latency_human}"}` + "\n",
	}))
	e.Use(middleware.Recover())

	// Sync ingestion: body cap 20MB & transparent gzip decompression
	e.Use(middleware.BodyLimit("20M"))
	e.Use(middleware.Decompress())

	// CORS — allow frontend origin with credentials
	e.Use(middleware.CORSWithConfig(middleware.CORSConfig{
		AllowOrigins:     cfg.CORS.AllowedOrigins,
		AllowMethods:     []string{echo.GET, echo.HEAD, echo.POST, echo.PUT, echo.PATCH, echo.DELETE, echo.OPTIONS},
		AllowHeaders:     []string{"Content-Type", "Authorization", "X-Requested-With", "Range"},
		ExposeHeaders:    []string{"Content-Disposition", "Content-Length", "Content-Range", "Accept-Ranges"},
		AllowCredentials: true,
		MaxAge:           300,
	}))

	// ─────────────────────────────
	// Health Check
	// ─────────────────────────────
	e.GET("/api/v1/health", func(c echo.Context) error {
		return c.JSON(200, map[string]string{
			"status":    "ok",
			"timestamp": c.RealIP(),
		})
	})
	e.GET("/api/v1/server-time", timeAttendanceHandler.ServerTime)

	// ─────────────────────────────
	// Admission control (1000-agent scale)
	// ─────────────────────────────
	loginRL := admit.NewLimiter(cfg.Scale.LoginRPS, cfg.Scale.LoginBurst)
	syncRL := admit.NewLimiter(cfg.Scale.SyncRPS, cfg.Scale.SyncBurst)
	adminRL := admit.NewLimiter(cfg.Scale.AdminSearchRPS, cfg.Scale.AdminSearchBurst)
	syncGate := admit.NewSyncGate(int64(cfg.Scale.SyncMaxConcurrent))
	byIP := func(c echo.Context) string { return c.RealIP() }

	// ─────────────────────────────
	// Public Routes (no auth required)
	// ─────────────────────────────
	a := e.Group("/api/v1/auth")
	a.POST("/login", authHandler.Login, loginRL.Middleware(byIP))
	a.POST("/refresh", authHandler.Refresh)
	a.POST("/employee-login", authHandler.EmployeeLogin, loginRL.Middleware(byIP))

	// Public releases from the server's setup folder (RELEASES_DIR).
	// No JWT / DeviceAuth — /releases visitors and desktop updaters download without login.
	if releaseHandler != nil {
		e.GET("/api/v1/releases", releaseHandler.ListReleases)
		e.GET("/api/v1/downloads/:assetId", releaseHandler.DownloadAsset)
		e.HEAD("/api/v1/downloads/:assetId", releaseHandler.DownloadAsset)
	}

	// ─────────────────────────────
	// Sync Ingestion Routes (Device Authorization Middleware)
	// ─────────────────────────────
	syncGroup := e.Group("/api/v1")
	if deviceRepo != nil {
		syncGroup.Use(appMiddleware.DeviceAuth(deviceRepo, userRepo, authService))
	}
	// Rate-limit + gate only the *sync POST* surface (not presence WS / live push).
	syncAdmit := e.Group("/api/v1")
	if deviceRepo != nil {
		syncAdmit.Use(appMiddleware.DeviceAuth(deviceRepo, userRepo, authService))
	}
	syncAdmit.Use(syncRL.Middleware(byIP))
	syncAdmit.Use(syncGate.Middleware())

	// Phase 1–3 sync POSTs (admission-controlled)
	syncAdmit.POST("/device-hardware/sync", newSchemaHandler.SyncDeviceHardware)
	syncAdmit.POST("/installed-apps/sync", newSchemaHandler.SyncInstalledApps)
	syncAdmit.POST("/installed-packages/sync", newSchemaHandler.SyncInstalledPackages)
	syncAdmit.POST("/network-info/sync", newSchemaHandler.SyncNetworkInfo)
	syncAdmit.POST("/session-events/sync", newSchemaHandler.SyncSessionEvents)
	syncAdmit.POST("/app-sessions/sync", newSchemaHandler.SyncAppSessions)
	syncAdmit.POST("/app-items/sync", newSchemaHandler.SyncAppItems)
	syncAdmit.POST("/app-status/sync", newSchemaHandler.SyncAppStatus)
	syncAdmit.POST("/hardware-devices/sync", newSchemaHandler.SyncHardwareDevices)
	syncAdmit.POST("/permission-status/sync", newSchemaHandler.SyncPermissionStatus)
	syncAdmit.POST("/storage-devices/sync", newSchemaHandler.SyncStorageDevices)
	syncAdmit.POST("/location-samples/sync", newSchemaHandler.SyncLocationSamples)
	// "Remove the account" signal (R4/R5): called by the client AFTER it has drained its
	// unsynced rows. Marks the CALLING machine 'old' and the employee untracked; the client
	// then wipes its local DB + machine id. Lives in the DeviceAuth group because a device
	// token (not a web-admin session) identifies the machine.
	syncAdmit.POST("/devices/self-remove", newSchemaHandler.SelfRemoveAccount)

	syncGroup.GET("/schedules/me", timeAttendanceHandler.GetMySchedule)

	// Security & DLP — device surface (policy pull, alert sync, health, removal verify)
	if dlpHandler != nil {
		syncGroup.GET("/dlp/policy", dlpHandler.GetPolicy)
		syncAdmit.POST("/dlp/alerts/sync", dlpHandler.SyncAlerts)
		syncAdmit.POST("/dlp/health", dlpHandler.PostHealth)
		syncAdmit.POST("/devices/removal-verify", dlpHandler.VerifyRemovalCode)
	}

	// Live stream — client push socket (DeviceAuth). Web watch/employees live under JWTAuth.
	if streamHandler != nil {
		syncGroup.GET("/live-stream/push", streamHandler.Push)
		syncGroup.POST("/live-stream/uplink-probe", streamHandler.UplinkProbe)
	}

	// Control / presence WebSocket (DeviceAuth) — keep-alive channel, not frame streaming.
	if wsHandler != nil {
		syncGroup.GET("/ws", wsHandler.Connect)
	}

	// ─────────────────────────────
	// Semi-Protected Routes (optional auth)
	// ─────────────────────────────
	semiProtected := e.Group("/api/v1")
	semiProtected.Use(appMiddleware.OptionalAuth(authService))

	semiProtected.GET("/auth/check", authHandler.CheckAuth)

	// ─────────────────────────────
	// Protected Routes (auth required)
	// ─────────────────────────────
	protected := e.Group("/api/v1")
	protected.Use(appMiddleware.JWTAuth(authService))
	// Soft rate-limit admin list/search GETs (100 concurrent admins).
	adminMW := adminRL.Middleware(byIP)
	protected.Use(func(next echo.HandlerFunc) echo.HandlerFunc {
		return func(c echo.Context) error {
			if c.Request().Method != echo.GET {
				return next(c)
			}
			path := c.Path()
			if strings.Contains(path, "/employees") ||
				strings.Contains(path, "/app-sessions") ||
				strings.Contains(path, "/app-items") ||
				strings.Contains(path, "/attendance") ||
				strings.Contains(path, "/dashboard") ||
				strings.Contains(path, "/live-stream/employees") {
				return adminMW(next)(c)
			}
			return next(c)
		}
	})

	// Auth
	protected.GET("/auth/me", authHandler.Me)
	protected.GET("/auth/profile", authHandler.GetProfile)
	protected.POST("/auth/logout", authHandler.Logout)

	// Users
	users := protected.Group("/users")
	users.GET("", userHandler.ListUsers)
	users.GET("/:id", userHandler.GetUser)
	users.POST("", userHandler.CreateUser)
	users.PUT("/:id", userHandler.UpdateUser)
	users.DELETE("/:id", userHandler.DeleteUser)

	// Employees
	employees := protected.Group("/employees")
	employees.GET("", employeeHandler.ListEmployees)
	employees.GET("/:id/detail", newSchemaHandler.GetEmployeeDetail)
	employees.GET("/:id/devices", authHandler.ListEmployeeDevices)
	employees.POST("/:id/devices/remove", newSchemaHandler.RemoveEmployeeDevice)
	if dlpHandler != nil {
		employees.POST("/:id/machines/:machineId/lockdown-pause", dlpHandler.PauseMachineLockdown)
	}
	employees.POST("/import", employeeHandler.ImportEmployees)
	employees.GET("/export", employeeHandler.ExportEmployees)
	employees.GET("/:id", employeeHandler.GetEmployee)
	employees.POST("", employeeHandler.CreateEmployee)
	employees.PUT("/:id", employeeHandler.UpdateEmployee)
	employees.DELETE("/:id", employeeHandler.DeleteEmployee)
	employees.POST("/:id/generate-secret", employeeHandler.GenerateSecret)

	// Device Revocation
	protected.POST("/devices/:id/revoke", authHandler.RevokeDevice)

	// App Sessions listing (protected — web admin access)
	if dashboardHandler != nil {
		protected.GET("/dashboard/summary", dashboardHandler.GetSummary)
	}
	if logInsightsHandler != nil {
		protected.GET("/logs/insights", logInsightsHandler.GetInsights)
	}
	if logGraphicalHandler != nil {
		protected.GET("/logs/graphical", logGraphicalHandler.GetGraphical)
	}

	protected.GET("/app-sessions/usage/sessions", newSchemaHandler.ListAppSessionsForApp)
	protected.GET("/app-sessions/usage", newSchemaHandler.ListAppSessionsUsage)
	protected.GET("/app-sessions", newSchemaHandler.ListAppSessions)

	// Hours Insights
	protected.GET("/hours-insights", newSchemaHandler.GetHoursInsights)

	// App Items listing (protected — web admin access)
	// usage MUST be registered before the bare /app-items list route.
	protected.GET("/app-items/usage", newSchemaHandler.ListAppItemsUsage)
	protected.GET("/app-items", newSchemaHandler.ListAppItems)

	// Location samples (Phase 3 GPS — web admin access)
	protected.GET("/location-samples", newSchemaHandler.ListLocationSamples)

	// Geofence zones (Phase 3 GPS B.8)
	geofence := protected.Group("/geofence-zones")
	geofence.GET("", geofenceHandler.ListZones)
	geofence.POST("", geofenceHandler.CreateZone)
	geofence.PUT("/:id", geofenceHandler.UpdateZone)
	geofence.DELETE("/:id", geofenceHandler.DeleteZone)

	// Departments
	depts := protected.Group("/departments")
	depts.GET("", departmentHandler.ListDepartments)
	depts.POST("", departmentHandler.CreateDepartment)
	depts.PUT("/:id", departmentHandler.UpdateDepartment)
	depts.DELETE("/:id", departmentHandler.DeleteDepartment)

	// Monitoring configuration (types, categories, app/site classification)
	monitoring := protected.Group("/monitoring")
	monitoring.GET("/types", monitoringHandler.ListTypes)
	monitoring.POST("/types", monitoringHandler.CreateType)
	monitoring.PUT("/types/:id", monitoringHandler.UpdateType)
	monitoring.DELETE("/types/:id", monitoringHandler.DeleteType)
	monitoring.GET("/categories", monitoringHandler.ListCategories)
	monitoring.POST("/categories", monitoringHandler.CreateCategory)
	monitoring.PUT("/categories/:id", monitoringHandler.UpdateCategory)
	monitoring.DELETE("/categories/:id", monitoringHandler.DeleteCategory)
	monitoring.GET("/apps", monitoringHandler.ListApps)
	monitoring.PATCH("/apps/:id", monitoringHandler.UpdateAppClassification)
	monitoring.GET("/websites", monitoringHandler.ListWebsites)
	monitoring.POST("/websites", monitoringHandler.CreateWebsite)
	monitoring.PATCH("/websites/:id", monitoringHandler.UpdateSiteClassification)

	// Security & DLP — admin surface
	if dlpHandler != nil {
		dlp := protected.Group("/dlp")
		dlp.GET("/rules", dlpHandler.ListRules)
		dlp.POST("/rules", dlpHandler.CreateRule)
		dlp.GET("/rules/:id", dlpHandler.GetRule)
		dlp.GET("/rules/:id/history", dlpHandler.ListDlpRuleRevisions)
		dlp.PATCH("/rules/:id", dlpHandler.UpdateRule)
		dlp.DELETE("/rules/:id", dlpHandler.DeleteRule)
		dlp.POST("/rules/publish", dlpHandler.Publish)
		dlp.GET("/lockdown", dlpHandler.GetLockdownDraft)
		dlp.PUT("/lockdown", dlpHandler.UpdateLockdownDraft)
		dlp.GET("/lockdown/hosts", dlpHandler.ListAllowHosts)
		dlp.POST("/lockdown/hosts", dlpHandler.CreateAllowHost)
		dlp.PATCH("/lockdown/hosts/:id", dlpHandler.UpdateAllowHost)
		dlp.DELETE("/lockdown/hosts/:id", dlpHandler.DeleteAllowHost)
		dlp.GET("/lockdown/apps", dlpHandler.ListAllowApps)
		dlp.POST("/lockdown/apps", dlpHandler.CreateAllowApp)
		dlp.PATCH("/lockdown/apps/:id", dlpHandler.UpdateAllowApp)
		dlp.DELETE("/lockdown/apps/:id", dlpHandler.DeleteAllowApp)
		dlp.GET("/alerts", dlpHandler.ListAlerts)
		dlp.GET("/alerts/:id/history", dlpHandler.ListAlertHistory)
		dlp.PATCH("/alerts/:id", dlpHandler.UpdateAlert)
		webBlocker := protected.Group("/web-blocker")
		webBlocker.GET("/rules", dlpHandler.ListWebBlockerRules)
		webBlocker.POST("/domains/expand", dlpHandler.ExpandWebBlockerDomains)
		webBlocker.GET("/rules/:id", dlpHandler.GetWebBlockerRule)
		webBlocker.GET("/rules/:id/history", dlpHandler.ListWebBlockerRevisions)
		webBlocker.POST("/rules", dlpHandler.CreateWebBlockerRule)
		webBlocker.PATCH("/rules/:id", dlpHandler.UpdateWebBlockerRule)
		webBlocker.DELETE("/rules/:id", dlpHandler.DeleteWebBlockerRule)
		protected.GET("/shadow-it/apps", dlpHandler.ListShadowIt)
		protected.PATCH("/shadow-it/apps/:id", dlpHandler.UpdateShadowIt)
		protected.POST("/devices/removal-code", dlpHandler.IssueRemovalCode)
	}

	// RBAC: module catalog + roles with per-submodule permissions
	protected.GET("/modules", rbacHandler.ListModules)

	rolesGroup := protected.Group("/roles")
	rolesGroup.GET("", rbacHandler.ListRoles)
	rolesGroup.POST("", rbacHandler.CreateRole)
	rolesGroup.PUT("/:id", rbacHandler.UpdateRole)
	rolesGroup.DELETE("/:id", rbacHandler.DeleteRole)

	// Shifts: catalog CRUD + dropdown list (the /api/v1/shifts/all endpoint
	// feeds the employee-form and self-service-profile dropdowns).
	shifts := protected.Group("/shifts")
	shifts.GET("", shiftHandler.ListShifts)
	shifts.GET("/all", shiftHandler.ListAllShifts)
	shifts.POST("", shiftHandler.CreateShift)
	shifts.PUT("/:id", shiftHandler.UpdateShift)
	shifts.DELETE("/:id", shiftHandler.DeleteShift)

	holidays := protected.Group("/holidays")
	holidays.GET("", timeAttendanceHandler.ListHolidays)
	holidays.POST("", timeAttendanceHandler.CreateHoliday)
	holidays.PUT("/:id", timeAttendanceHandler.UpdateHoliday)
	holidays.DELETE("/:id", timeAttendanceHandler.DeleteHoliday)

	protected.GET("/attendance/today", timeAttendanceHandler.GetToday)
	protected.GET("/attendance/events", timeAttendanceHandler.GetDayEvents)
	protected.GET("/attendance/range", timeAttendanceHandler.GetRange)

	// Live stream — web admin surface.
	// employees + watch-ticket stay under JWTAuth (cookies work on REST).
	// watch WebSocket is public but requires a one-time ticket (browsers often
	// omit httpOnly cookies on cross-port WS handshakes to :8080 from :3000).
	if streamHandler != nil {
		protected.GET("/live-stream/employees", streamHandler.ListEmployees)
		protected.GET("/live-stream/watch-ticket", streamHandler.IssueWatchTicket)
		e.GET("/api/v1/live-stream/watch", streamHandler.Watch)
	}
}
