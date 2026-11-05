'use client';

import { startOfDay, endOfDay } from 'date-fns';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import type { ActivityFilter, DatePreset } from '@/components/journey/ActivityFilters';
import { presetRange } from '@/components/journey/ActivityFilters';

/**
 * Wire the ActivityFilter value used by `ActivityFilters` (search + date
 * preset + dateFrom/dateTo ISO) to the browser URL.
 *
 * URL schema (compact, readable):
 *   ?q=<search>                (only when search is non-empty)
 *   &preset=<today|yesterday|7d|30d|all|custom>
 *   &from=YYYY-MM-DD&to=YYYY-MM-DD   (only when preset=custom)
 *
 * - Selecting a preset writes its short key (`today`, `7d`, …) so the URL
 *   stays human-readable. The `dateFrom`/`dateTo` ISO strings are then
 *   computed client-side from the preset, never sent over the wire.
 * - Custom ranges persist `from`/`to` as `YYYY-MM-DD` so they survive a
 *   page reload without round-tripping through `new Date()`.
 * - Empty `q` is stripped from the URL — `?q=&preset=today` is not stored.
 *
 * Single-instance guarantee
 * -------------------------
 * Multiple `useUrlQueryState` hooks on the same page each maintain their own
 * diff ref. A sibling hook writing a disjoint key looks like "URL changed" to
 * this hook's effect — the new diff in `use-url-query-state` ignores
 * non-owned keys, but TWO independent hooks still race on writes: whichever
 * flushes second reads a `searchParams` closure captured mid-flight and may
 * briefly drop the other's keys. The only race-free solution is ONE
 * `useUrlQueryState` per page. To support pages that mix the activity filter
 * with sibling URL state (e.g. `/timesheets` also has an `?employeeId=`
 * picker), this hook accepts an `extraSchema` / `extraInitial` pair and
 * merges the two schemas into a single underlying `useUrlQueryState` call.
 *
 * Returns `[filter, setFilter, extra, setExtra]` — `extra`/`setExtra` are
 * the `useState`-shaped tuple for the sibling keys (typed as `T`).
 */
export interface UseUrlActivityFilterResult<T extends Record<string, string>> {
  /** Current activity filter (search + preset + ISO dateFrom/dateTo). */
  filter: ActivityFilter;
  /** Replace the activity filter — writes only the filter's URL keys. */
  setFilter: (next: ActivityFilter) => void;
  /** Current values for any extra schema keys passed in. */
  extra: T;
  /** Patch the extra keys — writes only the extra keys' URL slots. */
  setExtra: (patch: Partial<T> | ((prev: T) => T)) => void;
}

export function useUrlActivityFilter<T extends Record<string, string> = Record<string, never>>(
  extraSchema: Record<keyof T, { parse?: (raw: string) => T[keyof T] }> = {} as Record<keyof T, { parse?: (raw: string) => T[keyof T] }>,
  extraInitial: T = {} as T,
  /**
   * Date preset used when the URL carries no `preset`/`from`/`to`.
   *
   * Defaults to `today` (the activity-filter convention). Pages whose data
   * is sparse and historical — e.g. the DLP alert log, where a single event
   * can be weeks old — pass `all` so the first paint shows the real history
   * instead of an empty "nothing today" list.
   */
  defaultPreset: DatePreset = 'today',
): UseUrlActivityFilterResult<T> {
  // Merge the activity filter schema with the extra schema into ONE
  // underlying `useUrlQueryState`. Same instance → same `lastSerialized` ref
  // → one diff per URL change, zero ping-pong.
  const [merged, setMerged] = useUrlQueryState<Record<string, string>>(
    {
      q: {},
      preset: {},
      from: {},
      to: {},
      ...extraSchema,
    } as Record<string, { parse?: (raw: string) => string }>,
    {
      q: '',
      preset: defaultPreset,
      from: '',
      to: '',
      ...extraInitial,
    } as Record<string, string>,
  );

  // Split the merged record back into the activity filter URL shape and the
  // extra URL shape so callers can use each independently.
  const url = {
    q: merged.q,
    preset: merged.preset,
    from: merged.from,
    to: merged.to,
  };
  const extra = {} as T;
  for (const key of Object.keys(extraSchema) as (keyof T)[]) {
    const v = merged[String(key)];
    extra[key] = (v === undefined ? extraInitial[key] : v) as T[keyof T];
  }

  // Decode URL → ActivityFilter. `from`/`to` are stored as YYYY-MM-DD for
  // `custom` ranges; otherwise they are derived from the preset.
  //
  // Deep-link fallback: when the caller hands us explicit `from`/`to`
  // dates but no `preset` (e.g. the `/attendance` link
  // `?employeeId=<uuid>&from=YYYY-MM-DD&to=YYYY-MM-DD` to `/timesheets`),
  // treat that as a `custom` range — otherwise the default `today` would
  // silently overwrite the caller's dates.
  const hasExplicitRange = Boolean(url.from && url.to);
  const rawPreset = (url.preset || (hasExplicitRange ? 'custom' : defaultPreset)) as DatePreset;
  const preset: DatePreset = rawPreset === 'custom' || hasExplicitRange ? 'custom' : rawPreset;
  const isCustom = preset === 'custom';
  const { dateFrom: presetFrom, dateTo: presetTo } = presetRange(preset);
  const filter: ActivityFilter = {
    search: url.q ?? '',
    preset,
    ...(isCustom && url.from && url.to
      ? {
          dateFrom: startOfDay(new Date(`${url.from}T00:00:00`)).toISOString(),
          dateTo: endOfDay(new Date(`${url.to}T23:59:59`)).toISOString(),
        }
      : { dateFrom: presetFrom, dateTo: presetTo }),
  };

  const setFilter = (next: ActivityFilter) => {
    // Re-encode ActivityFilter → compact URL form, preserving extra keys.
    setMerged(prev => ({
      ...prev,
      q: next.search || '',
      preset: next.preset,
      from: next.preset === 'custom' && next.dateFrom ? isoToYmd(next.dateFrom) : '',
      to: next.preset === 'custom' && next.dateTo ? isoToYmd(next.dateTo) : '',
    }));
  };

  const setExtra = (patch: Partial<T> | ((prev: T) => T)) => {
    setMerged(prev => {
      const base = {} as Record<string, string>;
      for (const key of Object.keys(extraSchema) as (keyof T)[]) {
        const v = prev[String(key)];
        base[String(key)] = v === undefined ? extraInitial[key] : v;
      }
      const nextExtra = typeof patch === 'function' ? (patch as (p: T) => T)(base as T) : { ...base, ...patch };
      const out: Record<string, string> = { ...prev };
      for (const key of Object.keys(extraSchema) as (keyof T)[]) {
        out[String(key)] = nextExtra[key] as string;
      }
      return out;
    });
  };

  return { filter, setFilter, extra, setExtra };
}

function isoToYmd(iso: string): string {
  try {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  } catch {
    return '';
  }
}