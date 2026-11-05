package handlers

import (
	"log"
	"net/http"
	"strconv"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

type GeofenceHandler struct {
	service *services.GeofenceService
}

func NewGeofenceHandler(service *services.GeofenceService) *GeofenceHandler {
	return &GeofenceHandler{service: service}
}

func (h *GeofenceHandler) ListZones(c echo.Context) error {
	resp, err := h.service.ListZones(c.Request().Context())
	if err != nil {
		log.Printf("[geofence] ListZones error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to list geofence zones", Detail: err.Error(),
		})
	}
	return c.JSON(http.StatusOK, resp)
}

func (h *GeofenceHandler) CreateZone(c echo.Context) error {
	var req dto.CreateGeofenceZoneRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	resp, err := h.service.CreateZone(c.Request().Context(), req)
	if err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: err.Error()})
	}
	return c.JSON(http.StatusCreated, resp)
}

func (h *GeofenceHandler) UpdateZone(c echo.Context) error {
	id, err := strconv.Atoi(c.Param("id"))
	if err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid zone id"})
	}
	var req dto.UpdateGeofenceZoneRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	resp, err := h.service.UpdateZone(c.Request().Context(), id, req)
	if err != nil {
		if err.Error() == "geofence zone not found" {
			return c.JSON(http.StatusNotFound, dto.APIError{Code: http.StatusNotFound, Message: err.Error()})
		}
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

func (h *GeofenceHandler) DeleteZone(c echo.Context) error {
	id, err := strconv.Atoi(c.Param("id"))
	if err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid zone id"})
	}
	if err := h.service.DeleteZone(c.Request().Context(), id); err != nil {
		return c.JSON(http.StatusNotFound, dto.APIError{Code: http.StatusNotFound, Message: "Geofence zone not found"})
	}
	return c.JSON(http.StatusOK, map[string]string{"message": "deleted"})
}
