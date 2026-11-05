'use client';

import { Suspense, useMemo } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { format, subDays } from 'date-fns';
import {
  AppWindow,
  BarChart3,
  Globe,
  Loader2,
  Timer,
} from 'lucide-react';
import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
  Area,
  AreaChart,
} from 'recharts';
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
  logGraphicalApi,
  type LogGraphicalResponse,
} from '@/lib/api';
import { formatSeconds } from '@/lib/format';

type GraphicalExtra = {
  departmentId: string;
  employeeId: string;
};

const tooltipStyle = {
  backgroundColor: 'hsl(var(--card))',
  border: '1px solid hsl(var(--border))',
  borderRadius: '0.5rem',
  fontSize: '12px',
};

function GraphicalInner() {
  const { filter, setFilter, extra, setExtra } = useUrlActivityFilter<GraphicalExtra>(
    { departmentId: {}, employeeId: {} },
    { departmentId: '', employeeId: '' },
  );

  const departmentId = extra.departmentId ? Number(extra.departmentId) : undefined;
  const employeeId = extra.employeeId || undefined;

  // Charts need a bounded window — "all time" maps to last 30 days so the series stays readable.
  const range = useMemo(() => {
    if (filter.preset === 'all' || !filter.dateFrom || !filter.dateTo) {
      const to = new Date();
      return {
        from: format(subDays(to, 29), 'yyyy-MM-dd'),
        to: format(to, 'yyyy-MM-dd'),
      };
    }
    return {
      from: format(new Date(filter.dateFrom), 'yyyy-MM-dd'),
      to: format(new Date(filter.dateTo), 'yyyy-MM-dd'),
    };
  }, [filter.preset, filter.dateFrom, filter.dateTo]);

  const chartQuery = useQuery({
    queryKey: ['logs-graphical', range.from, range.to, departmentId ?? null, employeeId ?? null],
    queryFn: () =>
      logGraphicalApi.get({
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
    queryKey: ['logs-graphical', 'departments'],
    queryFn: () => departmentsApi.list(),
    staleTime: 10 * 60_000,
  });

  const departments = departmentsQuery.data?.departments ?? [];
  const departmentName =
    departmentId && departmentId > 0
      ? departments.find((d) => d.id === departmentId)?.name
      : undefined;

  const data = chartQuery.data;

  return (
    <div className="space-y-5 animate-fade-in">
      <header className="rounded-xl border border-border bg-card shadow-card">
        <div className="flex items-start gap-3 px-4 py-4 sm:px-5">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl gradient-primary">
            <BarChart3 className="h-5 w-5 text-primary-foreground" />
          </div>
          <div className="min-w-0">
            <h2 className="font-display text-lg font-bold leading-tight text-foreground">
              Graphical Logs
            </h2>
            <p className="mt-1 max-w-2xl text-sm leading-relaxed text-muted-foreground">
              Time-series view of sessions, web visits, idle starts, and classified productivity.
              Buckets switch to hourly when the range is 3 days or less.
              {filter.preset === 'all' ? ' “All time” shows the last 30 days for chart clarity.' : ''}
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
            loading={chartQuery.isFetching}
            availablePresets={['today', 'yesterday', '7d', '30d', 'all']}
            searchShow={false}
          />
        </div>
      </header>

      {chartQuery.isLoading && !data ? (
        <div className="flex items-center justify-center min-h-[280px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      ) : chartQuery.isError && !data ? (
        <div className="rounded-xl border border-border bg-card p-8 text-center shadow-card">
          <p className="text-destructive font-medium">Failed to load charts</p>
          <p className="text-sm text-muted-foreground mt-1">
            {(chartQuery.error as Error).message}
          </p>
          <button
            type="button"
            onClick={() => chartQuery.refetch()}
            className="mt-3 text-sm text-primary hover:underline"
          >
            Retry
          </button>
        </div>
      ) : data ? (
        <GraphicalBody data={data} />
      ) : null}
    </div>
  );
}

function GraphicalBody({ data }: { data: LogGraphicalResponse }) {
  const { summary: s, activity, productivity, topApps, topDomains, bucket } = data;
  const empty = s.sessions === 0 && s.webPages === 0 && s.idleEvents === 0;

  const productivityHours = useMemo(
    () =>
      productivity.map((p) => ({
        bucket: p.bucket,
        productive: Math.round((p.productiveSeconds / 3600) * 10) / 10,
        unproductive: Math.round((p.unproductiveSeconds / 3600) * 10) / 10,
        neutral: Math.round((p.neutralSeconds / 3600) * 10) / 10,
      })),
    [productivity],
  );

  const appBars = useMemo(
    () =>
      [...topApps]
        .slice(0, 8)
        .reverse()
        .map((a) => ({
          name:
            a.appDisplayName.length > 22
              ? `${a.appDisplayName.slice(0, 20)}…`
              : a.appDisplayName,
          sessions: a.sessionCount,
        })),
    [topApps],
  );

  const domainBars = useMemo(
    () =>
      [...topDomains]
        .slice(0, 8)
        .reverse()
        .map((d) => ({
          name: d.domain.length > 28 ? `${d.domain.slice(0, 26)}…` : d.domain,
          visits: d.visits,
        })),
    [topDomains],
  );

  if (empty) {
    return (
      <div className="rounded-xl border border-border bg-card py-6 shadow-card">
        <EmptyState
          icon={BarChart3}
          text="No activity in this range to chart. Widen the dates or check that clients are syncing."
        />
      </div>
    );
  }

  return (
    <div className="space-y-5">
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
        <StatsCard title="Sessions" value={s.sessions} icon={AppWindow} subtitle={`By ${bucket}`} delay={0.02} />
        <StatsCard title="Web pages" value={s.webPages} icon={Globe} subtitle="Browser tabs" delay={0.04} />
        <StatsCard title="Idle starts" value={s.idleEvents} icon={Timer} subtitle="session_events" delay={0.06} />
        <StatsCard
          title="Classified time"
          value={formatSeconds(s.totalSeconds)}
          icon={BarChart3}
          subtitle={`${formatSeconds(s.productiveSeconds)} productive`}
          delay={0.08}
        />
      </div>

      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
        <section className="rounded-xl border border-border bg-card p-4 shadow-card">
          <h3 className="font-display text-sm font-semibold text-foreground mb-1">
            Activity over time
          </h3>
          <p className="text-xs text-muted-foreground mb-4">
            Sessions, web pages, and idle starts per {bucket}
          </p>
          <div className="h-[300px]">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={activity}>
                <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                <XAxis dataKey="bucket" stroke="hsl(var(--muted-foreground))" fontSize={11} interval="preserveStartEnd" />
                <YAxis stroke="hsl(var(--muted-foreground))" fontSize={11} allowDecimals={false} />
                <Tooltip contentStyle={tooltipStyle} />
                <Legend />
                <Bar dataKey="sessions" name="Sessions" fill="hsl(var(--primary))" radius={[3, 3, 0, 0]} />
                <Bar dataKey="webPages" name="Web pages" fill="hsl(199, 89%, 48%)" radius={[3, 3, 0, 0]} />
                <Bar dataKey="idleEvents" name="Idle starts" fill="hsl(38, 92%, 55%)" radius={[3, 3, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </section>

        <section className="rounded-xl border border-border bg-card p-4 shadow-card">
          <h3 className="font-display text-sm font-semibold text-foreground mb-1">
            Productivity mix
          </h3>
          <p className="text-xs text-muted-foreground mb-4">
            Classified app time (hours) per {bucket}
          </p>
          <div className="h-[300px]">
            <ResponsiveContainer width="100%" height="100%">
              <AreaChart data={productivityHours}>
                <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                <XAxis dataKey="bucket" stroke="hsl(var(--muted-foreground))" fontSize={11} interval="preserveStartEnd" />
                <YAxis stroke="hsl(var(--muted-foreground))" fontSize={11} />
                <Tooltip contentStyle={tooltipStyle} />
                <Legend />
                <Area
                  type="monotone"
                  dataKey="productive"
                  name="Productive"
                  stackId="1"
                  stroke="#10b981"
                  fill="#10b981"
                  fillOpacity={0.7}
                />
                <Area
                  type="monotone"
                  dataKey="neutral"
                  name="Neutral"
                  stackId="1"
                  stroke="#64748b"
                  fill="#64748b"
                  fillOpacity={0.55}
                />
                <Area
                  type="monotone"
                  dataKey="unproductive"
                  name="Unproductive"
                  stackId="1"
                  stroke="#ef4444"
                  fill="#ef4444"
                  fillOpacity={0.65}
                />
              </AreaChart>
            </ResponsiveContainer>
          </div>
        </section>
      </div>

      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
        <section className="rounded-xl border border-border bg-card p-4 shadow-card">
          <h3 className="font-display text-sm font-semibold text-foreground mb-1">Top apps</h3>
          <p className="text-xs text-muted-foreground mb-4">Session counts in this range</p>
          {appBars.length === 0 ? (
            <EmptyState icon={AppWindow} text="No app sessions to chart." />
          ) : (
            <div className="h-[280px]">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={appBars} layout="vertical" margin={{ left: 8, right: 16 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" horizontal={false} />
                  <XAxis type="number" stroke="hsl(var(--muted-foreground))" fontSize={11} allowDecimals={false} />
                  <YAxis
                    type="category"
                    dataKey="name"
                    width={110}
                    stroke="hsl(var(--muted-foreground))"
                    fontSize={11}
                  />
                  <Tooltip contentStyle={tooltipStyle} />
                  <Bar dataKey="sessions" name="Sessions" fill="hsl(var(--primary))" radius={[0, 3, 3, 0]} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          )}
        </section>

        <section className="rounded-xl border border-border bg-card p-4 shadow-card">
          <h3 className="font-display text-sm font-semibold text-foreground mb-1">Top domains</h3>
          <p className="text-xs text-muted-foreground mb-4">Browser tab visits in this range</p>
          {domainBars.length === 0 ? (
            <EmptyState icon={Globe} text="No web pages to chart." />
          ) : (
            <div className="h-[280px]">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={domainBars} layout="vertical" margin={{ left: 8, right: 16 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" horizontal={false} />
                  <XAxis type="number" stroke="hsl(var(--muted-foreground))" fontSize={11} allowDecimals={false} />
                  <YAxis
                    type="category"
                    dataKey="name"
                    width={120}
                    stroke="hsl(var(--muted-foreground))"
                    fontSize={11}
                  />
                  <Tooltip contentStyle={tooltipStyle} />
                  <Bar dataKey="visits" name="Visits" fill="hsl(199, 89%, 48%)" radius={[0, 3, 3, 0]} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          )}
        </section>
      </div>
    </div>
  );
}

export default function GraphicalLogsPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[400px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <GraphicalInner />
    </Suspense>
  );
}
