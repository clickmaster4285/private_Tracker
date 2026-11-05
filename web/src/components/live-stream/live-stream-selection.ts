'use client';

/**
 * Shared selection helpers for live-stream pages (main console + theater popout).
 */

/** Main `/live-stream` console — compact 2×2 wall. */
export const LIVE_STREAM_MAX_TILES = 4;

/**
 * Theater popout (`/live-stream/theater`) — more concurrent previews with scroll.
 * Bound so one admin tab cannot open unbounded PeerConnections.
 */
export const LIVE_STREAM_THEATER_MAX_TILES = 16;

export const LIVE_STREAM_COLS_OPTIONS = [1, 2, 3, 4] as const;
export type LiveStreamCols = (typeof LIVE_STREAM_COLS_OPTIONS)[number];

export function parseLiveStreamIds(
  raw: string,
  max = LIVE_STREAM_MAX_TILES,
): string[] {
  return raw
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
    .slice(0, max);
}

export function toggleLiveStreamId(
  selectedIds: string[],
  employeeId: string,
  max = LIVE_STREAM_MAX_TILES,
): string[] {
  const set = new Set(selectedIds);
  if (set.has(employeeId)) {
    set.delete(employeeId);
  } else {
    if (set.size >= max) return selectedIds;
    set.add(employeeId);
  }
  return [...set];
}

export function parseLiveStreamCols(raw: string, fallback: LiveStreamCols = 2): LiveStreamCols {
  const n = Number.parseInt(raw, 10);
  if ((LIVE_STREAM_COLS_OPTIONS as readonly number[]).includes(n)) {
    return n as LiveStreamCols;
  }
  return fallback;
}
