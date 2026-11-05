package models

// Session event_type vocabulary — Go mirror of client SessionEventTypes (R5).
// Keep in sync with client/Core/Models/SessionEvent.cs and web/src/lib/eventTypes.ts.
const (
	SessionEventPowerOn        = "power_on"
	SessionEventPowerOff       = "power_off"
	SessionEventResume         = "resume"
	SessionEventOsLogin        = "os_login"
	SessionEventOsLogout       = "os_logout"
	SessionEventScreenLock     = "screen_lock"
	SessionEventScreenUnlock   = "screen_unlock"
	SessionEventTrackerLogin   = "tracker_login"
	SessionEventUiHidden       = "ui_hidden"
	SessionEventIdleStart      = "idle_start"
	SessionEventIdleEnd        = "idle_end"
	SessionEventOldDataDropped = "old_data_dropped"
	// SessionEventLogin is the pre-2026-08-28 alias kept for legacy row filters.
	SessionEventLogin = "login"
)
