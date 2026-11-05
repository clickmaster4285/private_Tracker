'use client';

import { useState } from 'react';
import { getTimeZones } from '@vvo/tzdb';
import { Check, ChevronsUpDown } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from '@/components/ui/command';
import { cn } from '@/lib/utils';

/** IANA zones (UTC included) — shared by Web Blocker schedule + Shifts forms. */
const TIMEZONE_OPTIONS: { value: string; label: string }[] = (() => {
  const formatOffset = (mins: number) => {
    const sign = mins >= 0 ? '+' : '-';
    const abs = Math.abs(mins);
    const h = Math.floor(abs / 60);
    const m = abs % 60;
    return `UTC${sign}${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`;
  };
  const zones = getTimeZones({ includeUtc: true });
  const seen = new Set<string>();
  const rows: { value: string; label: string; sort: number }[] = [];
  for (const z of zones) {
    if (seen.has(z.name)) continue;
    seen.add(z.name);
    const city = z.mainCities?.[0] ? ` — ${z.mainCities[0]}` : '';
    rows.push({
      value: z.name,
      label: `${formatOffset(z.currentTimeOffsetInMinutes)} · ${z.name.replace(/_/g, ' ')}${city}`,
      sort: z.currentTimeOffsetInMinutes,
    });
  }
  if (!seen.has('UTC')) {
    rows.push({ value: 'UTC', label: 'UTC+00:00 · UTC', sort: 0 });
  }
  rows.sort((a, b) => a.sort - b.sort || a.value.localeCompare(b.value));
  return rows.map(({ value, label }) => ({ value, label }));
})();

export function browserTimezone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

export type TimezoneComboboxProps = {
  value: string;
  onChange: (tz: string) => void;
  /** Optional id for the trigger (forms / a11y). */
  id?: string;
  disabled?: boolean;
  className?: string;
  placeholder?: string;
};

/**
 * Searchable IANA timezone picker (`@vvo/tzdb` + cmdk).
 * Use for any schedule/shift wall-clock field that must match server/client TZ math.
 */
export function TimezoneCombobox({
  value,
  onChange,
  id,
  disabled,
  className,
  placeholder = 'Search timezone…',
}: TimezoneComboboxProps) {
  const [open, setOpen] = useState(false);
  const normalized = value.trim() || 'UTC';
  const selected =
    TIMEZONE_OPTIONS.find((z) => z.value === normalized) ??
    (normalized
      ? { value: normalized, label: `${normalized} (saved)` }
      : TIMEZONE_OPTIONS.find((z) => z.value === 'UTC')!);

  return (
    <Popover open={open} onOpenChange={setOpen} modal>
      <PopoverTrigger asChild>
        <Button
          id={id}
          type="button"
          variant="outline"
          role="combobox"
          aria-expanded={open}
          disabled={disabled}
          className={cn('w-full justify-between font-normal h-10 px-3', className)}
        >
          <span className="truncate text-left">{selected.label}</span>
          <ChevronsUpDown className="ml-2 h-4 w-4 shrink-0 opacity-50" />
        </Button>
      </PopoverTrigger>
      <PopoverContent
        className="w-[var(--radix-popover-trigger-width)] p-0 bg-card border-border"
        align="start"
      >
        <Command>
          <CommandInput placeholder={placeholder} />
          <CommandList className="max-h-72">
            <CommandEmpty>No timezone found.</CommandEmpty>
            <CommandGroup>
              {normalized !== '' &&
                !TIMEZONE_OPTIONS.some((z) => z.value === normalized) && (
                  <CommandItem
                    value={`${normalized} saved`}
                    onSelect={() => {
                      onChange(normalized);
                      setOpen(false);
                    }}
                  >
                    <Check
                      className={cn(
                        'mr-2 h-4 w-4',
                        value === selected.value ? 'opacity-100' : 'opacity-0',
                      )}
                    />
                    {normalized} (saved)
                  </CommandItem>
                )}
              {TIMEZONE_OPTIONS.map((z) => (
                <CommandItem
                  key={z.value}
                  value={`${z.value} ${z.label}`}
                  onSelect={() => {
                    onChange(z.value);
                    setOpen(false);
                  }}
                >
                  <Check
                    className={cn(
                      'mr-2 h-4 w-4',
                      normalized === z.value ? 'opacity-100' : 'opacity-0',
                    )}
                  />
                  <span className="truncate">{z.label}</span>
                </CommandItem>
              ))}
            </CommandGroup>
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  );
}
