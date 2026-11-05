package handlers

import (
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/labstack/echo/v4"
)

func TestGetMyScheduleRejectsMissingDeviceIdentity(t *testing.T) {
	e := echo.New()
	request := httptest.NewRequest(http.MethodGet, "/api/v1/schedules/me", nil)
	recorder := httptest.NewRecorder()
	context := e.NewContext(request, recorder)
	handler := NewTimeAttendanceHandler(nil)

	if err := handler.GetMySchedule(context); err != nil {
		t.Fatalf("GetMySchedule returned error: %v", err)
	}
	if recorder.Code != http.StatusUnauthorized {
		t.Fatalf("status = %d, want %d", recorder.Code, http.StatusUnauthorized)
	}
}

func TestServerTimeReturnsDateHeader(t *testing.T) {
	e := echo.New()
	request := httptest.NewRequest(http.MethodGet, "/api/v1/server-time", nil)
	recorder := httptest.NewRecorder()
	context := e.NewContext(request, recorder)
	handler := NewTimeAttendanceHandler(nil)

	if err := handler.ServerTime(context); err != nil {
		t.Fatalf("ServerTime returned error: %v", err)
	}
	if recorder.Code != http.StatusOK {
		t.Fatalf("status = %d, want %d", recorder.Code, http.StatusOK)
	}
	if recorder.Header().Get("Date") == "" {
		t.Fatal("Date header is missing")
	}
}
