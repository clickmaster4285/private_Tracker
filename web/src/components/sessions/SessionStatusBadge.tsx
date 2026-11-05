'use client';

import { Activity, AlertCircle, CheckCircle2, WifiOff } from 'lucide-react';
import type { AppSession } from '@/lib/api';

// 4-state lifecycle (2026-09-02 + OFFLINE 2026-09-02):
//   ACTIVE  = the tracker is alive and reporting (last_sync_at < OFFLINE_AFTER)
//   OFFLINE = the whole machine has been silent for OFFLINE_AFTER..STALE_AFTER
//             (network drop, PC sleep, uninstall) — the session MAY still be live
//             once the machine comes back; the client resync will resurrect it.
//   STALE   = the gap is real and large (STALE_AFTER..CLOSE_AFTER) — the device
//             is probably shut down or gone.
//   CLOSED  = terminal (CLOSE_AFTER+, or the client sent ended_at).
// Server is the authority on the status column. The sweep job in
// server/internal/jobs/session_lifecycle_sweep.go flips the value
// per-machine based on whether ANY row for that machine_id has a recent
// last_sync_at.
export type SessionStatus = 'ACTIVE' | 'OFFLINE' | 'STALE' | 'CLOSED';

export function sessionStatus(s: Pick<AppSession, 'endedAt' | 'status'>): SessionStatus {
  // Trust the server-declared status. Pre-031 rows don't carry the field
  // — fall back to the legacy "endedAt ? CLOSED : ACTIVE" rule so the UI
  // keeps rendering for legacy data.
  const declared = s.status?.toUpperCase();
  if (
    declared === 'ACTIVE' ||
    declared === 'OFFLINE' ||
    declared === 'STALE' ||
    declared === 'CLOSED'
  ) {
    return declared;
  }
  return s.endedAt ? 'CLOSED' : 'ACTIVE';
}

/** True when the session is genuinely still open on a live tracker. */
export function isSessionLive(s: Pick<AppSession, 'endedAt' | 'status'>): boolean {
  return sessionStatus(s) === 'ACTIVE' && !s.endedAt;
}

/**
 * Status pill used across employee-journey and logs/comprehensive.
 * ACTIVE  → green pulse + "Running"
 * OFFLINE → orange pulse + "Offline" (machine went silent — the client may
 *           still be running locally and reconnect)
 * STALE   → amber dot + "Stale · last sync X ago" (computed by caller)
 * CLOSED  → muted dot + "Closed"
 */
export default function SessionStatusBadge({
  status,
  staleSinceLabel,
  className = '',
}: {
  status: SessionStatus;
  staleSinceLabel?: string;
  className?: string;
}) {
  if (status === 'ACTIVE') {
    return (
      <span className={`inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-success/15 text-success transition-transform duration-200 hover:scale-105 ${className}`}>
        <span className="w-1.5 h-1.5 rounded-full bg-success animate-pulse-soft" />
        <Activity className="w-3 h-3" />
        Running
      </span>
    );
  }
  if (status === 'OFFLINE') {
    return (
      <span
        className={`inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-orange-500/15 text-orange-600 dark:text-orange-400 transition-transform duration-200 hover:scale-105 ${className}`}
        title={staleSinceLabel ? `Tracker last synced ${staleSinceLabel} — machine unreachable, session may still be running` : 'Machine unreachable — last sync is older than the offline threshold'}
      >
        <span className="w-1.5 h-1.5 rounded-full bg-orange-500 animate-pulse-soft" />
        <WifiOff className="w-3 h-3" />
        Offline{staleSinceLabel ? ` · ${staleSinceLabel}` : ''}
      </span>
    );
  }
  if (status === 'STALE') {
    return (
      <span
        className={`inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-warning/15 text-warning transition-transform duration-200 hover:scale-105 ${className}`}
        title={staleSinceLabel ? `Tracker last synced ${staleSinceLabel} — may still be running offline` : undefined}
      >
        <span className="w-1.5 h-1.5 rounded-full bg-warning" />
        <AlertCircle className="w-3 h-3" />
        Stale{staleSinceLabel ? ` · ${staleSinceLabel}` : ''}
      </span>
    );
  }
  return (
    <span className={`inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium bg-muted text-muted-foreground transition-transform duration-200 hover:scale-105 ${className}`}>
      <CheckCircle2 className="w-3 h-3" />
      Closed
    </span>
  );
}