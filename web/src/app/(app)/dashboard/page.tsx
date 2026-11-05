'use client';

import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Suspense } from 'react';
import { Building2, LayoutDashboard, Loader2, X } from 'lucide-react';
import DownloadAppSection from '@/components/DownloadAppSection';
import ActivityFilters from '@/components/journey/ActivityFilters';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { useAuth } from '@/lib/auth';
import { usePermissions } from '@/lib/permissions';
import { dashboardApi, departmentsApi, liveStreamApi, ApiError } from '@/lib/api';
import { useDashboardFilters } from '@/components/dashboard/use-dashboard-filters';
import {
  AttentionStrip,
  ClassificationPulse,
  DashboardStatGrid,
  AttendanceCard,
  QuickActions,
} from '@/components/dashboard/DashboardStatGrid';
import { OnlineNowPanel } from '@/components/dashboard/OnlineNowPanel';
import {
  RecentSessions,
  TopAppsWidget,
  TopDomainsWidget,
} from '@/components/dashboard/ActivityWidgets';

const DOWNLOAD_DISMISS_KEY = 'alpha_dashboard_download_dismissed';

function DashboardInner() {
  const { user } = useAuth();
  const { canAccess } = usePermissions();
  const role = user?.role || '';
  const { filter, setFilter, departmentId, setDepartmentId, summaryRange } = useDashboardFilters();
  const range = summaryRange();

  const [downloadDismissed, setDownloadDismissed] = useState(() => {
    if (typeof window === 'undefined') return false;
    try {
      return sessionStorage.getItem(DOWNLOAD_DISMISS_KEY) === '1';
    } catch {
      return false;
    }
  });

  const summaryQuery = useQuery({
    queryKey: ['dashboard', 'summary', range.from, range.to, departmentId ?? null],
    queryFn: () =>
      dashboardApi.summary({
        from: range.from,
        to: range.to,
        departmentId,
      }),
    staleTime: 45_000,
  });

  const presenceQuery = useQuery({
    queryKey: ['dashboard', 'presence', departmentId ?? null],
    queryFn: () =>
      liveStreamApi.employees({
        onlineOnly: true,
        limit: 24,
        departmentId,
      }),
    staleTime: 5_000,
    refetchInterval: 12_000,
    refetchIntervalInBackground: false,
    retry: false,
  });

  const departmentsQuery = useQuery({
    queryKey: ['dashboard', 'departments'],
    queryFn: () => departmentsApi.list(),
    staleTime: 10 * 60_000,
  });

  const presenceDisabled = useMemo(() => {
    const err = presenceQuery.error;
    return err instanceof ApiError && err.status === 503;
  }, [presenceQuery.error]);

  const summary = summaryQuery.data;
  const can = (module: string) => canAccess(role, module);

  const departments = departmentsQuery.data?.departments ?? [];

  return (
    <div className="space-y-6 animate-fade-in">
      <header className="rounded-xl border border-border bg-card shadow-card">
        <div className="flex items-start gap-3 px-4 py-4 sm:px-5">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl gradient-primary">
            <LayoutDashboard className="h-5 w-5 text-primary-foreground" />
          </div>
          <div className="min-w-0">
            <h2 className="font-display text-lg font-bold leading-tight text-foreground">Overview</h2>
            <p className="mt-1 max-w-2xl text-sm leading-relaxed text-muted-foreground">
              Workforce, productivity, attendance, and activity for the period and department you select.
            </p>
          </div>
        </div>
        <div className="flex flex-col gap-3 border-t border-border px-4 py-3 sm:px-5 lg:flex-row lg:flex-wrap lg:items-center lg:justify-between">
          <Select
            value={departmentId ? String(departmentId) : 'all'}
            onValueChange={(value) => setDepartmentId(value === 'all' ? undefined : Number(value))}
          >
            <SelectTrigger className="relative h-9 w-full bg-background pl-9 sm:w-56" aria-label="Department">
              <Building2 className="pointer-events-none absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
              <SelectValue placeholder="All departments" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All departments</SelectItem>
              {departments.map((department) => (
                <SelectItem key={department.id} value={String(department.id)}>
                  {department.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <ActivityFilters
            value={filter}
            onChange={setFilter}
            loading={summaryQuery.isFetching}
            availablePresets={['today', 'yesterday', '7d', '30d', 'all']}
            searchShow={false}
          />
        </div>
      </header>

      <DashboardStatGrid
        summary={summary}
        loading={summaryQuery.isLoading}
        error={summaryQuery.isError}
        onRetry={() => summaryQuery.refetch()}
      />

      <AttentionStrip summary={summary} />

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {can('live-stream') && (
          <OnlineNowPanel
            employees={presenceQuery.data?.data}
            total={presenceQuery.data?.total}
            loading={presenceQuery.isLoading}
            error={presenceQuery.isError && !presenceDisabled}
            disabled={presenceDisabled}
            onRetry={() => presenceQuery.refetch()}
          />
        )}
        <AttendanceCard summary={summary} loading={summaryQuery.isLoading} />
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {can('configuration/apps') || can('configuration/websites') ? (
          <ClassificationPulse summary={summary} loading={summaryQuery.isLoading} />
        ) : null}
        <QuickActions canAccess={can} />
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <TopAppsWidget apps={summary?.topApps} />
        <TopDomainsWidget domains={summary?.topDomains} />
      </div>

      <RecentSessions sessions={summary?.recentSessions} />

      {!downloadDismissed && (
        <div className="relative">
          <button
            type="button"
            aria-label="Dismiss download banner"
            className="absolute top-3 right-3 z-10 p-1 rounded-md text-muted-foreground hover:bg-accent"
            onClick={() => {
              try {
                sessionStorage.setItem(DOWNLOAD_DISMISS_KEY, '1');
              } catch { /* ignore */ }
              setDownloadDismissed(true);
            }}
          >
            <X className="w-4 h-4" />
          </button>
          <DownloadAppSection compact />
        </div>
      )}
    </div>
  );
}

export default function DashboardPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[400px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <DashboardInner />
    </Suspense>
  );
}
