'use client';

import { Suspense } from 'react';
import { Camera, Loader2 } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import ActivityFilters from '@/components/journey/ActivityFilters';
import { ScreenshotGrid } from '@/components/screenshots/ScreenshotGrid';
import {
  ScreenshotTimeRangeFilter,
  browserTimeZone,
  isoToYmd,
  parseTimeParam,
} from '@/components/screenshots/ScreenshotTimeRangeFilter';
import { useUrlActivityFilter } from '@/hooks/use-url-activity-filter';
import { employeesApi } from '@/lib/api';

function ScreenshotsInner() {
  const { filter, setFilter, extra, setExtra } = useUrlActivityFilter<{
    employeeId: string;
    timeFrom: string;
    timeTo: string;
  }>(
    {
      employeeId: {},
      timeFrom: { parse: parseTimeParam },
      timeTo: { parse: parseTimeParam },
    },
    { employeeId: '', timeFrom: '', timeTo: '' },
    'today',
  );

  const employeesQuery = useQuery({
    queryKey: ['employees', 'screenshots-picker'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 200 }),
  });

  // Pass filter params through only — server applies date + clock filters.
  const from = filter.preset === 'all' ? undefined : isoToYmd(filter.dateFrom);
  const to = filter.preset === 'all' ? undefined : isoToYmd(filter.dateTo);
  const timeZone = browserTimeZone();

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="bg-card rounded-xl border border-border p-5 space-y-4">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex items-center gap-2">
            <Camera className="w-5 h-5 text-primary" />
            <div>
              <h3 className="font-display font-semibold text-foreground">Screenshots</h3>
              <p className="text-xs text-muted-foreground">
                Lazy-loaded grid — images fetch only as you scroll (not all at once).
              </p>
            </div>
          </div>
          <select
            value={extra.employeeId}
            onChange={(e) => setExtra({ employeeId: e.target.value })}
            className="h-9 rounded-md border border-border bg-background px-2 text-sm min-w-[12rem]"
          >
            <option value="">All employees</option>
            {(employeesQuery.data?.data ?? []).map((e) => (
              <option key={e.employeeId} value={e.employeeId}>
                {e.name} ({e.employeeId})
              </option>
            ))}
          </select>
        </div>

        <ActivityFilters value={filter} onChange={setFilter} searchShow={false} />

        <ScreenshotTimeRangeFilter
          timeFrom={extra.timeFrom}
          timeTo={extra.timeTo}
          onChange={(next) => setExtra(next)}
        />

        <ScreenshotGrid
          employeeId={extra.employeeId || undefined}
          from={from}
          to={to}
          timeFrom={extra.timeFrom || undefined}
          timeTo={extra.timeTo || undefined}
          timeZone={timeZone}
        />
      </div>
    </div>
  );
}

export default function ScreenshotsPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[400px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <ScreenshotsInner />
    </Suspense>
  );
}
