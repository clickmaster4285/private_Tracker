package handlers

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"net/http"
	"time"

	"github.com/alpha-ai-tracker/server/internal/config"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

const (
	authCookieName    = "auth_token"
	refreshCookieName = "refresh_token"
	authCookiePath    = "/"
)

func setAuthCookies(c echo.Context, accessValue string, accessExpiry time.Time, refreshValue string, refreshExpiry time.Time) {
	access := &http.Cookie{
		Name:     authCookieName,
		Value:    accessValue,
		Path:     authCookiePath,
		HttpOnly: true,
		Secure:   false, // Set to true in production with HTTPS
		SameSite: http.SameSiteLaxMode,
		Expires:  accessExpiry,
	}
	c.SetCookie(access)

	if refreshValue != "" {
		refresh := &http.Cookie{
			Name:     refreshCookieName,
			Value:    refreshValue,
			Path:     authCookiePath,
			HttpOnly: true,
			Secure:   false, // Set to true in production with HTTPS
			SameSite: http.SameSiteLaxMode,
			Expires:  refreshExpiry,
		}
		c.SetCookie(refresh)
	}
}

func ClearAuthCookie(c echo.Context) {
	for _, name := range []string{authCookieName, refreshCookieName} {
		c.SetCookie(&http.Cookie{
			Name:     name,
			Value:    "",
			Path:     authCookiePath,
			HttpOnly: true,
			Secure:   false,
			SameSite: http.SameSiteLaxMode,
			MaxAge:   -1,
			Expires:  time.Unix(0, 0),
		})
	}
}

func readCookie(c echo.Context, name string) string {
	cookie, err := c.Cookie(name)
	if err != nil {
		return ""
	}
	return cookie.Value
}

// AuthHandler handles authentication endpoints.
type AuthHandler struct {
	authService  *services.AuthService
	userService  *services.UserService
	employeeRepo *repository.EmployeeRepo
	deviceRepo   *repository.DeviceRepo
	redisClient  services.RedisClientInterface
	jwtCfg       config.JWTConfig
}

// NewAuthHandler creates a new AuthHandler.
func NewAuthHandler(
	authService *services.AuthService,
	userService *services.UserService,
	employeeRepo *repository.EmployeeRepo,
	deviceRepo *repository.DeviceRepo,
	redisClient services.RedisClientInterface,
	jwtCfg config.JWTConfig,
) *AuthHandler {
	return &AuthHandler{
		authService:  authService,
		userService:  userService,
		employeeRepo: employeeRepo,
		deviceRepo:   deviceRepo,
		redisClient:  redisClient,
		jwtCfg:       jwtCfg,
	}
}

// Login handles POST /api/v1/auth/login
func (h *AuthHandler) Login(c echo.Context) error {
	var req dto.LoginRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	if req.Email == "" || req.Password == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Email and password are required",
		})
	}

	resp, err := h.authService.Login(c.Request().Context(), &req)
	if err != nil {
		// 403 (not 401) for a suspended account: the credentials were valid, the
		// account is simply not authorized, and the web shows the message as-is.
		if errors.Is(err, services.ErrAccountSuspended) {
			return c.JSON(http.StatusForbidden, dto.APIError{
				Code:    http.StatusForbidden,
				Message: err.Error(),
			})
		}
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code:    http.StatusUnauthorized,
			Message: err.Error(),
		})
	}

	setAuthCookies(c, resp.Token, time.Now().Add(h.jwtCfg.AccessExpiry), resp.RefreshToken, resp.RefreshExpiresAt)

	return c.JSON(http.StatusOK, dto.LoginResponse{
		User: resp.User,
	})
}

// Refresh handles POST /api/v1/auth/refresh — public route.
// Validates + rotates the refresh-token cookie and re-mints both cookies.
// A 401 here means the session is gone for good; the web client force-redirects to /login.
func (h *AuthHandler) Refresh(c echo.Context) error {
	rawRefresh := readCookie(c, refreshCookieName)
	if rawRefresh == "" {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code:    http.StatusUnauthorized,
			Message: "Refresh token missing",
		})
	}

	resp, err := h.authService.RefreshSession(c.Request().Context(), rawRefresh)
	if err != nil {
		if errors.Is(err, services.ErrAccountSuspended) {
			ClearAuthCookie(c)
			return c.JSON(http.StatusForbidden, dto.APIError{
				Code:    http.StatusForbidden,
				Message: err.Error(),
			})
		}
		ClearAuthCookie(c)
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code:    http.StatusUnauthorized,
			Message: "Invalid or expired refresh token",
		})
	}

	setAuthCookies(c, resp.Token, time.Now().Add(h.jwtCfg.AccessExpiry), resp.RefreshToken, resp.RefreshExpiresAt)

	return c.JSON(http.StatusOK, dto.LoginResponse{
		User: resp.User,
	})
}

