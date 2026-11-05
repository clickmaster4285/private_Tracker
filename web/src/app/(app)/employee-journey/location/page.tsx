'use client';

import LocationComingSoon from '@/components/location/LocationComingSoon';
import { LOCATION_UI_ENABLED } from '@/lib/locationUi';
import EmployeeJourneyLocationLive from './LocationTrailLive';

/**
 * Route shell for /employee-journey/location. Live UI is in LocationTrailLive.tsx;
 * gated by LOCATION_UI_ENABLED in web/src/lib/locationUi.ts.
 */
export default function EmployeeJourneyLocationPage() {
  if (LOCATION_UI_ENABLED) return <EmployeeJourneyLocationLive />;
  return <LocationComingSoon variant="trail" />;
}
