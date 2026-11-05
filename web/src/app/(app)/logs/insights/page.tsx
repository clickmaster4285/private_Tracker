'use client';

import { Suspense, useMemo } from 'react';
import Link from 'next/link';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { format } from 'date-fns';
import {
  AlertTriangle,
  AppWindow,
  CheckCircle2,
  Globe,
  Info,
  Lightbulb,
  Loader2,
  Timer,
  Users,
  Wifi,
} from 'lucide-react';
import ActivityFilters from '@/components/journey/ActivityFilters';
import EmptyState from '@/components/employees/EmptyState';
import EmployeeSelector from '@/components/EmployeeSelector';
import StatsCard from '@/components/ui/StatsCard';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { useUrlActivityFilter } from '@/hooks/use-url-activity-filter';
import {
  departmentsApi,
  employeesApi,
  logInsightsApi,
  type LogInsightsHighlight,
  type LogInsightsResponse,
} from '@/lib/api';
import { formatSeconds } from '@/lib/format';
import { cn } from '@/lib/utils';

type InsightsExtra = {
  departmentId: string;
  employeeId: string;
};

function severityIcon(severity: LogInsightsHighlight['severity']) {
  if (severity === 'warning') return AlertTriangle;
  if (severity === 'success') return CheckCircle2;
  return Info;
}

function severityClass(severity: LogInsightsHighlight['severity']) {
  if (severity === 'warning') return 'border-warning/30 bg-warning/10 text-warning';
  if (severity === 'success') return 'border-success/30 bg-success/10 text-success';
  return 'border-primary/20 bg-primary/5 text-primary';
}

