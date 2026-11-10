'use client';

import { Suspense, useMemo } from 'react';
import { Camera, Loader2 } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import ActivityFilters, { type ActivityFilter } from '@/components/journey/ActivityFilters';
import { ScreenshotGrid } from '@/components/screenshots/ScreenshotGrid';
import { useUrlActivityFilter } from '@/hooks/use-url-activity-filter';
import { employeesApi } from '@/lib/api';

function ScreenshotsInner() {
  const { filter, setFilter, extra, setExtra } = useUrlActivityFilter<{ employeeId: string }>(
    { employeeId: {} },
    { employeeId: '' },
    'today',
  );

  const employeesQuery = useQuery({
    queryKey: ['employees', 'screenshots-picker'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 200 }),
  });

  const range = useMemo(() => dateRangeFromFilter(filter), [filter]);

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

        <ScreenshotGrid
          employeeId={extra.employeeId || undefined}
          from={range.from}
          to={range.to}
        />
      </div>
    </div>
  );
}

function dateRangeFromFilter(filter: ActivityFilter): { from?: string; to?: string } {
  if (filter.preset === 'all') return {};
  if (filter.dateFrom && filter.dateTo) {
    return { from: filter.dateFrom, to: filter.dateTo };
  }
  return {};
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
