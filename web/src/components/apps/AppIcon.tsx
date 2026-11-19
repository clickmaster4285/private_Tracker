'use client';

import { useState } from 'react';
import { monitoringApi } from '@/lib/api';

/**
 * Catalog app icon (PNG extracted on the employee PC). Falls back to a letter tile when
 * the app has no icon yet or the image fails to load.
 */
export function AppIcon({
  appId,
  name,
  iconVersion,
  size = 28,
  tryWithoutVersion = false,
}: {
  appId?: string | null;
  name: string;
  /** From the catalog list; 0/undefined = no icon (no request is made). */
  iconVersion?: number;
  size?: number;
  /** Request the icon even without a known version (rows that only carry the app id). */
  tryWithoutVersion?: boolean;
}) {
  const [failed, setFailed] = useState(false);
  const canLoad = !!appId && !failed && (!!iconVersion || tryWithoutVersion);
  const letter = (name.trim()[0] ?? '?').toUpperCase();

  if (canLoad) {
    return (
      // eslint-disable-next-line @next/next/no-img-element -- authenticated API image, not a static asset
      <img
        src={monitoringApi.apps.iconSrc(appId, iconVersion)}
        alt=""
        width={size}
        height={size}
        loading="lazy"
        onError={() => setFailed(true)}
        className="shrink-0 rounded-md object-contain"
        style={{ width: size, height: size }}
      />
    );
  }
  return (
    <span
      aria-hidden
      className="shrink-0 rounded-md bg-muted text-muted-foreground font-semibold flex items-center justify-center"
      style={{ width: size, height: size, fontSize: Math.round(size * 0.45) }}
    >
      {letter}
    </span>
  );
}
