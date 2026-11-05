'use client';

import { useEffect, useMemo, useRef } from 'react';
import { Loader2, MapPin } from 'lucide-react';
import { useInfiniteQuery } from '@tanstack/react-query';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import { locationSamplesApi, type Employee } from '@/lib/api';
import { formatDateTime } from '@/lib/format';

function LocationTrailBody({ employee }: { employee: Employee }) {
  const sentinelRef = useRef<HTMLDivElement>(null);

  const {
    data,
    isLoading,
    isError,
    error,
    fetchNextPage,
    hasNextPage,
    isFetchingNextPage,
  } = useInfiniteQuery({
    queryKey: ['location-samples', employee.employeeId],
    queryFn: ({ pageParam }) =>
      locationSamplesApi.list({
        employeeId: employee.employeeId,
        page: pageParam,
        perPage: 30,
      }),
    initialPageParam: 1,
    getNextPageParam: (last) =>
      last.page < last.totalPages ? last.page + 1 : undefined,
    enabled: Boolean(employee.employeeId),
  });

  const rows = useMemo(
    () => data?.pages.flatMap(p => p.data) ?? [],
    [data],
  );

  const total = data?.pages[0]?.total ?? 0;

  useEffect(() => {
    const el = sentinelRef.current;
    if (!el || !hasNextPage || isFetchingNextPage) return;
    const obs = new IntersectionObserver(
      entries => {
        if (entries[0]?.isIntersecting) fetchNextPage();
      },
      { rootMargin: '300px' },
    );
    obs.observe(el);
    return () => obs.disconnect();
  }, [fetchNextPage, hasNextPage, isFetchingNextPage]);

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16 text-muted-foreground gap-2">
        <Loader2 className="w-5 h-5 animate-spin" />
        Loading location trail…
      </div>
    );
  }

  if (isError) {
    return (
      <EmptyState
        icon={MapPin}
        text={`Failed to load location trail: ${error instanceof Error ? error.message : 'Unknown error'}`}
      />
    );
  }

  if (rows.length === 0) {
    return (
      <EmptyState
        icon={MapPin}
        text="This employee has not shared any location updates yet. They need to grant location permission on their desktop client."
      />
    );
  }

  return (
    <div className="bg-card rounded-xl border border-border overflow-x-auto">
      <table className="w-full min-w-[600px]">
        <thead>
          <tr className="border-b border-border">
            {['Captured', 'Coordinates', 'Source', 'Address'].map(h => (
              <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map(row => (
            <tr key={row.id} className="border-b border-border last:border-0 hover:bg-muted/30">
              <td className="px-4 py-3 text-sm text-muted-foreground">{formatDateTime(row.capturedAt)}</td>
              <td className="px-4 py-3 text-xs font-mono text-foreground">
                {row.latitude.toFixed(5)}, {row.longitude.toFixed(5)}
                {row.accuracyM != null ? ` (±${Math.round(row.accuracyM)}m)` : ''}
              </td>
              <td className="px-4 py-3 text-sm capitalize text-muted-foreground">{row.source}</td>
              <td className="px-4 py-3 text-sm text-foreground">{row.address || '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <div ref={sentinelRef} className="h-1" />
      {isFetchingNextPage && (
        <p className="text-center text-sm text-muted-foreground py-3">Loading more…</p>
      )}
      {!hasNextPage && (
        <p className="text-center text-sm text-muted-foreground py-3">Showing all {total}</p>
      )}
    </div>
  );
}

export default function EmployeeJourneyLocationLive() {
  return (
    <EmployeePage
      title="Location Trail"
      subtitle="Geographic positions reported by the employee's device over time."
      icon={MapPin}
    >
      {({ employee }) => <LocationTrailBody employee={employee} />}
    </EmployeePage>
  );
}
