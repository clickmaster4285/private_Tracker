'use client';

import { Fragment, Suspense, useEffect, useMemo, useState } from 'react';
import { AppWindow, Timer, Layers, Activity, Loader2, ChevronRight, ChevronDown, CheckCircle2 } from 'lucide-react';
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import ActivityFilters, { type ActivityFilter } from '@/components/journey/ActivityFilters';
import { appSessionsApi, type AppSession, type AppUsageRow } from '@/lib/api';
import { formatDateTime, formatSeconds } from '@/lib/format';
import SessionStatusBadge, { sessionStatus } from '@/components/sessions/SessionStatusBadge';

const SESSIONS_PER_PAGE = 20;

// I-11 (plan 2.5): fold embedded WebView2 host processes into their parent
// browser group so Windows Edge renders as ONE aggregate row instead of
// `msedge` (browser) + `msedgewebview2` (embedded webviews). These are
// structural OS process names, not a product-name list — only the exact
// WebView2 alias is normalised to its parent binary.
function usageProcessName(processName: string): string {
  return processName === 'msedgewebview2' ? 'msedge' : processName;
}

// Merge two aggregate rows that share a normalised `(appDisplayName, processName)`
// key. Duration is already calculated by the server; this only combines the
// two structural process aliases used for embedded WebView2 rows.
function mergeUsage(a: AppUsageRow, b: AppUsageRow): AppUsageRow {
  return {
    ...a,
    sessionCount: a.sessionCount + b.sessionCount,
    totalDurationSeconds: a.totalDurationSeconds + b.totalDurationSeconds,
    firstOpenedAt: a.firstOpenedAt < b.firstOpenedAt ? a.firstOpenedAt : b.firstOpenedAt,
    lastClosedAt: a.lastClosedAt > b.lastClosedAt ? a.lastClosedAt : b.lastClosedAt,
    lastActiveAt: a.lastActiveAt > b.lastActiveAt ? a.lastActiveAt : b.lastActiveAt,
    hasOpenSession: a.hasOpenSession || b.hasOpenSession,
  };
}

export default function EmployeeJourneyApps() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="App Usage"
        subtitle="Time spent per application across the employee's most recent sessions."
        icon={AppWindow}
      >
        {({ employee, filter, setFilter }) => (
          <AppUsageBody employeeId={employee.employeeId} filter={filter} setFilter={setFilter} />
        )}
      </EmployeePage>
    </Suspense>
  );
}