// Logout handles POST /api/v1/auth/logout
func (h *AuthHandler) Logout(c echo.Context) error {
	// Revoke the presented refresh token so it can never mint another access token
	if err := h.authService.RevokeRefreshToken(c.Request().Context(), readCookie(c, refreshCookieName)); err != nil {
		c.Logger().Errorf("failed to revoke refresh token: %v", err)
	}

	ClearAuthCookie(c)

	return c.JSON(http.StatusOK, map[string]interface{}{
		"message": "logged out successfully",
	})
}

// Me handles GET /api/v1/auth/me — returns current user (with role permissions) from cookie
func (h *AuthHandler) Me(c echo.Context) error {
	userID, ok := c.Get("user_id").(string)
	if !ok || userID == "" {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code:    http.StatusUnauthorized,
			Message: "Authentication required",
		})
	}

	user, err := h.authService.GetUserResponseByID(c.Request().Context(), userID)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to get user",
		})
	}
	if user == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code:    http.StatusNotFound,
			Message: "User not found",
		})
	}

	return c.JSON(http.StatusOK, user)
}

// GetProfile handles GET /api/v1/auth/profile — returns the aggregate
// profile payload (user + role + RBAC view + linked employee) for the
// /settings/profile page. The JWT cookie supplies identity; no path param.
func (h *AuthHandler) GetProfile(c echo.Context) error {
	userID, ok := c.Get("user_id").(string)
	if !ok || userID == "" {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code:    http.StatusUnauthorized,
			Message: "Authentication required",
		})
	}

	profile, err := h.userService.GetProfile(c.Request().Context(), userID)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to load profile",
			Detail:  err.Error(),
		})
	}
	if profile == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code:    http.StatusNotFound,
			Message: "User not found",
		})
	}

	return c.JSON(http.StatusOK, profile)
}

// CheckAuth handles GET /api/v1/auth/check — lightweight auth status check (optional auth)
func (h *AuthHandler) CheckAuth(c echo.Context) error {
	userID, ok := c.Get("user_id").(string)
	if !ok || userID == "" {
		return c.JSON(http.StatusOK, dto.AuthCheckResponse{
			Authenticated: false,
		})
	}

	user, err := h.authService.GetUserResponseByID(c.Request().Context(), userID)
	if err != nil || user == nil {
		return c.JSON(http.StatusOK, dto.AuthCheckResponse{
			Authenticated: false,
		})
	}

	// A suspended account is not authorized. Report unauthenticated AND drop the
	// cookies, so the web routes the user to /login immediately (where the next
	// sign-in attempt surfaces the 403 "Account suspended" message) instead of
	// letting an already-issued 15-minute access token keep the session alive.
	if user.Status == "suspended" {
		ClearAuthCookie(c)
		return c.JSON(http.StatusOK, dto.AuthCheckResponse{
			Authenticated: false,
		})
	}

	return c.JSON(http.StatusOK, dto.AuthCheckResponse{
		Authenticated: true,
		User:          user,
	})
}

