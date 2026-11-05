'use client';

import { useEffect, useRef, useState } from 'react';
import { Search, CalendarRange, Loader2 } from 'lucide-react';
import { Calendar } from '@/components/ui/calendar';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { endOfDay, format, startOfDay, subDays } from 'date-fns';

export type DatePreset = 'today' | 'yesterday' | '7d' | '30d' | 'all' | 'custom';

export interface ActivityFilter {
  /** Debounced search text — sent to the server as `search`. */
  search: string;
  /** Inclusive start (ISO with local offset) — sent as `dateFrom`. */
  dateFrom?: string;
  /** Inclusive end (ISO with local offset) — sent as `dateTo`. */
  dateTo?: string;
  preset: DatePreset;
}

const PRESETS: { key: DatePreset; label: string }[] = [
  { key: 'today', label: 'Today' },
  { key: 'yesterday', label: 'Yesterday' },
  { key: '7d', label: 'Last 7 days' },
  { key: '30d', label: 'Last 30 days' },
  { key: 'all', label: 'All time' },
];

/**
 * Default filter = TODAY with the actual local-day bounds computed at mount time.
 * (The preset name alone is not enough — without dateFrom/dateTo the server gets
 * NO date filter and returns every session, leaking yesterday's data into "Today".)
 */
export function createDefaultFilter(): ActivityFilter {
  const { dateFrom, dateTo } = presetRange('today');
  return { search: '', preset: 'today', dateFrom, dateTo };
}

export const DEFAULT_FILTER: ActivityFilter = createDefaultFilter();

export function presetRange(preset: DatePreset): { dateFrom?: string; dateTo?: string } {
  switch (preset) {
    case 'today':
      return { dateFrom: startOfDay(new Date()).toISOString(), dateTo: endOfDay(new Date()).toISOString() };
    case 'yesterday': {
      const y = subDays(new Date(), 1);
      return { dateFrom: startOfDay(y).toISOString(), dateTo: endOfDay(y).toISOString() };
    }
    case '7d':
      return { dateFrom: startOfDay(subDays(new Date(), 6)).toISOString(), dateTo: endOfDay(new Date()).toISOString() };
    case '30d':
      return { dateFrom: startOfDay(subDays(new Date(), 29)).toISOString(), dateTo: endOfDay(new Date()).toISOString() };
    case 'all':
    case 'custom':
      return {};
  }
}

function presetLabel(preset: DatePreset): string {
  switch (preset) {
    case 'today': return 'Today';
    case 'yesterday': return 'Yesterday';
    case '7d': return 'Last 7 days';
    case '30d': return 'Last 30 days';
    case 'all': return 'All time';
    case 'custom': return 'Custom';
  }
}

interface ActivityFiltersProps {
  value: ActivityFilter;
  onChange: (filter: ActivityFilter) => void;
  loading?: boolean;
  /**
   * Restrict the visible preset chips. The custom-range popover is always
   * available (drives `from`/`to` for `preset=custom`). Pages whose server
   * endpoint accepts a single date (`/attendance/day`) pass
   * `['today', 'yesterday']` so users can only pick one day or an explicit
   * custom day — multi-day ranges are still encodable in the URL via
   * `?from=&to=` (so deep-links from other pages still work) but cannot be
   * picked from the popover UI.
   *
   * When omitted, all five presets are shown.
   */
  availablePresets?: DatePreset[];
  /**
   * Lock the custom-range popover to single-day selection. The Calendar is
   * rendered in `single` mode and `applyCustomRange` sets `dateFrom = dateTo`.
   * Used by `/attendance` so a one-row-per-day log cannot accidentally show
   * "Monday's row" for a Tuesday–Wednesday range.
   */
  singleDay?: boolean;
  /**
   * Show the search input.
   */
  searchShow?: boolean;
  /**
   * Placeholder text for the search input. Defaults to the generic
   * "Search apps, pages, URLs…" copy — pages with a different domain
   * (e.g. DLP alerts) pass their own.
   */
  searchPlaceholder?: string;
}

