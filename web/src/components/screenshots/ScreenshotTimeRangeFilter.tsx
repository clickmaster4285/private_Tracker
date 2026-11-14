'use client';

import { Clock, X } from 'lucide-react';
import { Input } from '@/components/ui/input';

/** Compact HH:mm — empty string means "not selected". */
const HHMM = /^([01]\d|2[0-3]):[0-5]\d$/;

export function parseTimeParam(raw: string): string {
  const v = (raw || '').trim();
  return HHMM.test(v) ? v : '';
}

/** Browser IANA zone for server-side clock filters. */
export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

/** YYYY-MM-DD from an ActivityFilter ISO bound (local calendar day). */
export function isoToYmd(iso: string | undefined): string | undefined {
  if (!iso) return undefined;
  try {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return undefined;
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  } catch {
    return undefined;
  }
}

export function ScreenshotTimeRangeFilter({
  timeFrom,
  timeTo,
  onChange,
}: {
  timeFrom: string;
  timeTo: string;
  onChange: (next: { timeFrom: string; timeTo: string }) => void;
}) {
  const active = Boolean(timeFrom || timeTo);

  return (
    <div className="flex flex-wrap items-end gap-2">
      <div className="flex items-center gap-1.5 text-xs text-muted-foreground pb-2">
        <Clock className="w-3.5 h-3.5" />
        <span>Time range</span>
        {!active && <span className="text-muted-foreground/70">(optional)</span>}
      </div>
      <div className="flex flex-col gap-0.5">
        <label className="text-[10px] text-muted-foreground">From</label>
        <Input
          type="time"
          value={timeFrom}
          onChange={(e) => onChange({ timeFrom: e.target.value, timeTo })}
          className="h-9 w-[8.5rem]"
          aria-label="Screenshot time from"
        />
      </div>
      <div className="flex flex-col gap-0.5">
        <label className="text-[10px] text-muted-foreground">To</label>
        <Input
          type="time"
          value={timeTo}
          onChange={(e) => onChange({ timeFrom, timeTo: e.target.value })}
          className="h-9 w-[8.5rem]"
          aria-label="Screenshot time to"
        />
      </div>
      {active && (
        <button
          type="button"
          onClick={() => onChange({ timeFrom: '', timeTo: '' })}
          className="h-9 inline-flex items-center gap-1 rounded-md border border-border px-2 text-xs text-muted-foreground hover:bg-muted"
          title="Clear time range"
        >
          <X className="w-3.5 h-3.5" />
          Clear
        </button>
      )}
    </div>
  );
}