// EmployeeLogin handles POST /api/v1/auth/employee-login
func (h *AuthHandler) EmployeeLogin(c echo.Context) error {
	var req dto.EmployeeLoginRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	if req.EmployeeID == "" || req.SecretKey == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Employee ID and secret key are required",
		})
	}

	// Check Redis is available
	if h.redisClient == nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Employee authentication is unavailable (Redis not connected)",
		})
	}

	// Validate secret from Redis
	valid, err := h.redisClient.ValidateSecret(c.Request().Context(), req.EmployeeID, req.SecretKey)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to validate secret",
			Detail:  err.Error(),
		})
	}
	if !valid {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code:    http.StatusUnauthorized,
			Message: "Invalid or expired secret key",
		})
	}

	// Look up employee
	emp, err := h.employeeRepo.GetByEmployeeID(c.Request().Context(), req.EmployeeID)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to get employee",
			Detail:  err.Error(),
		})
	}
	if emp == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code:    http.StatusNotFound,
			Message: "Employee not found",
		})
	}

	// Generate JWT for employee session
	token, err := h.authService.GenerateEmployeeToken(emp)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to generate token",
			Detail:  err.Error(),
		})
	}

	// Update employee tracking status to tracked and set online
	updates := map[string]interface{}{
		"tracking_status": "tracked",
		"is_online":       true,
	}
	if _, err := h.employeeRepo.Update(c.Request().Context(), emp.ID, updates); err != nil {
		// Non-fatal — log but don't block login
		c.Logger().Errorf("failed to update employee tracking status: %v", err)
	}

	// Update in-memory struct for the response (avoid re-fetch from DB)
	emp.TrackingStatus = "tracked"
	emp.IsOnline = true

	resp := dto.EmployeeLoginResponse{
		Employee: dto.EmployeeResponse{
			ID:              emp.ID,
			EmployeeID:      emp.EmployeeID,
			Name:            emp.Name,
			Email:           emp.Email,
			Department:      emp.Department,
			DepartmentID:    emp.DepartmentID,
			Shift:           emp.Shift,
			TrackingEnabled: emp.TrackingEnabled,
			TrackingStatus:  emp.TrackingStatus,
			IsOnline:        emp.IsOnline,
			Avatar:          emp.Avatar,
			AvatarColor:     emp.AvatarColor,
			CreatedAt:       emp.CreatedAt,
			UpdatedAt:       emp.UpdatedAt,
		},
		Token: token,
	}

	// Issue long-lived device token if deviceRepo is available
	if h.deviceRepo != nil {
		tokenBytes := make([]byte, 32)
		if _, err := rand.Read(tokenBytes); err == nil {
			rawDeviceToken := "dev_tok_" + hex.EncodeToString(tokenBytes)
			hasher := sha256.New()
			hasher.Write([]byte(rawDeviceToken))
			tokenHash := hex.EncodeToString(hasher.Sum(nil))

			machineID := req.MachineID
			if machineID == "" {
				machineID = "default-" + emp.EmployeeID
			}
			platform := req.Platform
			if platform == "" {
				platform = "unknown"
			}
			clientVer := req.ClientVersion
			if clientVer == "" {
				clientVer = "1.0.0"
			}

			device, err := h.deviceRepo.UpsertDevice(
				c.Request().Context(),
				emp.EmployeeID,
				machineID,
				platform,
				clientVer,
				req.DeviceName,
				tokenHash,
				nil, // non-expiring by default (revocable)
			)
			if err == nil && device != nil {
				resp.DeviceToken = rawDeviceToken
				resp.DeviceID = device.ID
			}

			// R3/R7: record the machine in the canonical lifecycle registry. A machine
			// that was marked 'old' and logs in again — or a brand-new machine id after
			// a local wipe — becomes 'active'; first_track_at is preserved for a machine
			// we have already seen, so re-login never restarts its history.
			if _, merr := h.deviceRepo.UpsertEmployeeMachineActive(c.Request().Context(), emp.EmployeeID, machineID); merr != nil {
				c.Logger().Errorf("failed to upsert employee machine: %v", merr)
			}
		}
	}

	return c.JSON(http.StatusOK, resp)
}

// ListEmployeeDevices handles GET /api/v1/employees/:id/devices (web admin)
func (h *AuthHandler) ListEmployeeDevices(c echo.Context) error {
	employeeID := c.Param("id")
	if employeeID == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Employee ID is required"})
	}

	if h.deviceRepo == nil {
		return c.JSON(http.StatusOK, []interface{}{})
	}

	devices, err := h.deviceRepo.ListByEmployeeID(c.Request().Context(), employeeID)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to list devices", Detail: err.Error()})
	}

	return c.JSON(http.StatusOK, devices)
}

// RevokeDevice handles POST /api/v1/devices/:id/revoke (web admin)
func (h *AuthHandler) RevokeDevice(c echo.Context) error {
	deviceID := c.Param("id")
	if deviceID == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Device ID is required"})
	}

	if h.deviceRepo == nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Device repository unavailable"})
	}

	if err := h.deviceRepo.RevokeDevice(c.Request().Context(), deviceID); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Failed to revoke device", Detail: err.Error()})
	}

	return c.JSON(http.StatusOK, map[string]string{"message": "Device access revoked successfully"})
}


