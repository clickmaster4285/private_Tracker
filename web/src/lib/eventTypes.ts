/**
 * Authoritative session_events vocabulary — web mirror of client SessionEventTypes (R5).
 * Keep in sync with client/Core/Models/SessionEvent.cs and server/internal/models/session_event_types.go.
 */
export const SESSION_EVENT_TYPES = {
  POWER_ON: 'power_on',
  POWER_OFF: 'power_off',
  RESUME: 'resume',
  OS_LOGIN: 'os_login',
  OS_LOGOUT: 'os_logout',
  SCREEN_LOCK: 'screen_lock',
  SCREEN_UNLOCK: 'screen_unlock',
  TRACKER_LOGIN: 'tracker_login',
  UI_HIDDEN: 'ui_hidden',
  IDLE_START: 'idle_start',
  IDLE_END: 'idle_end',
  OLD_DATA_DROPPED: 'old_data_dropped',
  /** @deprecated Legacy rows written before 2026-08-28 */
  LOGIN: 'login',
} as const;

export type SessionEventType =
  (typeof SESSION_EVENT_TYPES)[keyof typeof SESSION_EVENT_TYPES];
