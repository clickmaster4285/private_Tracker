package middleware

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"net/http"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

// DeviceAuth creates middleware that authenticates device tokens (or fallback employee JWTs)
// and attaches employee_id and device_id to Echo context.
func DeviceAuth(deviceRepo *repository.DeviceRepo, userRepo *repository.UserRepo, authService *services.AuthService) echo.MiddlewareFunc {
	return func(next echo.HandlerFunc) echo.HandlerFunc {
		return func(c echo.Context) error {
			authHeader := c.Request().Header.Get("Authorization")
			var token string

			if authHeader != "" {
				parts := strings.SplitN(authHeader, " ", 2)
				if len(parts) == 2 && (strings.EqualFold(parts[0], "Device") || strings.EqualFold(parts[0], "Bearer")) {
					token = strings.TrimSpace(parts[1])
				}
			}

			// Fallback: check query parameter or request header directly
			if token == "" {
				token = c.QueryParam("token")
			}

			if token == "" {
				return c.JSON(http.StatusUnauthorized, dto.APIError{
					Code:    http.StatusUnauthorized,
					Message: "Missing Authorization header or device token",
				})
			}

			// 1. Attempt device token lookup by SHA-256 hash
			hasher := sha256.New()
			hasher.Write([]byte(token))
			tokenHash := hex.EncodeToString(hasher.Sum(nil))

			device, err := deviceRepo.GetByTokenHashIncludingRevoked(c.Request().Context(), tokenHash)
			if err == nil && device != nil {
				// A revoked (removed) device is accepted on purpose so a machine whose
				// device was removed can still deliver its final batch of data — the
				// handler persists it FIRST, then answers action=wipe_local (R6).
				// We must not refresh last_seen for it: the machine is no longer tracked.
				if device.RevokedAt != nil {
					c.Set("employee_id", device.EmployeeID)
					c.Set("device_id", device.ID)
					c.Set("device_machine_id", device.MachineID)
					c.Set("device_removed", true)
					return next(c)
				}

				// Copy ID out of the pooled Echo context; use Background + timeout
				// so TouchLastSeen does not race request cancellation after WS upgrade.
				// Version/platform headers let a reinstalled client refresh employee_devices
				// without requiring a fresh employee-login (web Client Version column).
				devID := device.ID
				empID := device.EmployeeID
				machineID := device.MachineID
				clientVer := strings.TrimSpace(c.Request().Header.Get("X-Client-Version"))
				platform := strings.TrimSpace(c.Request().Header.Get("X-Client-Platform"))
				go func(id, emp, machine, ver, plat string) {
					ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
					defer cancel()
					_ = deviceRepo.TouchLastSeen(ctx, id, ver, plat)
					// Keep the machine registry's activity window current so legacy
					// attribution (migrations 047/050) has a live upper bound.
					_ = deviceRepo.TouchEmployeeMachineLastTrack(ctx, emp, machine)
				}(devID, empID, machineID, clientVer, platform)

				c.Set("employee_id", device.EmployeeID)
				c.Set("device_id", device.ID)
				c.Set("device_machine_id", device.MachineID)
				return next(c)
			}

			// 2. Fallback to JWT validation for backward compatibility
			if authService != nil {
				claims, err := authService.ValidateToken(token)
				if err == nil && claims != nil && claims.UserID != "" &&
					claims.Issuer == "alpha-ai-tracker-employee" {
					// The JWT's UserID is the users.id (a UUID), but the sync
					// handlers look up by employee_id (e.g. "IS-131"). Resolve
					// the user row to get the human-readable employee_id —
					// this is the only way a Bearer-only request lands data
					// on the right employee when the device record has been
					// rotated out by another login.
					empID := claims.UserID
					if userRepo != nil {
						if u, uerr := userRepo.GetByID(c.Request().Context(), claims.UserID); uerr == nil && u != nil {
							empID = u.EmployeeID
						}
					}
					c.Set("employee_id", empID)
					return next(c)
				}
			}

			return c.JSON(http.StatusUnauthorized, dto.APIError{
				Code:    http.StatusUnauthorized,
				Message: "Invalid or revoked authorization credential",
			})
		}
	}
}