function InsightsInner() {
  const { filter, setFilter, extra, setExtra } = useUrlActivityFilter<InsightsExtra>(
    { departmentId: {}, employeeId: {} },
    { departmentId: '', employeeId: '' },
  );

  const departmentId = extra.departmentId ? Number(extra.departmentId) : undefined;
  const employeeId = extra.employeeId || undefined;

  const range = useMemo(() => {
    if (filter.preset === 'all' || !filter.dateFrom || !filter.dateTo) {
      return { from: '1970-01-01', to: format(new Date(), 'yyyy-MM-dd') };
    }
    return {
      from: format(new Date(filter.dateFrom), 'yyyy-MM-dd'),
      to: format(new Date(filter.dateTo), 'yyyy-MM-dd'),
    };
  }, [filter.preset, filter.dateFrom, filter.dateTo]);

  const insightsQuery = useQuery({
    queryKey: ['logs-insights', range.from, range.to, departmentId ?? null, employeeId ?? null],
    queryFn: () =>
      logInsightsApi.get({
        from: range.from,
        to: range.to,
        departmentId: departmentId && departmentId > 0 ? departmentId : undefined,
        employeeId,
        topN: 8,
      }),
    placeholderData: keepPreviousData,
    staleTime: 45_000,
  });

  const departmentsQuery = useQuery({
    queryKey: ['logs-insights', 'departments'],
    queryFn: () => departmentsApi.list(),
    staleTime: 10 * 60_000,
  });

  const departments = departmentsQuery.data?.departments ?? [];
  const departmentName =
    departmentId && departmentId > 0
      ? departments.find((d) => d.id === departmentId)?.name
      : undefined;

  const employeesQuery = useQuery({
    queryKey: ['employees', 'selector'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 100 }),
    staleTime: 5 * 60_000,
  });

  const data = insightsQuery.data;
  const employees = employeesQuery.data?.data ?? [];
  const employeeUuidByCode = useMemo(() => {
    const map = new Map<string, string>();
    for (const e of employees) map.set(e.employeeId, e.id);
    return map;
  }, [employees]);

  return (
    <div className="space-y-5 animate-fade-in">
      <header className="rounded-xl border border-border bg-card shadow-card">
        <div className="flex items-start gap-3 px-4 py-4 sm:px-5">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl gradient-primary">
            <Lightbulb className="h-5 w-5 text-primary-foreground" />
          </div>
          <div className="min-w-0">
            <h2 className="font-display text-lg font-bold leading-tight text-foreground">
              Log Insights
            </h2>
            <p className="mt-1 max-w-2xl text-sm leading-relaxed text-muted-foreground">
              Digest of app sessions, web activity, idle signals, and classified productivity for the
              period you select. Rule-based findings — not AI-generated prose.
            </p>
          </div>
        </div>
        <div className="flex flex-col gap-3 border-t border-border px-4 py-3 sm:px-5 lg:flex-row lg:flex-wrap lg:items-center lg:justify-between">
          <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
            <Select
              value={departmentId ? String(departmentId) : 'all'}
              onValueChange={(value) =>
                setExtra({
                  departmentId: value === 'all' ? '' : value,
                  // Clear employee when department changes so filters stay coherent.
                  employeeId: '',
                })
              }
            >
              <SelectTrigger className="h-9 w-full bg-background sm:w-48" aria-label="Department">
                <SelectValue placeholder="All departments" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="all">All departments</SelectItem>
                {departments.map((d) => (
                  <SelectItem key={d.id} value={String(d.id)}>
                    {d.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <EmployeeSelector
              value={employeeId ?? ''}
              allowAll
              department={departmentName}
              className="max-w-none sm:w-64 bg-background"
              onChange={(emp) => setExtra({ employeeId: emp?.employeeId ?? '' })}
            />
          </div>
          <ActivityFilters
            value={filter}
            onChange={setFilter}
            loading={insightsQuery.isFetching}
            availablePresets={['today', 'yesterday', '7d', '30d', 'all']}
            searchShow={false}
          />
        </div>
      </header>

      {insightsQuery.isLoading && !data ? (
        <div className="flex items-center justify-center min-h-[280px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      ) : insightsQuery.isError && !data ? (
        <div className="rounded-xl border border-border bg-card p-8 text-center shadow-card">
          <p className="text-destructive font-medium">Failed to load insights</p>
          <p className="text-sm text-muted-foreground mt-1">
            {(insightsQuery.error as Error).message}
          </p>
          <button
            type="button"
            onClick={() => insightsQuery.refetch()}
            className="mt-3 text-sm text-primary hover:underline"
          >
            Retry
          </button>
        </div>
      ) : data ? (
        <InsightsBody data={data} employeeUuidByCode={employeeUuidByCode} />
      ) : null}
    </div>
  );
}

function InsightsBody({
  data,
  employeeUuidByCode,
}: {
  data: LogInsightsResponse;
  employeeUuidByCode: Map<string, string>;
}) {
  const { metrics: m, productivity: p, highlights, topApps, topDomains, outliers } = data;
  const empty = m.sessions === 0 && m.webPages === 0;

  return (
    <div className="space-y-5">
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-6 gap-4">
        <StatsCard title="Sessions" value={m.sessions} icon={AppWindow} subtitle="In range" delay={0.02} />
        <StatsCard title="Web pages" value={m.webPages} icon={Globe} subtitle="Browser tabs" delay={0.04} />
        <StatsCard
          title="Active employees"
          value={m.activeEmployees}
          icon={Users}
          subtitle="With sessions"
          delay={0.06}
        />
        <StatsCard
          title="Productive"
          value={`${p.productivePct}%`}
          icon={CheckCircle2}
          subtitle={formatSeconds(p.productiveSeconds)}
          delay={0.08}
        />
        <StatsCard
          title="Idle starts"
          value={m.idleEvents}
          icon={Timer}
          subtitle={`${m.employeesWithIdle} employee${m.employeesWithIdle === 1 ? '' : 's'}`}
          delay={0.1}
        />
        <StatsCard
          title="Open now"
          value={m.openSessions}
          icon={Wifi}
          subtitle={m.staleSessions > 0 ? `${m.staleSessions} stale` : 'Active sessions'}
          subtitleColor={m.staleSessions > 0 ? 'text-warning' : undefined}
          delay={0.12}
        />
      </div>

      {!empty && p.totalSeconds > 0 && (
        <div className="rounded-xl border border-border bg-card p-4 shadow-card">
          <div className="flex items-center justify-between gap-3 mb-3">
            <h3 className="font-display text-sm font-semibold text-foreground">Productivity mix</h3>
            <span className="text-xs text-muted-foreground">
              {formatSeconds(p.totalSeconds)} classified session time
            </span>
          </div>
          <div className="h-3 w-full rounded-full bg-muted overflow-hidden flex">
            <div
              className="h-full bg-success transition-all"
              style={{ width: `${p.productivePct}%` }}
              title={`Productive ${p.productivePct}%`}
            />
            <div
              className="h-full bg-destructive/80 transition-all"
              style={{ width: `${p.unproductivePct}%` }}
              title={`Unproductive ${p.unproductivePct}%`}
            />
            <div
              className="h-full bg-muted-foreground/40 transition-all"
              style={{ width: `${p.neutralPct}%` }}
              title={`Neutral ${p.neutralPct}%`}
            />
          </div>
          <div className="mt-2 flex flex-wrap gap-4 text-xs text-muted-foreground">
            <span>
              <span className="inline-block w-2 h-2 rounded-full bg-success mr-1.5 align-middle" />
              Productive {p.productivePct}%
            </span>
            <span>
              <span className="inline-block w-2 h-2 rounded-full bg-destructive/80 mr-1.5 align-middle" />
              Unproductive {p.unproductivePct}%
            </span>
            <span>
              <span className="inline-block w-2 h-2 rounded-full bg-muted-foreground/40 mr-1.5 align-middle" />
              Neutral {p.neutralPct}%
            </span>
          </div>
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <section className="rounded-xl border border-border bg-card p-4 shadow-card">
          <h3 className="font-display text-sm font-semibold text-foreground mb-3">Findings</h3>
          {highlights.length === 0 ? (
            <EmptyState icon={Lightbulb} text="No findings for this filter." />
          ) : (
            <ul className="space-y-2">
              {highlights.map((h, i) => {
                const Icon = severityIcon(h.severity);
                return (
                  <li
                    key={`${h.title}-${i}`}
                    className={cn(
                      'rounded-lg border px-3 py-2.5 flex items-start gap-2.5',
                      severityClass(h.severity),
                    )}
                  >
                    <Icon className="w-4 h-4 mt-0.5 shrink-0" />
                    <div className="min-w-0">
                      <p className="text-sm font-semibold text-foreground">{h.title}</p>
                      <p className="text-xs text-muted-foreground mt-0.5 leading-relaxed">{h.detail}</p>
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </section>

        <section className="rounded-xl border border-border bg-card p-4 shadow-card">
          <h3 className="font-display text-sm font-semibold text-foreground mb-3">
            People to review
          </h3>
          {outliers.length === 0 ? (
            <EmptyState icon={Users} text="No standout employees in this range." />
          ) : (
            <ul className="space-y-2">
              {outliers.map((o) => (
                <li
                  key={`${o.kind}-${o.employeeId}`}
                  className="flex items-center justify-between gap-3 rounded-lg border border-border px-3 py-2.5 hover:bg-muted/30"
                >
                  <div className="min-w-0">
                    <Link
                      href={`/employee-journey/timeline?employeeId=${encodeURIComponent(
                        employeeUuidByCode.get(o.employeeId) ?? o.employeeId,
                      )}`}
                      className="text-sm font-medium text-foreground hover:text-primary truncate block"
                    >
                      {o.employeeName}
                    </Link>
                    <p className="text-xs text-muted-foreground font-mono">{o.employeeId}</p>
                  </div>
                  <span className="shrink-0 text-xs font-semibold rounded-md border border-border px-2 py-1 bg-muted/50 text-foreground">
                    {o.label}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <section className="rounded-xl border border-border bg-card p-4 shadow-card">
          <h3 className="font-display text-sm font-semibold text-foreground mb-3">Top apps</h3>
          {topApps.length === 0 ? (
            <EmptyState icon={AppWindow} text="No app sessions in this range." />
          ) : (
            <ul className="space-y-1.5">
              {topApps.map((a, i) => (
                <li
                  key={`${a.appDisplayName}-${a.processName}-${i}`}
                  className="flex items-center justify-between gap-2 px-2 py-1.5 rounded-lg hover:bg-muted/40"
                >
                  <span className="text-sm text-foreground truncate">
                    <span className="text-muted-foreground mr-2 tabular-nums">{i + 1}.</span>
                    {a.appDisplayName}
                  </span>
                  <span className="text-xs text-muted-foreground shrink-0 tabular-nums">
                    {a.sessionCount} sess{a.openNow > 0 ? ` · ${a.openNow} open` : ''}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="rounded-xl border border-border bg-card p-4 shadow-card">
          <h3 className="font-display text-sm font-semibold text-foreground mb-3">Top domains</h3>
          {topDomains.length === 0 ? (
            <EmptyState icon={Globe} text="No web pages in this range." />
          ) : (
            <ul className="space-y-1.5">
              {topDomains.map((d, i) => (
                <li
                  key={d.domain}
                  className="flex items-center justify-between gap-2 px-2 py-1.5 rounded-lg hover:bg-muted/40"
                >
                  <span className="text-sm text-foreground truncate">
                    <span className="text-muted-foreground mr-2 tabular-nums">{i + 1}.</span>
                    {d.domain}
                  </span>
                  <span className="text-xs text-muted-foreground shrink-0 tabular-nums">
                    {d.visits} visits
                  </span>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </div>
  );
}

export default function UserInsightsPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[400px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <InsightsInner />
    </Suspense>
  );
}