function AppUsageBody({
  employeeId,
  filter,
  setFilter,
}: {
  employeeId: string;
  filter: ActivityFilter;
  setFilter: (next: ActivityFilter) => void;
}) {
  const [isFiltering, setIsFiltering] = useState(false);
  // Expanded rows keyed by `${appDisplayName}|${processName}`. Stored in
  // state (not URL) because the expand/collapse is a per-user UI view,
  // not a shareable filter — the page re-fetches the inner sessions
  // list on first expand, then caches it for the lifetime of the page.
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  // Per-app aggregate (server-side GROUP BY). One query, no fan-out.
  // The page renders `lastClosed - firstOpened` for the Duration cell
  // so multi-tab windows never inflate the per-app total.
  const query = useQuery({
    queryKey: ['app-sessions', 'usage', { employeeId, ...filter }],
    queryFn: () => appSessionsApi.usage({
      employeeId,
      page: 1,
      perPage: 100,
      search: filter.search || undefined,
      dateFrom: filter.dateFrom,
      dateTo: filter.dateTo,
    }),
    placeholderData: keepPreviousData,
  });

  useEffect(() => { setIsFiltering(query.isFetching); }, [query.isFetching]);

  const usage: AppUsageRow[] = useMemo(() => {
    const rows = query.data?.data ?? [];
    const grouped = new Map<string, AppUsageRow>();
    for (const r of rows) {
      const proc = usageProcessName(r.processName);
      const key = `${r.appDisplayName}|${proc}`;
      const merged = { ...r, processName: proc };
      const existing = grouped.get(key);
      grouped.set(key, existing ? mergeUsage(existing, merged) : merged);
    }
    return Array.from(grouped.values())
      .sort((a, b) => b.totalDurationSeconds - a.totalDurationSeconds);
  }, [query.data]);

  const totalDuration = query.data?.totalDurationSeconds ?? 0;
  const totalSessions = query.data?.totalSessionCount ?? 0;
  const runningCount = query.data?.openSessionCount ?? 0;
  const filtered = filter.search !== '' || filter.preset !== 'all';

  const toggle = (key: string) => {
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="h-1.5 w-full bg-gradient-to-r from-primary via-violet-500 to-cyan-500" />
      <ActivityFilters value={filter} onChange={setFilter} loading={isFiltering} singleDay />

      {query.isLoading ? (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : query.isError ? (
        <div className="flex flex-col items-center justify-center py-12 gap-3 text-center">
          <p className="text-sm text-destructive font-medium">Failed to load app usage</p>
          <p className="text-xs text-muted-foreground">{(query.error as Error)?.message || 'Unknown error'}</p>
        </div>
      ) : usage.length === 0 ? (
        <EmptyState
          icon={AppWindow}
          text={filtered ? 'No app usage matches the current filters' : 'No app usage synced yet'}
        />
      ) : (
        <>
      {/* Stat tiles */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        <UsageTile icon={AppWindow} label="Applications" value={usage.length} accent="bg-primary/10 text-primary" />
        <UsageTile icon={Layers} label="Sessions" value={totalSessions} accent="bg-info/15 text-info" />
        <UsageTile icon={Timer} label="Total session time" value={formatSeconds(totalDuration)} accent="bg-success/15 text-success" />
        <UsageTile icon={Activity} label="With open sessions" value={runningCount} accent="bg-warning/15 text-warning" />
      </div>

      <div className="bg-card rounded-xl border border-border shadow-card overflow-x-auto">
        <table className="w-full min-w-[760px]">
          <thead>
            <tr className="border-b border-border">
              {['Application', 'Sessions', 'Duration', 'Last Active', 'Status'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {usage.map(u => {
              const rowKey = `${u.appDisplayName}|${u.processName}`;
              const isOpen = expanded.has(rowKey);
              const expandable = u.sessionCount > 0;
              return (
                <Fragment key={rowKey}>
                  <tr className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                    <td className="px-4 py-3">
                      <div
                        className={`flex items-center gap-2 ${expandable ? 'cursor-pointer select-none' : ''}`}
                        onClick={expandable ? () => toggle(rowKey) : undefined}
                        title={expandable ? (isOpen ? 'Collapse sessions' : 'Expand sessions') : undefined}
                      >
                        {expandable ? (
                          isOpen
                            ? <ChevronDown className="w-4 h-4 text-muted-foreground flex-shrink-0" />
                            : <ChevronRight className="w-4 h-4 text-muted-foreground flex-shrink-0" />
                        ) : (
                          <span className="w-4 flex-shrink-0" />
                        )}
                        <div className="w-8 h-8 rounded-lg bg-primary/10 flex items-center justify-center flex-shrink-0">
                          <AppWindow className="w-4 h-4 text-primary" />
                        </div>
                        <div className="min-w-0">
                          <p className="text-sm font-medium text-foreground truncate">{u.appDisplayName || u.processName}</p>
                          {u.processName && u.processName !== u.appDisplayName && (
                            <p className="text-xs text-muted-foreground font-mono truncate">{u.processName}</p>
                          )}
                        </div>
                      </div>
                    </td>
                    <td className="px-4 py-3 text-sm text-foreground font-medium">{u.sessionCount}</td>
                    <td className="px-4 py-3 text-sm text-success font-medium whitespace-nowrap">{formatSeconds(u.totalDurationSeconds)}</td>
                    <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(u.lastActiveAt)}</td>
                    <td className="px-4 py-3 whitespace-nowrap">
                      <SessionStatusBadge status={u.hasOpenSession ? 'ACTIVE' : 'CLOSED'} />
                    </td>
                  </tr>
                  {isOpen && (
                    <tr className="border-b border-border bg-muted/20">
                      <td colSpan={6} className="px-0 py-0">
                        <ExpandedSessions
                          appDisplayName={u.appDisplayName}
                          processName={u.processName}
                          employeeId={employeeId}
                          filter={filter}
                        />
                      </td>
                    </tr>
                  )}
                </Fragment>
              );
            })}
          </tbody>
        </table>
      </div>
        </>
      )}
    </div>
  );
}

function ExpandedSessions({
  appDisplayName,
  processName,
  employeeId,
  filter,
}: {
  appDisplayName: string;
  processName: string;
  employeeId: string;
  filter: ActivityFilter;
}) {
  // Per-app session list, paginated via useInfiniteQuery (per the
  // Web Infinite-Scroll Rule). Re-keyed on the date filter + the
  // app tuple, so changing the date range or picking a different
  // employee refetches from page 1. The 20/page cap keeps the
  // per-page payload small even for users with 200+ sessions/week.
  const sessionsQuery = useInfiniteQuery({
    queryKey: ['app-sessions', 'usage', 'sessions', {
      appDisplayName, processName, employeeId, ...filter,
    }],
    queryFn: ({ pageParam }) => appSessionsApi.usageSessions({
      appDisplayName,
      processName,
      page: pageParam,
      perPage: SESSIONS_PER_PAGE,
      employeeId,
      dateFrom: filter.dateFrom,
      dateTo: filter.dateTo,
    }),
    initialPageParam: 1,
    getNextPageParam: (last) =>
      last.page < last.totalPages ? last.page + 1 : undefined,
  });

  const sessions: AppSession[] = useMemo(
    () => sessionsQuery.data?.pages.flatMap(p => p.data) ?? [],
    [sessionsQuery.data]
  );
  const totalSessions = sessionsQuery.data?.pages[0]?.total ?? 0;
  const hasNextPage = sessionsQuery.hasNextPage;

  return (
    <div className="px-4 py-3">
      <div className="flex items-center justify-between mb-2">
        <div className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          {totalSessions} session{totalSessions === 1 ? '' : 's'}
          {sessionsQuery.isFetching && (
            <Loader2 className="w-3 h-3 inline ml-2 animate-spin align-middle" />
          )}
        </div>
      </div>
      {sessionsQuery.isLoading ? (
        <div className="flex items-center justify-center py-6">
          <Loader2 className="w-5 h-5 animate-spin text-primary" />
        </div>
      ) : sessionsQuery.isError ? (
        <p className="text-xs text-destructive py-2">
          Failed to load sessions: {(sessionsQuery.error as Error)?.message || 'Unknown error'}
        </p>
      ) : sessions.length === 0 ? (
        <p className="text-xs text-muted-foreground py-2">No sessions for this app in the selected range.</p>
      ) : (
        <table className="w-full">
          <thead>
            <tr className="border-b border-border">
              {['Opened', 'Closed', 'Duration', 'Process', 'Context', 'Foreground', 'Background', 'Status'].map(h => (
                <th key={h} className="text-left px-3 py-2 text-xs font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {sessions.map(s => {
              const dur = sessionDurationSeconds(s);
              const fg = s.foregroundSeconds ?? 0;
              const bg = s.backgroundSeconds ?? 0;
              return (
                <tr key={s.id} className="border-b border-border last:border-0">
                  <td className="px-3 py-2.5 text-sm text-foreground whitespace-nowrap">{formatDateTime(s.startedAt)}</td>
                  <td className="px-3 py-2.5 text-sm text-muted-foreground whitespace-nowrap">
                    {s.endedAt
                      ? formatDateTime(s.endedAt)
                      : s.lastSyncAt && (
                          s.status === 'STALE' ||
                          s.status === 'OFFLINE' ||
                          Date.now() - new Date(s.lastSyncAt).getTime() > 10 * 60 * 1000
                        )
                      ? formatDateTime(s.lastSyncAt)
                      : <span className="text-warning font-medium">Running</span>}
                  </td>
                  <td className="px-3 py-2.5 text-sm text-foreground font-medium whitespace-nowrap">{formatSeconds(dur)}</td>
                  <td className="px-3 py-2.5 text-xs text-muted-foreground font-mono whitespace-nowrap">{s.processName || '—'}</td>
                  <td className="px-3 py-2.5 text-xs text-muted-foreground max-w-xs truncate" title={s.contextLabel ?? ''}>
                    {s.contextLabel || '—'}
                  </td>
                  <td className="px-3 py-2.5 text-sm text-success font-medium whitespace-nowrap">{formatSeconds(fg)}</td>
                  <td className="px-3 py-2.5 text-sm text-muted-foreground font-medium whitespace-nowrap">{formatSeconds(bg)}</td>
                  <td className="px-3 py-2.5 whitespace-nowrap">
                    <SessionStatusBadge status={sessionStatus(s)} />
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      {/* Infinite-scroll sentinel — load more on view, per the rule. */}
      {hasNextPage && (
        <div className="flex justify-center py-2">
          <button
            type="button"
            onClick={() => sessionsQuery.fetchNextPage()}
            disabled={sessionsQuery.isFetchingNextPage}
            className="text-xs text-primary hover:underline disabled:opacity-50"
          >
            {sessionsQuery.isFetchingNextPage ? 'Loading more…' : 'Load more sessions'}
          </button>
        </div>
      )}
      {!hasNextPage && sessions.length > 0 && (
        <div className="text-center text-xs text-muted-foreground py-2">Showing all {totalSessions}</div>
      )}
    </div>
  );
}

/**
 * 3-state-aware duration end (matches sessionDurationSeconds from the
 * Session Timeline page). CLOSED → endedAt, STALE/OFFLINE → lastSyncAt,
 * ACTIVE → now only while lastSyncAt is fresh (≤10 min); otherwise lastSyncAt
 * so a historical "Running" row cannot stretch to wall-clock now.
 */
function sessionDurationSeconds(s: AppSession): number {
  const start = new Date(s.startedAt).getTime();
  const status = s.status ?? (s.endedAt ? 'CLOSED' : 'ACTIVE');
  const lastSync = s.lastSyncAt ? new Date(s.lastSyncAt).getTime() : NaN;
  let end: number;
  if (s.endedAt) {
    end = new Date(s.endedAt).getTime();
  } else if (status === 'STALE' || status === 'OFFLINE') {
    end = Number.isFinite(lastSync) ? lastSync : Date.now();
  } else if (Number.isFinite(lastSync) && Date.now() - lastSync > 10 * 60 * 1000) {
    end = lastSync;
  } else {
    end = Date.now();
  }
  return Math.max(0, (end - start) / 1000);
}

function UsageTile({ icon: Icon, label, value, accent }: {
  icon: React.ElementType; label: string; value: number | string; accent: string;
}) {
  return (
    <div className="bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-shadow">
      <div className={`w-9 h-9 rounded-lg flex items-center justify-center ${accent}`}>
        <Icon className="w-4 h-4" />
      </div>
      <p className="mt-3 text-2xl font-bold text-foreground font-display">{value}</p>
      <p className="text-xs text-muted-foreground mt-0.5">{label}</p>
    </div>
  );
}
