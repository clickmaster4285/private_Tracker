'use client';

import { Suspense, useEffect, useMemo, useRef } from 'react';
import { Route, Activity, AppWindow, Loader2 } from 'lucide-react';
import { useInfiniteQuery } from '@tanstack/react-query';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import FocusTime from '@/components/journey/FocusTime';
import ActivityFilters, { type ActivityFilter } from '@/components/journey/ActivityFilters';
import { appSessionsApi } from '@/lib/api';
import { formatDateTime, formatDuration, formatRelative } from '@/lib/format';
import SessionStatusBadge, { sessionStatus } from '@/components/sessions/SessionStatusBadge';

const PER_PAGE = 30;

export default function EmployeeJourneyTimeline() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="Session Timeline"
        subtitle="Chronological view of every app session the employee ran."
        icon={Route}
      >
        {({ employee, filter, setFilter }) => (
          <TimelineBody employeeId={employee.employeeId} filter={filter} setFilter={setFilter} />
        )}
      </EmployeePage>
    </Suspense>
  );
}

function TimelineBody({
  employeeId,
  filter,
  setFilter,
}: {
  employeeId: string;
  filter: ActivityFilter;
  setFilter: (next: ActivityFilter) => void;
}) {
  // Server-side pagination with infinite scroll (same pattern as the old
  // employee detail Activity tab).
  const {
    data,
    isLoading,
    isError,
    error,
    isFetchingNextPage,
    fetchNextPage,
    hasNextPage,
  } = useInfiniteQuery({
    // Include every server-side filter in the key so changing a preset starts
    // at page 1 instead of appending results from the previous date range.
    queryKey: ['app-sessions', { employeeId, perPage: PER_PAGE, ...filter }],
    queryFn: ({ pageParam }) => appSessionsApi.list({
      employeeId,
      page: pageParam as number,
      perPage: PER_PAGE,
      search: filter.search || undefined,
      dateFrom: filter.dateFrom,
      dateTo: filter.dateTo,
    }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
  });

  const sessions = useMemo(() => data?.pages.flatMap(p => p.data) ?? [], [data]);
  const total = data?.pages[0]?.total ?? 0;

  // Sentinel that triggers the next page fetch when scrolled into view.
  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && hasNextPage && !isFetchingNextPage) {
          fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  return (
    <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden animate-fade-in">
      <div className="px-4 py-3 border-b border-border flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Activity className="w-4 h-4 text-primary" />
          <h3 className="text-sm font-semibold text-foreground">App Activity</h3>
        </div>
        <span className="text-xs text-muted-foreground">{total.toLocaleString()} session{total === 1 ? '' : 's'}</span>
      </div>
      <div className="px-4 py-3 border-b border-border">
        <ActivityFilters value={filter} onChange={setFilter} loading={isLoading || isFetchingNextPage} />
      </div>
      {isLoading ? (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : isError ? (
        <div className="flex flex-col items-center justify-center py-12 gap-3 text-center">
          <p className="text-sm text-destructive font-medium">Failed to load activity</p>
          <p className="text-xs text-muted-foreground">{(error as Error)?.message || 'Unknown error'}</p>
        </div>
      ) : sessions.length === 0 ? (
        <EmptyState
          icon={Activity}
          text={filter.search || filter.preset !== 'all' ? 'No app activity matches the current filters' : 'No app activity synced yet'}
        />
      ) : <>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[980px]">
          <thead>
            <tr className="border-b border-border">
              {['Application', 'Status', 'Opened', 'Closed', 'Duration', 'Foreground / Background'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {sessions.map(s => {
              const fg = s.foregroundSeconds ?? 0;
              const bg = s.backgroundSeconds ?? 0;
              const status = sessionStatus(s);
              // 4-state duration end (2026-09-02 + OFFLINE 2026-09-02):
              // CLOSED → endedAt, STALE/OFFLINE → lastSyncAt, ACTIVE → now.
              const endIso =
                s.endedAt
                || ((status === 'STALE' || status === 'OFFLINE') && s.lastSyncAt)
                || new Date().toISOString();
              const sinceLabel =
                status === 'OFFLINE' || status === 'STALE'
                  ? formatRelative(s.lastSyncAt)
                  : undefined;
              return (
                <tr key={s.id} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      <div className="w-8 h-8 rounded-lg bg-primary/10 flex items-center justify-center flex-shrink-0">
                        <AppWindow className="w-4 h-4 text-primary" />
                      </div>
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-foreground truncate">{s.appDisplayName || s.processName}</p>
                        <div className="flex items-center gap-1.5">
                          {s.appDisplayName && s.appDisplayName !== s.processName && (
                            <span className="text-xs text-muted-foreground font-mono">{s.processName}</span>
                          )}
                          <span className="px-1.5 py-0.5 rounded text-[10px] font-mono bg-primary/10 text-primary capitalize">{s.platform || '—'}</span>
                          {s.contextLabel && <span className="text-xs text-muted-foreground truncate max-w-[140px]">· {s.contextLabel}</span>}
                        </div>
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    <SessionStatusBadge status={status} staleSinceLabel={sinceLabel} />
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(s.startedAt)}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">
                    {s.endedAt
                      ? formatDateTime(s.endedAt)
                      : (status === 'STALE' || status === 'OFFLINE') && s.lastSyncAt
                      ? formatDateTime(s.lastSyncAt)
                      : '—'}
                  </td>
                  <td className="px-4 py-3 text-sm text-foreground font-medium whitespace-nowrap">
                    {formatDuration(s.startedAt, endIso)}
                  </td>
                  <td className="px-4 py-3">
                    <FocusTime fg={fg} bg={bg} />
                  </td>
                </tr>
              );
            })}
            {isFetchingNextPage && (
              <tr>
                <td colSpan={6} className="px-4 py-4">
                  <div className="flex items-center justify-center gap-2 text-sm text-muted-foreground">
                    <Loader2 className="w-4 h-4 animate-spin" /> Loading more…
                  </div>
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      {hasNextPage && (
        <div ref={sentinelRef} className="h-12 flex items-center justify-center text-xs text-muted-foreground">
          Scroll for more
        </div>
      )}
      </>}
    </div>
  );
}
