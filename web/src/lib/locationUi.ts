/**
 * Web location dashboard gate (Phase 3 GPS).
 *
 * When `false`, `/gps-location` and `/employee-journey/location` render a
 * Coming Soon shell. Live implementations live in `GpsLocationLive.tsx` and
 * `LocationTrailLive.tsx` — flip this flag to re-enable without rewriting UI.
 *
 * Client/server location sync is unchanged; this only hides the admin UI.
 */
export const LOCATION_UI_ENABLED = false;
