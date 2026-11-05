// Shared formatting helpers for the web dashboard.

export function formatMb(mb?: number | null): string {
  if (!mb) return '—';
  if (mb >= 1024) return `${(mb / 1024).toFixed(mb >= 1048576 ? 1 : 0)} GB`;
  return `${mb} MB`;
}

export function formatDate(iso?: string | null): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

export function formatDateShort(iso?: string | null): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

export function formatDateTime(iso?: string | null): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString(undefined, {
    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
  });
}

/** Format an instant in the shift IANA timezone (matches server late/present math). */
export function formatDateTimeInZone(iso?: string | null, timeZone?: string | null): string {
  if (!iso) return '—';
  const options: Intl.DateTimeFormatOptions = {
    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
  };
  if (timeZone) {
    try {
      return new Date(iso).toLocaleString(undefined, { ...options, timeZone });
    } catch {
      // invalid IANA name — fall back to browser local
    }
  }
  return formatDateTime(iso);
}

/** Clock time in the shift IANA timezone (hours:minutes:seconds). */
export function formatTimeInZone(iso?: string | null, timeZone?: string | null): string {
  if (!iso) return '—';
  const options: Intl.DateTimeFormatOptions = {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  };
  try {
    return new Date(iso).toLocaleTimeString(undefined, timeZone ? { ...options, timeZone } : options);
  } catch {
    return new Date(iso).toLocaleTimeString(undefined, options);
  }
}

export function formatDuration(start: string, end: string): string {
  const diff = new Date(end).getTime() - new Date(start).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return '<1m';
  if (mins < 60) return `${mins}m`;
  const hours = Math.floor(mins / 60);
  const remaining = mins % 60;
  return `${hours}h ${remaining}m`;
}

export function formatSeconds(sec?: number | null): string {
  if (!sec || sec < 1) return '0s';
  const total = Math.round(sec);
  const m = Math.floor(total / 60);
  const s = total % 60;
  if (m < 1) return `${s}s`;
  if (m < 60) return `${m}m ${s}s`;
  const h = Math.floor(m / 60);
  return `${h}h ${m % 60}m`;
}

/** Compact "3m ago / 2h ago / 1d ago" used for the STALE session pill. */
export function formatRelative(iso?: string | null, now: Date = new Date()): string {
  if (!iso) return '—';
  const diffMs = now.getTime() - new Date(iso).getTime();
  if (Number.isNaN(diffMs) || diffMs < 0) return 'just now';
  const sec = Math.floor(diffMs / 1000);
  if (sec < 5) return 'just now';
  if (sec < 60) return `${sec}s ago`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m ago`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const day = Math.floor(hr / 24);
  return `${day}d ago`;
}