export default function ActivityFilters({ value, onChange, loading, availablePresets, singleDay, searchShow = true, searchPlaceholder = 'Search apps, pages, URLs…' }: ActivityFiltersProps) {
  const [searchInput, setSearchInput] = useState(value.search);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Debounce search: only propagate to the server after 300ms of no typing.
  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      if (searchInput !== value.search) onChange({ ...value, search: searchInput });
    }, 300);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchInput]);

  // Sync the local input box if the filter is reset from the URL (e.g. the
  // user edits ?search=… in the address bar).
  useEffect(() => {
    setSearchInput(value.search);
  }, [value.search]);

  const applyPreset = (preset: DatePreset) => {
    const { dateFrom, dateTo } = presetRange(preset);
    onChange({ ...value, preset, dateFrom, dateTo });
  };

  const applyCustomRange = (range: { from?: Date; to?: Date } | Date | undefined) => {
    // Range mode: { from, to } (both required).
    // Single mode: a single Date — treat from === to so the URL state
    // stays symmetric and `/attendance` shows one row for the picked day.
    const from = range instanceof Date ? range : range?.from;
    if (!from) return;
    const to = range instanceof Date ? range : range?.to ?? from;
    onChange({
      ...value,
      preset: 'custom',
      dateFrom: startOfDay(from).toISOString(),
      dateTo: endOfDay(to).toISOString(),
    });
  };

  // No "Clear" button per the AGENTS.md / prompt.md URL-state rule — the
  // search field, preset chips, and the date popover are the only ways to
  // change the filter. Clearing is done by selecting "All time" / an empty
  // search / popping the date picker off (which leaves the filter in place
  // until the user actively changes it). Hiding a destructive button stops
  // a half-typed clear from accidentally wiping state during a deep link.

  const visiblePresets = (availablePresets ?? PRESETS.map(p => p.key)).filter(
    key => PRESETS.some(p => p.key === key),
  );

  const rangeLabel = value.preset === 'custom' && value.dateFrom && value.dateTo
    ? singleDay
      ? format(new Date(value.dateFrom), 'MMM d, yyyy')
      : `${format(new Date(value.dateFrom), 'MMM d')} – ${format(new Date(value.dateTo), 'MMM d')}`
    : presetLabel(value.preset);

  return (
    <div className="flex flex-wrap items-center gap-2">
      {/* Search */}
      {searchShow && (
      <div className="relative flex items-center">
        
        <Search className="w-4 h-4 text-muted-foreground absolute left-3" />
        <input
          value={searchInput}
          onChange={e => setSearchInput(e.target.value)}
          placeholder={searchPlaceholder}
          className="bg-card border border-border rounded-lg pl-9 pr-9 py-2 text-sm outline-none focus:ring-2 focus:ring-primary/30 transition-shadow w-56 placeholder:text-muted-foreground"
        />
        {/* No clear button — see AGENTS.md URL-State Rule. The only signal is
            the loading spinner when a query is in flight. */}
        {loading && <Loader2 className="w-3.5 h-3.5 animate-spin text-muted-foreground absolute right-3" />}
      </div>
)}
      {/* Date presets */}
      <div className="flex items-center gap-1 flex-wrap">
        {visiblePresets.map(key => {
          const p = PRESETS.find(pp => pp.key === key)!;
          return (
            <button
              key={p.key}
              onClick={() => applyPreset(p.key)}
              className={`px-2.5 py-1.5 rounded-lg text-xs font-medium transition-colors whitespace-nowrap ${
                value.preset === p.key
                  ? 'bg-primary text-primary-foreground shadow-card-hover'
                  : 'text-muted-foreground hover:text-foreground hover:bg-muted'
              }`}
            >
              {p.label}
            </button>
          );
        })}
      </div>

      {/* Custom date range (single day when the page is single-day-scoped) */}
      <Popover>
        <PopoverTrigger asChild>
          <button
            className={`inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg text-xs font-medium transition-colors border whitespace-nowrap ${
              value.preset === 'custom'
                ? 'border-primary text-primary bg-primary/10'
                : 'border-border text-muted-foreground hover:text-foreground hover:bg-muted'
            }`}
          >
            <CalendarRange className="w-3.5 h-3.5" />
            {rangeLabel}
          </button>
        </PopoverTrigger>
        <PopoverContent className="w-auto p-0 bg-card border-border" align="start">
          {singleDay ? (
            <Calendar
              mode="single"
              selected={value.dateFrom ? new Date(value.dateFrom) : undefined}
              onSelect={applyCustomRange}
              initialFocus
            />
          ) : (
            <Calendar
              mode="range"
              selected={{
                from: value.dateFrom ? new Date(value.dateFrom) : undefined,
                to: value.dateTo ? new Date(value.dateTo) : undefined,
              }}
              onSelect={applyCustomRange}
              numberOfMonths={2}
            />
          )}
        </PopoverContent>
      </Popover>
    </div>
  );
}