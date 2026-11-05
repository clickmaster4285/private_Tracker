'use client';

import LocationComingSoon from '@/components/location/LocationComingSoon';
import { LOCATION_UI_ENABLED } from '@/lib/locationUi';
import GpsLocationLivePage from './GpsLocationLive';

/**
 * Route shell for /gps-location. Live UI is in GpsLocationLive.tsx;
 * gated by LOCATION_UI_ENABLED in web/src/lib/locationUi.ts.
 */
export default function GPSLocationPage() {
  if (LOCATION_UI_ENABLED) return <GpsLocationLivePage />;
  return <LocationComingSoon variant="fleet" />;
}
