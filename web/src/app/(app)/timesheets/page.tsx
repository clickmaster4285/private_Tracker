'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import { Clock, ListTree, Loader2 } from 'lucide-react';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import EmployeeSelector from '@/components/EmployeeSelector';
import EmptyState from '@/components/employees/EmptyState';
import ActivityFilters from '@/components/journey/ActivityFilters';
import {
  attendanceApi,
  employeesApi,
  type AttendanceRecord,
  type AttendanceStatus,
  type Employee,
} from '@/lib/api';
import { formatDateTimeInZone, formatSeconds } from '@/lib/format';
import { useUrlActivityFilter } from '@/hooks/use-url-activity-filter';
import { Button } from '@/components/ui/button';
import DayEventsDialog from '@/components/timesheets/DayEventsDialog';

const PER_PAGE = 31;

const STATUS_LABEL: Record<AttendanceStatus, string> = {
  present: 'Present',
  late: 'Late',
  absent: 'Absent',
  half_day: 'Half Day',
  off_shift: 'Off Shift',
  unknown: 'Unknown',
};

const statusColors: Record<AttendanceStatus, string> = {
  present: 'bg-success/15 text-success',
  late: 'bg-warning/15 text-warning',
  absent: 'bg-destructive/15 text-destructive',
  half_day: 'bg-warning/15 text-warning',
  off_shift: 'bg-info/15 text-info',
  unknown: 'bg-muted text-muted-foreground',
};

/**
 * Convert an ISO timestamp (from `ActivityFilter.dateFrom`/`dateTo`) to the
 * `YYYY-MM-DD` form the server's `GET /attendance/range` expects.
 */
function isoToYmd(iso: string | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** Attendance range is computed per day and capped at 366 inclusive days. */
function lastNDaysYmd(daysInclusive: number): { from: string; to: string } {
  const pad = (n: number) => String(n).padStart(2, '0');
  const ymd = (d: Date) =>
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  const to = new Date();
  const from = new Date(to.getFullYear(), to.getMonth(), to.getDate() - (daysInclusive - 1));
  return { from: ymd(from), to: ymd(to) };
}

export default function TimesheetsPage() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <TimesheetsPageInner />
    </Suspense>
  );
}

