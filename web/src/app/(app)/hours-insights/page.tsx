'use client';

import { Suspense, useEffect, useMemo, useState } from 'react';
import {
  AppWindow, Globe, Timer, Activity, Loader2,
  Monitor, ShieldCheck, Layers, Sparkles,
  BarChart3, CheckCircle2, AlertCircle, HelpCircle, PieChart as PieChartIcon,
  Radar, LineChart as LineChartIcon,
} from 'lucide-react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import {
  BarChart, Bar, XAxis, YAxis, CartesianGrid, ResponsiveContainer,
  Legend, Tooltip, PieChart, Pie, Cell, LineChart, Line, LabelList,
} from 'recharts';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import ActivityFilters, { type ActivityFilter } from '@/components/journey/ActivityFilters';
import { hoursInsightsApi } from '@/lib/api';
import { formatSeconds } from '@/lib/format';
import { ChartContainer } from '@/components/ui/chart';
import { cn } from '@/lib/utils';

const PRODUCTIVE_COLOR = '#10b981';
const UNPRODUCTIVE_COLOR = '#ef4444';
const NEUTRAL_COLOR = '#64748b';

const productivityChartConfig = {
  productive: { label: 'Productive', color: PRODUCTIVE_COLOR },
  neutral: { label: 'Neutral', color: NEUTRAL_COLOR },
  unproductive: { label: 'Unproductive', color: UNPRODUCTIVE_COLOR },
};

export default function HoursInsightsPage() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="Hours Insights"
        subtitle="Track and compare employee time across productivity categories and individual applications."
        icon={Clock}
        bodySchema={{
          view: { parse: (raw: string) => (raw === 'applications' ? 'applications' : 'productivity') },
          chart: { parse: (raw: string) => (['histogram', 'pie', 'nightingale', 'line'].includes(raw) ? raw : 'histogram') },
        }}
        bodyInitial={{ view: 'applications', chart: 'histogram' }}
      >
        {({ employee, filter, setFilter, body, setBody }) => (
          <HoursInsightsBody
            employeeId={employee.employeeId}
            filter={filter}
            setFilter={setFilter}
            viewMode={(body.view as 'productivity' | 'applications') || 'productivity'}
            setViewMode={(next) => setBody({ view: next })}
            chartType={(body.chart as ChartType) || 'histogram'}
            setChartType={(next) => setBody({ chart: next })}
          />
        )}
      </EmployeePage>
    </Suspense>
  );
}

