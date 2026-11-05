'use client';

import { cn } from '@/lib/utils';
import type { EmployeeDevice } from '@/lib/api';

/** Prefer server-projected effectiveMode; fall back to fixed (fail-safe). */
export function deviceEffectiveMode(device: EmployeeDevice): 'fixed' | 'roaming' {
  return device.effectiveMode === 'roaming' ? 'roaming' : 'fixed';
}

export function trackingModeLabel(mode: 'fixed' | 'roaming'): string {
  return mode === 'roaming' ? 'Roaming' : 'Fixed';
}

export function trackingModeHint(mode: 'fixed' | 'roaming'): string {
  return mode === 'roaming'
    ? 'User can Start/Stop tracking (home laptop)'
    : 'Always on — user cannot stop tracking';
}

interface TrackingModeBadgeProps {
  mode?: 'fixed' | 'roaming' | null;
  source?: string | null;
  className?: string;
  showSource?: boolean;
}

export default function TrackingModeBadge({
  mode,
  source,
  className,
  showSource = false,
}: TrackingModeBadgeProps) {
  const m = mode === 'roaming' ? 'roaming' : 'fixed';
  return (
    <span
      title={trackingModeHint(m) + (source ? ` · from ${source}` : '')}
      className={cn(
        'inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide border',
        m === 'roaming'
          ? 'bg-sky-500/10 text-sky-700 border-sky-500/30 dark:text-sky-300'
          : 'bg-emerald-500/10 text-emerald-700 border-emerald-500/30 dark:text-emerald-300',
        className,
      )}
    >
      {trackingModeLabel(m)}
      {showSource && source && source !== 'default' && (
        <span className="font-normal normal-case opacity-70">· {source}</span>
      )}
    </span>
  );
}

interface DeviceTrackingChipsProps {
  device: EmployeeDevice;
  className?: string;
}

/** Mode + optional desired/phase chips for Device Specs. */
export function DeviceTrackingChips({ device, className }: DeviceTrackingChipsProps) {
  const mode = deviceEffectiveMode(device);
  const desired = device.desiredState === 'stopped' ? 'stopped' : 'running';
  const phase = (device.phase || '').trim();
  return (
    <div className={cn('flex flex-wrap items-center gap-1.5', className)}>
      <TrackingModeBadge mode={mode} source={device.effectiveModeSource} showSource />
      <span
        className={cn(
          'inline-flex rounded-md px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide border',
          desired === 'stopped'
            ? 'bg-amber-500/10 text-amber-800 border-amber-500/30 dark:text-amber-300'
            : 'bg-muted text-muted-foreground border-border',
        )}
      >
        {desired}
      </span>
      {phase && !(desired === 'running' && phase === 'collecting') ? (
        <span className="inline-flex rounded-md px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground border border-border">
          {phase}
        </span>
      ) : null}
    </div>
  );
}
