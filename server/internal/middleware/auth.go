package middleware

import (
	"net/http"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/handlers"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

const (
	authCookieName = "auth_token"
)

// JWTAuth returns an Echo middleware that validates JWT from cookie or Authorization header.
func JWTAuth(authService *services.AuthService) echo.MiddlewareFunc {
	return func(next echo.HandlerFunc) echo.HandlerFunc {
		return func(c echo.Context) error {
			tokenString := ""

			// Try cookie first
			cookie, err := c.Cookie(authCookieName)
			if err == nil && cookie.Value != "" {
				tokenString = cookie.Value
			}

			// Fallback to Authorization header (for API clients)
			if tokenString == "" {
				authHeader := c.Request().Header.Get("Authorization")
				if strings.HasPrefix(authHeader, "Bearer ") {
					tokenString = strings.TrimPrefix(authHeader, "Bearer ")
				}
			}

			if tokenString == "" {
				return c.JSON(http.StatusUnauthorized, dto.APIError{
					Code:    http.StatusUnauthorized,
					Message: "Authentication required",
				})
			}

			claims, err := authService.ValidateToken(tokenString)
			if err != nil || claims.Issuer != "alpha-ai-tracker" {
				return c.JSON(http.StatusUnauthorized, dto.APIError{
					Code:    http.StatusUnauthorized,
					Message: "Invalid or expired token",
				})
			}

			// Set user info in context
			c.Set("user_id", claims.UserID)

			// Check user status — if not active, clear the cookie and deny access
			user, err := authService.GetUserByID(c.Request().Context(), claims.UserID)
			if err != nil {
				c.Logger().Errorf("failed to load user %s for status check: %v", claims.UserID, err)
				return next(c)
			}
			if user != nil && user.Status != "active" {
				handlers.ClearAuthCookie(c)
				return c.JSON(http.StatusUnauthorized, dto.APIError{
					Code:    http.StatusUnauthorized,
					Message: "Account is not active",
				})
			}

			return next(c)
		}
	}
}

// OptionalAuth is like JWTAuth but doesn't fail if no token is provided.
// Useful for endpoints where auth is optional.
func OptionalAuth(authService *services.AuthService) echo.MiddlewareFunc {
	return func(next echo.HandlerFunc) echo.HandlerFunc {
		return func(c echo.Context) error {
			tokenString := ""

			cookie, err := c.Cookie(authCookieName)
			if err == nil && cookie.Value != "" {
				tokenString = cookie.Value
			}

			if tokenString == "" {
				authHeader := c.Request().Header.Get("Authorization")
				if strings.HasPrefix(authHeader, "Bearer ") {
					tokenString = strings.TrimPrefix(authHeader, "Bearer ")
				}
			}

			if tokenString != "" {
				claims, err := authService.ValidateToken(tokenString)
				if err == nil && claims.Issuer == "alpha-ai-tracker" {
					c.Set("user_id", claims.UserID)
				}
			}

			return next(c)
		}
	}
}