function TimesheetsPageInner() {
  // Single underlying URL state for BOTH the activity filter and the
  // employee picker — one `useUrlQueryState` instance, one diff ref, no
  // race between sibling hooks writing disjoint URL keys.
  const { filter, setFilter, extra: employeeUrl, setExtra: setEmployeeUrl } =
    useUrlActivityFilter(
      { employeeId: {} },
      { employeeId: '' },
    );

  // One-time default = "Last 14 days" when no preset/from/to is in the URL.
  // Mirrors the prior `daysAgo(13) → today` window without flooding the
  // address bar on first load.
  const [didApplyDefault, setDidApplyDefault] = useState(false);
  useEffect(() => {
    if (didApplyDefault) return;
    const url = new URL(window.location.href);
    const hasPreset = url.searchParams.has('preset');
    const hasFrom = url.searchParams.has('from');
    const hasTo = url.searchParams.has('to');
    if (!hasPreset && !hasFrom && !hasTo) {
      // Default: Last 14 days (today + 13 days back)
      const today = new Date();
      const start = new Date();
      start.setDate(start.getDate() - 13);
      setFilter({
        search: filter.search,
        preset: 'custom',
        dateFrom: new Date(start.getFullYear(), start.getMonth(), start.getDate(), 0, 0, 0).toISOString(),
        dateTo: new Date(today.getFullYear(), today.getMonth(), today.getDate(), 23, 59, 59).toISOString(),
      });
    }
    setDidApplyDefault(true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const [employee, setEmployee] = useState<Employee | null>(null);

  // Server expects YYYY-MM-DD; ActivityFilter stores ISO. Convert at the call
  // site (memoized so the query key only changes when the date really changed).
  const { from, to } = useMemo(() => {
    const start = isoToYmd(filter.dateFrom);
    const end = isoToYmd(filter.dateTo);
    if (filter.preset === 'all' || !start || !end) {
      return lastNDaysYmd(366);
    }
    return { from: start, to: end };
  }, [filter.preset, filter.dateFrom, filter.dateTo]);

  const { data: employeesData } = useQuery({
    queryKey: ['employees', 'selector'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 100 }),
  });

  // Resolve the URL-selected employee UUID → Employee object once the list
  // arrives. The selector's onChange below pushes the new id back into the URL.
  useEffect(() => {
    if (!employeeUrl.employeeId || !employeesData) {
      setEmployee(prev => (prev && prev.id === employeeUrl.employeeId ? prev : null));
      return;
    }
    const match = employeesData.data.find(e => e.id === employeeUrl.employeeId);
    setEmployee(prev => (prev?.id === match?.id ? prev : (match ?? null)));
  }, [employeeUrl.employeeId, employeesData]);

  const handleSelectEmployee = (emp: Employee | null) => {
    setEmployee(emp);
    setEmployeeUrl({ employeeId: emp?.id ?? '' });
  };

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex md:flex-row flex-col justify-between gap-4">
        <div className="flex flex-col gap-4">
          {/* Server-side filters: search + date (preset/custom range). Mounted
          above the table so the search input never loses focus on
          loading/error/empty states. Same component as /employee-journey/apps
          and the other journey pages. */}
          <ActivityFilters
            value={filter}
            onChange={setFilter}
            availablePresets={['today', 'yesterday', '7d', '30d', 'all']}
          />
          {filter.preset === 'all' && (
            <p className="text-xs text-muted-foreground">
              All time is capped at the last 366 days (one row per calendar day).
            </p>
          )}

        </div>

        <div className="flex flex-col xl:flex-row gap-3">
          <EmployeeSelector
            value={employee?.id ?? ''}
            onChange={handleSelectEmployee}
            placeholder="Select employee…"
            className="w-full lg:w-80"
          />
        </div>
      </div>

      
      {!employee ? (
        <EmptyState icon={Clock} text="Select an employee to view their timesheet history." />
      ) : (
        <TimesheetBody
          employee={employee}
          from={from}
          to={to}
          search={filter.search}
        />
      )}
    </div>
  );
}

function TimesheetBody({
  employee,
  from,
  to,
  search,
}: {
  employee: Employee;
  from: string;
  to: string;
  search: string;
}) {
  const [detailRow, setDetailRow] = useState<AttendanceRecord | null>(null);

  const {
    data,
    isLoading,
    isError,
    error,
    isFetchingNextPage,
    fetchNextPage,
    hasNextPage,
  } = useInfiniteQuery({
    queryKey: ['attendance', 'range', employee.employeeId, from, to, search, PER_PAGE],
    queryFn: ({ pageParam }) =>
      attendanceApi.range({
        employeeId: employee.employeeId,
        from,
        to,
        page: pageParam as number,
        perPage: PER_PAGE,
      }),
    initialPageParam: 1,
    getNextPageParam: last => (last.page < last.totalPages ? last.page + 1 : undefined),
  });

  const rows = useMemo(
    () => data?.pages.flatMap(p => p.data) ?? [],
    [data],
  );
  const total = data?.pages[0]?.total ?? 0;

  const totals = useMemo(() => rows.reduce(
    (acc, row) => ({
      active: acc.active + row.activeSeconds,
      idle: acc.idle + row.idleSeconds,
      offShift: acc.offShift + row.offShiftSeconds,
    }),
    { active: 0, idle: 0, offShift: 0 },
  ), [rows]);

  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      entries => {
        if (entries[0]?.isIntersecting && hasNextPage && !isFetchingNextPage) {
          fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16">
        <Loader2 className="w-6 h-6 animate-spin text-primary" />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="text-center py-12">
        <p className="text-destructive font-medium">Failed to load timesheets</p>
        <p className="text-sm text-muted-foreground mt-1">{(error as Error).message}</p>
      </div>
    );
  }

  if (rows.length === 0) {
    return <EmptyState icon={Clock} text="No attendance rows in this date range." />;
  }

  return (
    <>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        {[
          { label: 'Total Active', value: formatSeconds(totals.active), color: 'text-success' },
          { label: 'Total Idle / Locked', value: formatSeconds(totals.idle), color: 'text-muted-foreground' },
          { label: 'Total Off Shift', value: formatSeconds(totals.offShift), color: 'text-info' },
        ].map(tile => (
          <div key={tile.label} className="bg-card rounded-xl border border-border p-4">
            <p className={`text-xl font-display font-bold ${tile.color}`}>{tile.value}</p>
            <p className="text-xs text-muted-foreground mt-1">{tile.label}</p>
          </div>
        ))}
      </div>

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[1080px]">
          <thead>
            <tr className="border-b border-border">
              {['Date', 'Clock In', 'Clock Out', 'Active', 'Idle', 'Off Shift', 'Status', ''].map(h => (
                <th key={h || 'actions'} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row: AttendanceRecord, i) => (
              <motion.tr
                key={`${row.employeeId}-${row.workDate}`}
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                transition={{ delay: i * 0.02 }}
                className="border-b border-border last:border-0 hover:bg-muted/30"
              >
                <td className="px-4 py-3 text-sm text-muted-foreground">{row.workDate}</td>
                <td className="px-4 py-3 text-sm text-foreground">{formatDateTimeInZone(row.firstActiveAt, row.timezone)}</td>
                <td className="px-4 py-3 text-sm text-foreground">{formatDateTimeInZone(row.lastActiveAt, row.timezone)}</td>
                <td className="px-4 py-3 text-sm text-success font-medium">{formatSeconds(row.activeSeconds)}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{formatSeconds(row.idleSeconds)}</td>
                <td className="px-4 py-3 text-sm text-info">{formatSeconds(row.offShiftSeconds)}</td>
                <td className="px-4 py-3">
                  <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${statusColors[row.status]}`}>
                    {STATUS_LABEL[row.status]}
                  </span>
                </td>
                <td className="px-4 py-3">
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    className="h-8"
                    onClick={() => setDetailRow(row)}
                  >
                    <ListTree className="h-3.5 w-3.5" />
                    Events
                  </Button>
                </td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </div>

      {hasNextPage ? (
        <div ref={sentinelRef} className="h-12 flex items-center justify-center text-xs text-muted-foreground">
          {isFetchingNextPage ? (
            <span className="flex items-center gap-2">
              <Loader2 className="w-4 h-4 animate-spin" /> Loading more…
            </span>
          ) : (
            'Scroll for more'
          )}
        </div>
      ) : (
        <p className="text-sm text-muted-foreground text-center">
          Showing all {total.toLocaleString()} day{total === 1 ? '' : 's'} for {employee.name}
        </p>
      )}

      <DayEventsDialog
        open={Boolean(detailRow)}
        onOpenChange={(open) => { if (!open) setDetailRow(null); }}
        employeeName={employee.name}
        row={detailRow}
      />
    </>
  );
}