function HoursInsightsBody({
  employeeId,
  filter,
  setFilter,
  viewMode,
  setViewMode,
  chartType,
  setChartType,
}: {
  employeeId: string;
  filter: ActivityFilter;
  setFilter: (next: ActivityFilter) => void;
  viewMode: 'productivity' | 'applications';
  setViewMode: (next: 'productivity' | 'applications') => void;
  chartType: ChartType;
  setChartType: (next: ChartType) => void;
}) {
  const [isFiltering, setIsFiltering] = useState(false);

  const query = useQuery({
    queryKey: ['hours-insights', { employeeId, ...filter }],
    queryFn: () => hoursInsightsApi.get({
      employeeId,
      dateFrom: filter.dateFrom,
      dateTo: filter.dateTo,
      preset: filter.preset,
    }),
    placeholderData: keepPreviousData,
  });

  useEffect(() => { setIsFiltering(query.isFetching); }, [query.isFetching]);

  const data = query.data;
  const summary = data?.summary;
  const productivityChart = data?.chart ?? [];
  const appChartRaw = data?.appChart ?? [];
  const topApps = data?.topApps ?? [];
  const topItems = data?.topItems ?? [];

  const totalDuration = summary?.totalSeconds ?? 0;
  const productiveSec = summary?.productiveSeconds ?? 0;
  const unproductiveSec = summary?.unproductiveSeconds ?? 0;
  const neutralSec = summary?.neutralSeconds ?? 0;
  const appCount = summary?.appCount ?? 0;
  const filtered = filter.search !== '' || filter.preset !== 'all';

  // Individual application chart config & flattened rows
  const { appChartConfig, appChartData, appKeysSorted } = useMemo(() => {
    const cfg: Record<string, { label: string; color: string }> = {};

    // Keys in ascending order of usage so that when Recharts stacks them,
    // the highest usage app is on TOP of the stack.
    const reversedApps = [...topApps].reverse();
    const keys: string[] = [];

    // Add 'Other' first if present
    cfg['Other'] = { label: 'Other', color: '#94a3b8' };
    keys.push('Other');

    reversedApps.forEach(a => {
      cfg[a.name] = { label: a.name, color: a.color };
      keys.push(a.name);
    });

    const chartData = appChartRaw.map(b => {
      const row: Record<string, string | number> = { bucket: b.bucket };
      keys.forEach(k => {
        row[k] = b.apps[k] || 0;
      });
      return row;
    });

    return {
      appChartConfig: cfg,
      appChartData: chartData,
      appKeysSorted: keys,
    };
  }, [topApps, appChartRaw]);

  // Calculations for stat percentages
  const prodPct = totalDuration > 0 ? Math.round((productiveSec / totalDuration) * 100) : 0;
  const neutPct = totalDuration > 0 ? Math.round((neutralSec / totalDuration) * 100) : 0;
  const unprodPct = totalDuration > 0 ? Math.round((unproductiveSec / totalDuration) * 100) : 0;

  const topAppName = topApps[0]?.name || 'None';
  const topAppDuration = topApps[0]?.totalSeconds ? formatSeconds(topApps[0].totalSeconds) : '0s';
  const totalSessions = topApps.reduce((acc, a) => acc + a.sessionCount, 0);

  return (
    <div className="space-y-4">
      {/* Top Filter Bar: Activity Filters + View Mode Segmented Control */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 bg-card p-3 rounded-xl border border-border shadow-card">
        <div className="flex-1">
           <ActivityFilters value={filter} onChange={setFilter} loading={isFiltering} singleDay />
        </div>

        {/* View Mode Toggle */}
        <div className="flex items-center gap-1 self-start sm:self-center p-1 bg-muted/70 rounded-xl border border-border/80 flex-shrink-0">
           <button
            type="button"
            onClick={() => setViewMode('applications')}
            className={cn(
              "flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-semibold transition-all duration-200",
              viewMode === 'applications'
                ? "bg-card text-foreground shadow-sm border border-border/60"
                : "text-muted-foreground hover:text-foreground"
            )}
          >
            <Layers className="w-3.5 h-3.5 text-blue-500" />
            <span>From Application Individually</span>
          </button>
          <button
            type="button"
            onClick={() => setViewMode('productivity')}
            className={cn(
              "flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-semibold transition-all duration-200",
              viewMode === 'productivity'
                ? "bg-card text-foreground shadow-sm border border-border/60"
                : "text-muted-foreground hover:text-foreground"
            )}
          >
            <ShieldCheck className="w-3.5 h-3.5 text-emerald-500" />
            <span>From Productivity</span>
          </button>
         
        </div>
      </div>

      {query.isLoading ? (
        <div className="flex items-center justify-center py-16">
          <Loader2 className="w-7 h-7 animate-spin text-primary" />
        </div>
      ) : query.isError ? (
        <div className="flex flex-col items-center justify-center py-16 gap-3 text-center bg-card rounded-xl border border-border p-6 shadow-card">
          <AlertCircle className="w-8 h-8 text-destructive" />
          <p className="text-sm text-destructive font-semibold">Failed to load hours insights</p>
          <p className="text-xs text-muted-foreground max-w-md">{(query.error as Error)?.message || 'Unknown error'}</p>
        </div>
      ) : !data || totalDuration === 0 ? (
        <EmptyState
          icon={Monitor}
          text={filtered ? 'No usage matches the current filters' : 'No usage data synced yet for this period'}
        />
      ) : (
        <>
          {/* Stat Tiles */}
          {viewMode === 'productivity' ? (
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
              <UsageTile
                icon={Timer}
                label="Total Active Time"
                value={formatSeconds(totalDuration)}
                badge={`${appCount} apps`}
                accent="bg-primary/10 text-primary"
              />
              <UsageTile
                icon={CheckCircle2}
                label="Productive Time"
                value={formatSeconds(productiveSec)}
                badge={`${prodPct}% of total`}
                accent="bg-emerald-500/15 text-emerald-600 dark:text-emerald-400"
              />
              <UsageTile
                icon={HelpCircle}
                label="Neutral Time"
                value={formatSeconds(neutralSec)}
                badge={`${neutPct}% of total`}
                accent="bg-slate-500/15 text-slate-600 dark:text-slate-400"
              />
              <UsageTile
                icon={AlertCircle}
                label="Unproductive Time"
                value={formatSeconds(unproductiveSec)}
                badge={`${unprodPct}% of total`}
                accent="bg-rose-500/15 text-rose-600 dark:text-rose-400"
              />
            </div>
          ) : (
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
              <UsageTile
                icon={Timer}
                label="Total App Time"
                value={formatSeconds(totalDuration)}
                accent="bg-primary/10 text-primary"
              />
              <UsageTile
                icon={AppWindow}
                label="Applications Used"
                value={topApps.length}
                accent="bg-info/15 text-info"
              />
              <UsageTile
                icon={Sparkles}
                label="Top Application"
                value={topAppName}
                badge={topAppDuration}
                accent="bg-amber-500/15 text-amber-600 dark:text-amber-400"
              />
              <UsageTile
                icon={Activity}
                label="Total Sessions"
                value={totalSessions}
                accent="bg-violet-500/15 text-violet-600 dark:text-violet-400"
              />
            </div>
          )}

          {/* Chart Section */}
          <div className="bg-card rounded-xl border border-border shadow-card p-5">
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 mb-4">
              <div>
                <h4 className="text-sm font-semibold text-foreground flex items-center gap-2">
                  {viewMode === 'productivity' ? (
                    <>
                      <ShieldCheck className="w-4 h-4 text-emerald-500" />
                      <span>Productivity Time Distribution</span>
                    </>
                  ) : (
                    <>
                      <BarChart3 className="w-4 h-4 text-blue-500" />
                      <span>Application Usage Time</span>
                    </>
                  )}
                </h4>
                <p className="text-xs text-muted-foreground mt-0.5">
                  {viewMode === 'productivity'
                    ? 'Stacked view showing productive, neutral, and unproductive hours'
                    : 'Total time by application for the selected range'
                  }
                </p>
              </div>

            </div>
            <ChartSelector value={chartType} onChange={setChartType} />
            <HoursChart
              chartType={chartType}
              viewMode={viewMode}
              productivityData={productivityChart}
              appData={appChartData}
              appTotals={topApps}
              appKeys={appKeysSorted}
              appConfig={appChartConfig}
              totalDuration={totalDuration}
              productivityTotals={{
                productive: productiveSec,
                neutral: neutralSec,
                unproductive: unproductiveSec,
              }}
            />
          </div>

          {/* Breakdown Section */}
          {viewMode === 'productivity' ? (
            <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden">
              <div className="p-4 border-b border-border">
                <h4 className="text-sm font-semibold text-foreground">Top Applications & Websites by Duration</h4>
                <p className="text-xs text-muted-foreground mt-0.5">Ranked by duration spent during the selected range</p>
              </div>
              <div className="overflow-x-auto">
                <table className="w-full min-w-[640px]">
                  <thead>
                    <tr className="border-b border-border bg-muted/20">
                      {['App / Website', 'Category', 'Category Type', 'Duration', 'Focus Score'].map(h => (
                        <th key={h} className="text-left px-4 py-3 text-xs font-semibold text-muted-foreground uppercase tracking-wider whitespace-nowrap">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {topItems.map((item, idx) => {
                      const typeBg = item.type === 'Productive'
                        ? 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 border-emerald-500/20'
                        : item.type === 'Unproductive'
                        ? 'bg-rose-500/10 text-rose-600 dark:text-rose-400 border-rose-500/20'
                        : 'bg-slate-500/10 text-slate-600 dark:text-slate-400 border-slate-500/20';

                      return (
                        <tr key={`${item.kind}-${item.name}-${idx}`} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                          <td className="px-4 py-3">
                            <div className="flex items-center gap-2.5">
                              <div className="w-8 h-8 rounded-lg bg-primary/10 flex items-center justify-center flex-shrink-0">
                                {item.kind === 'app'
                                  ? <AppWindow className="w-4 h-4 text-primary" />
                                  : <Globe className="w-4 h-4 text-primary" />
                                }
                              </div>
                              <div className="min-w-0">
                                <p className="text-sm font-medium text-foreground truncate">{item.name}</p>
                                {item.isBrowser && (
                                  <p className="text-[11px] text-muted-foreground font-mono truncate">Browser</p>
                                )}
                              </div>
                            </div>
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap">
                            <span className="px-2 py-0.5 rounded-md text-xs font-medium bg-muted text-muted-foreground border border-border/60">
                              {item.category || '-'}
                            </span>
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap">
                            <span className={cn("px-2 py-0.5 rounded-md text-xs font-medium border", typeBg)}>
                              {item.type}
                            </span>
                          </td>
                          <td className="px-4 py-3 text-sm text-foreground font-medium whitespace-nowrap font-mono">{formatSeconds(item.totalSeconds)}</td>
                          <td className="px-4 py-3 text-sm whitespace-nowrap">
                            <span className={`font-semibold ${item.focusScore >= 70 ? 'text-emerald-500' : item.focusScore >= 40 ? 'text-amber-500' : 'text-rose-500'}`}>
                              {item.focusScore.toFixed(1)}%
                            </span>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </div>
          ) : (
            <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden">
              <div className="p-4 border-b border-border">
                <h4 className="text-sm font-semibold text-foreground">Individual Applications Usage Breakdown</h4>
                <p className="text-xs text-muted-foreground mt-0.5">Most active applications sorted by total usage time</p>
              </div>
              <div className="overflow-x-auto">
                <table className="w-full min-w-[680px]">
                  <thead>
                    <tr className="border-b border-border bg-muted/20">
                      {['Application', 'Category', 'Share of Time', 'Duration', 'Sessions', 'Status'].map(h => (
                        <th key={h} className="text-left px-4 py-3 text-xs font-semibold text-muted-foreground uppercase tracking-wider whitespace-nowrap">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {topApps.map((app, idx) => {
                      const sharePct = totalDuration > 0 ? Math.min(100, Math.round((app.totalSeconds / totalDuration) * 100)) : 0;
                      return (
                        <tr key={`${app.name}-${idx}`} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                          <td className="px-4 py-3">
                            <div className="flex items-center gap-2.5">
                              <div
                                className="w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0"
                                style={{ backgroundColor: `${app.color}15`, color: app.color }}
                              >
                                <AppWindow className="w-4 h-4" />
                              </div>
                              <div className="min-w-0">
                                <p className="text-sm font-medium text-foreground truncate">{app.name}</p>
                                <span className="text-[11px] text-muted-foreground">Rank #{idx + 1}</span>
                              </div>
                            </div>
                          </td>
                          <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">
                            <span className="px-2 py-0.5 rounded-md text-xs font-medium bg-muted text-muted-foreground">
                              {app.category || '-'}
                            </span>
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap min-w-[160px]">
                            <div className="flex items-center gap-2">
                              <div className="flex-1 h-2 rounded-full bg-muted overflow-hidden">
                                <div
                                  className="h-full rounded-full transition-all duration-300"
                                  style={{ width: `${sharePct}%`, backgroundColor: app.color }}
                                />
                              </div>
                              <span className="text-xs font-medium text-muted-foreground w-8 text-right font-mono">{sharePct}%</span>
                            </div>
                          </td>
                          <td className="px-4 py-3 text-sm text-foreground font-semibold whitespace-nowrap font-mono">{formatSeconds(app.totalSeconds)}</td>
                          <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap font-mono">{app.sessionCount}</td>
                          <td className="px-4 py-3 whitespace-nowrap">
                            <span className={cn(
                              "px-2 py-0.5 rounded-md text-[11px] font-medium border",
                              app.type === 'Productive'
                                ? "bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 border-emerald-500/20"
                                : app.type === 'Unproductive'
                                ? "bg-rose-500/10 text-rose-600 dark:text-rose-400 border-rose-500/20"
                                : "bg-slate-500/10 text-slate-600 dark:text-slate-400 border-slate-500/20"
                            )}>
                              {app.type || 'Neutral'}
                            </span>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}

type ChartType = 'histogram' | 'pie' | 'nightingale' | 'line';

const chartOptions: Array<{ value: ChartType; label: string; icon: React.ElementType }> = [
  { value: 'histogram', label: 'Histogram', icon: BarChart3 },
  { value: 'pie', label: 'Pie', icon: PieChartIcon },
  { value: 'nightingale', label: 'Nightingale', icon: Radar },
  { value: 'line', label: 'Line', icon: LineChartIcon },
];

function ChartSelector({ value, onChange }: { value: ChartType; onChange: (next: ChartType) => void }) {
  return (
    <div className="flex items-center gap-1 p-1 mb-4 overflow-x-auto bg-muted/60 border border-border/80 rounded-xl w-fit max-w-full">
      {chartOptions.map(({ value: option, label, icon: Icon }) => (
        <button
          key={option}
          type="button"
          onClick={() => onChange(option)}
          aria-pressed={value === option}
          className={cn(
            'flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-semibold whitespace-nowrap transition-all',
            value === option
              ? 'bg-card text-foreground shadow-sm border border-border/60'
              : 'text-muted-foreground hover:text-foreground',
          )}
        >
          <Icon className="w-3.5 h-3.5" />
          {label}
        </button>
      ))}
    </div>
  );
}

function HoursChart({
  chartType,
  viewMode,
  productivityData,
  appData,
  appTotals,
  appKeys,
  appConfig,
  totalDuration,
  productivityTotals,
}: {
  chartType: ChartType;
  viewMode: 'productivity' | 'applications';
  productivityData: Array<{ bucket: string; productive: number; neutral: number; unproductive: number }>;
  appData: Array<Record<string, string | number>>;
  appTotals: Array<{ name: string; totalSeconds: number; color: string }>;
  appKeys: string[];
  appConfig: Record<string, { label: string; color: string }>;
  totalDuration: number;
  productivityTotals: { productive: number; neutral: number; unproductive: number };
}) {
  const isProductivity = viewMode === 'productivity';
  const pieData = isProductivity
    ? [
        { name: 'Productive', value: productivityData.reduce((sum, row) => sum + row.productive, 0), color: PRODUCTIVE_COLOR },
        { name: 'Neutral', value: productivityData.reduce((sum, row) => sum + row.neutral, 0), color: NEUTRAL_COLOR },
        { name: 'Unproductive', value: productivityData.reduce((sum, row) => sum + row.unproductive, 0), color: UNPRODUCTIVE_COLOR },
      ]
    : (() => {
        const topAppSeconds = appTotals.reduce((sum, app) => sum + app.totalSeconds, 0);
        const otherSeconds = Math.max(totalDuration - topAppSeconds, 0);
        const items = appTotals
          .map((app) => ({ name: app.name, value: app.totalSeconds, color: app.color }))
          .filter((item) => item.value > 0);
        if (otherSeconds > 0) {
          items.push({ name: 'Other', value: otherSeconds, color: '#94a3b8' });
        }
        return items;
      })();

  // Application Histogram intentionally uses the server-provided totals rather
  // than hourly buckets. This keeps it aligned with the breakdown and avoids
  // presenting one bar for every hour in the selected range.
  const histogramData = isProductivity
    ? [
        { bucket: 'Productive', total: productivityTotals.productive, color: PRODUCTIVE_COLOR },
        { bucket: 'Neutral', total: productivityTotals.neutral, color: NEUTRAL_COLOR },
        { bucket: 'Unproductive', total: productivityTotals.unproductive, color: UNPRODUCTIVE_COLOR },
      ].filter((entry) => entry.total > 0)
    : appTotals
        .filter((app) => app.totalSeconds > 0)
        .map((app) => ({
          bucket: app.name,
          total: app.totalSeconds,
          color: app.color,
        }));

  const lineKeys = isProductivity ? ['productive', 'neutral', 'unproductive'] : appKeys;
  const lineConfig = isProductivity
    ? productivityChartConfig
    : appConfig;

  const formatValue = (value: number) => formatSeconds(Number(value));
  const chartHeight = 'h-[340px] sm:h-[380px]';

  return (
    <ChartContainer config={lineConfig} className={`${chartHeight} w-full`}>
      <ResponsiveContainer width="100%" height="100%">
        {chartType === 'histogram' ? (
          <BarChart
            data={histogramData}
            margin={{ top: isProductivity ? 10 : 28, right: 18, left: 0, bottom: isProductivity ? 0 : 18 }}
          >
            <CartesianGrid strokeDasharray="3 3" stroke="var(--color-border)" opacity={0.6} />
            <XAxis
              dataKey="bucket"
              tickLine={false}
              axisLine={false}
              fontSize={11}
              interval={0}
              angle={isProductivity ? 0 : -18}
              textAnchor={isProductivity ? 'middle' : 'end'}
              height={isProductivity ? 30 : 54}
              tickFormatter={(value: unknown) => {
                const label = String(value);
                return isProductivity ? label : (label.length > 18 ? `${label.slice(0, 18)}…` : label);
              }}
            />
            <YAxis tickLine={false} axisLine={false} fontSize={11} width={65} tickFormatter={formatValue} />
            <Tooltip formatter={(value: number) => [formatValue(value), 'Usage']} />
            <Bar dataKey="total" name="Usage" radius={[5, 5, 0, 0]} maxBarSize={72}>
              {histogramData.map((entry) => (
                <Cell key={String(entry.bucket)} fill={'color' in entry ? String(entry.color) : '#3b82f6'} />
              ))}
              <LabelList
                dataKey="total"
                position="top"
                formatter={(value: unknown) => formatSeconds(Number(value))}
                fill="currentColor"
                fontSize={12}
                fontWeight={600}
              />
            </Bar>
          </BarChart>
        ) : chartType === 'pie' || chartType === 'nightingale' ? (
          <PieChart>
            {chartType === 'pie' ? (
              <Pie
                data={pieData}
                dataKey="value"
                nameKey="name"
                cx="50%"
                cy="50%"
                innerRadius={72}
                outerRadius={122}
                paddingAngle={3}
                label={({ name, value }) => `${name} ${Math.round((value / Math.max(totalDuration, 1)) * 100)}%`}
              >
                {pieData.map((entry) => <Cell key={entry.name} fill={entry.color} />)}
              </Pie>
            ) : (
              pieData.map((entry, index) => {
                const total = pieData.reduce((sum, item) => sum + item.value, 0);
                const previous = pieData.slice(0, index).reduce((sum, item) => sum + item.value, 0);
                const startAngle = 90 - (previous / Math.max(total, 1)) * 360;
                const endAngle = startAngle - (entry.value / Math.max(total, 1)) * 360;
                return (
                  <Pie
                    key={entry.name}
                    data={[entry]}
                    dataKey="value"
                    nameKey="name"
                    cx="50%"
                    cy="50%"
                    innerRadius={18}
                    outerRadius={72 + index * 12}
                    startAngle={startAngle}
                    endAngle={endAngle}
                    paddingAngle={2}
                  >
                    <Cell fill={entry.color} />
                  </Pie>
                );
              })
            )}
            <Tooltip formatter={(value: number, name: string) => [formatValue(value), name]} />
            <Legend />
          </PieChart>
        ) : (
          <LineChart data={isProductivity ? productivityData : appData} margin={{ top: 10, right: 12, left: 0, bottom: 0 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--color-border)" opacity={0.6} />
            <XAxis dataKey="bucket" tickLine={false} axisLine={false} fontSize={11} />
            <YAxis tickLine={false} axisLine={false} fontSize={11} width={65} tickFormatter={formatValue} />
            <Tooltip formatter={(value: number, name: string) => [formatValue(value), lineConfig[name]?.label || name]} />
            <Legend />
            {lineKeys.map((key) => (
              <Line
                key={key}
                type="monotone"
                dataKey={key}
                name={lineConfig[key]?.label || key}
                stroke={lineConfig[key]?.color || '#94a3b8'}
                strokeWidth={2}
                dot={{ r: 2 }}
                activeDot={{ r: 5 }}
              />
            ))}
          </LineChart>
        )}
      </ResponsiveContainer>
    </ChartContainer>
  );
}

function UsageTile({
  icon: Icon,
  label,
  value,
  badge,
  accent,
}: {
  icon: React.ElementType;
  label: string;
  value: number | string;
  badge?: string;
  accent: string;
}) {
  return (
    <div className="bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-all">
      <div className="flex items-center justify-between gap-2">
        <div className={`w-8 h-8 rounded-lg flex items-center justify-center ${accent}`}>
          <Icon className="w-4 h-4" />
        </div>
        {badge && (
          <span className="px-2 py-0.5 rounded-full text-[11px] font-semibold bg-muted text-muted-foreground border border-border">
            {badge}
          </span>
        )}
      </div>
      <p className="mt-3 text-xl sm:text-2xl font-bold text-foreground font-display truncate" title={String(value)}>
        {value}
      </p>
      <p className="text-xs text-muted-foreground mt-0.5 font-medium">{label}</p>
    </div>
  );
}

function Clock(props: React.SVGProps<SVGSVGElement>) {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...props}>
      <circle cx="12" cy="12" r="10" />
      <polyline points="12 6 12 12 16 14" />
    </svg>
  );
}
