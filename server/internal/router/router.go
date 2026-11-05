package router

import (
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
	trackingControlHandler *handlers.TrackingControlHandler,
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
	// Public Routes (no auth required)
	// ─────────────────────────────
	a := e.Group("/api/v1/auth")
	a.POST("/login", authHandler.Login)
	a.POST("/refresh", authHandler.Refresh)
	a.POST("/employee-login", authHandler.EmployeeLogin)

	// Public GitHub Releases bridge (private repo → public installer downloads).
	// No JWT / DeviceAuth — visitors on /releases must download without login.
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

	// Phase 1 sync endpoints
	syncGroup.POST("/device-hardware/sync", newSchemaHandler.SyncDeviceHardware)
	syncGroup.POST("/installed-apps/sync", newSchemaHandler.SyncInstalledApps)
	syncGroup.POST("/installed-packages/sync", newSchemaHandler.SyncInstalledPackages)
	syncGroup.POST("/network-info/sync", newSchemaHandler.SyncNetworkInfo)
	syncGroup.POST("/session-events/sync", newSchemaHandler.SyncSessionEvents)

	// Phase 2 sync endpoints
	syncGroup.POST("/app-sessions/sync", newSchemaHandler.SyncAppSessions)
	syncGroup.POST("/app-items/sync", newSchemaHandler.SyncAppItems)

	// Phase 3 sync endpoints
	syncGroup.POST("/app-status/sync", newSchemaHandler.SyncAppStatus)
	syncGroup.POST("/hardware-devices/sync", newSchemaHandler.SyncHardwareDevices)
	syncGroup.POST("/permission-status/sync", newSchemaHandler.SyncPermissionStatus)
	syncGroup.POST("/storage-devices/sync", newSchemaHandler.SyncStorageDevices)
	syncGroup.POST("/location-samples/sync", newSchemaHandler.SyncLocationSamples)
	// "Remove the account" signal (R4/R5): called by the client AFTER it has drained its
	// unsynced rows. Marks the CALLING machine 'old' and the employee untracked; the client
	// then wipes its local DB + machine id. Lives in the DeviceAuth group because a device
	// token (not a web-admin session) identifies the machine.
	syncGroup.POST("/devices/self-remove", newSchemaHandler.SelfRemoveAccount)
	syncGroup.GET("/schedules/me", timeAttendanceHandler.GetMySchedule)

	// Security & DLP — device surface (policy pull, alert sync, health, removal verify)
	if dlpHandler != nil {
		syncGroup.GET("/dlp/policy", dlpHandler.GetPolicy)
		syncGroup.POST("/dlp/alerts/sync", dlpHandler.SyncAlerts)
		syncGroup.POST("/dlp/health", dlpHandler.PostHealth)
		syncGroup.POST("/devices/removal-verify", dlpHandler.VerifyRemovalCode)
	}

	// Device tracking control (Fixed/Roaming) — DeviceAuth
	if trackingControlHandler != nil {
		syncGroup.GET("/devices/control", trackingControlHandler.GetControl)
		syncGroup.POST("/devices/tracking-events", trackingControlHandler.SyncTrackingEvents)
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
	if trackingControlHandler != nil {
		employees.PATCH("/:id/machines/:machineId/tracking-mode", trackingControlHandler.SetMachineTrackingMode)
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

	// Device Configuration (Fixed/Roaming admin surfaces)
	if trackingControlHandler != nil {
		dc := protected.Group("/device-configuration")
		dc.GET("/roaming-machines", trackingControlHandler.ListRoamingMachines)
		dc.GET("/tracking-events", trackingControlHandler.ListTrackingEventLogs)
		dc.GET("/mode-types", trackingControlHandler.GetTrackerModeTypes)
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
