package handlers

import (
	"net/http"
	"strconv"

	"github.com/labstack/echo/v4"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
)

// UserHandler handles user CRUD endpoints.
type UserHandler struct {
	userService *services.UserService
}

// NewUserHandler creates a new UserHandler.
func NewUserHandler(userService *services.UserService) *UserHandler {
	return &UserHandler{userService: userService}
}

// ListUsers handles GET /api/v1/users
func (h *UserHandler) ListUsers(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	roleID, _ := strconv.Atoi(c.QueryParam("roleId"))

	params := repository.ListParams{
		Search:        c.QueryParam("search"),
		RoleID:        roleID,
		Status:        c.QueryParam("status"),
		AccountStatus: c.QueryParam("accountStatus"),
		Page:          page,
		PerPage:       perPage,
	}

	result, err := h.userService.List(c.Request().Context(), params)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list users",
			Detail:  err.Error(),
		})
	}

	return c.JSON(http.StatusOK, result)
}

// GetUser handles GET /api/v1/users/:id
func (h *UserHandler) GetUser(c echo.Context) error {
	id := c.Param("id")

	user, err := h.userService.GetByID(c.Request().Context(), id)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to get user",
			Detail:  err.Error(),
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

// CreateUser handles POST /api/v1/users
func (h *UserHandler) CreateUser(c echo.Context) error {
	var req dto.CreateUserRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	if req.Name == "" || req.Email == "" || req.RoleID <= 0 {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Name, email, and roleId are required",
		})
	}

	user, err := h.userService.Create(c.Request().Context(), &req)
	if err != nil {
		code := http.StatusInternalServerError
		if err.Error() == "email already exists" {
			code = http.StatusConflict
		}
		return c.JSON(code, dto.APIError{
			Code:    code,
			Message: err.Error(),
		})
	}

	return c.JSON(http.StatusCreated, user)
}

// UpdateUser handles PUT /api/v1/users/:id
func (h *UserHandler) UpdateUser(c echo.Context) error {
	id := c.Param("id")

	var req dto.UpdateUserRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	user, err := h.userService.Update(c.Request().Context(), id, &req)
	if err != nil {
		code := http.StatusInternalServerError
		switch err.Error() {
		case "user not found":
			code = http.StatusNotFound
		case "email already exists":
			code = http.StatusConflict
		case "a valid role is required", "invalid status":
			code = http.StatusBadRequest
		case "cannot suspend company admin":
			// Mirrors Delete's "cannot delete company admin" refusal.
			code = http.StatusForbidden
		}
		return c.JSON(code, dto.APIError{
			Code:    code,
			Message: err.Error(),
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

// DeleteUser handles DELETE /api/v1/users/:id
func (h *UserHandler) DeleteUser(c echo.Context) error {
	id := c.Param("id")

	if err := h.userService.Delete(c.Request().Context(), id); err != nil {
		code := http.StatusInternalServerError
		if err.Error() == "user not found" {
			code = http.StatusNotFound
		} else if err.Error() == "cannot delete company admin" {
			code = http.StatusForbidden
		}
		return c.JSON(code, dto.APIError{
			Code:    code,
			Message: err.Error(),
		})
	}

	return c.JSON(http.StatusOK, map[string]interface{}{
		"message": "user deleted successfully",
	})
}
