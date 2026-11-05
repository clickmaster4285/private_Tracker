'use client';

import Link from 'next/link';
import {
  Users, UserCheck, Wifi, MonitorSmartphone, Globe, Radio,
  AlertTriangle, AppWindow, Building2, Loader2, CalendarCheck,
} from 'lucide-react';
import StatsCard from '@/components/ui/StatsCard';
import type { DashboardSummaryResponse } from '@/lib/api';

function SkeletonCard() {
  return (
    <div className="bg-card rounded-xl border border-border p-5 animate-pulse">
      <div className="h-4 w-24 bg-muted rounded mb-3" />
      <div className="h-8 w-16 bg-muted rounded" />
    </div>
  );
}

export function DashboardStatGrid({
  summary,
  loading,
  error,
  onRetry,
}: {
  summary?: DashboardSummaryResponse;
  loading: boolean;
  error?: boolean;
  onRetry?: () => void;
}) {
  if (loading && !summary) {
    return (
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-6 gap-4">
        {Array.from({ length: 6 }).map((_, i) => <SkeletonCard key={i} />)}
      </div>
    );
  }

  if (error && !summary) {
    return (
      <div className="bg-card rounded-xl border border-border p-5 flex items-center justify-between gap-3">
        <p className="text-sm text-muted-foreground">Failed to load dashboard metrics.</p>
        {onRetry && (
          <button type="button" onClick={onRetry} className="text-sm text-primary hover:underline">
            Retry
          </button>
        )}
      </div>
    );
  }

  if (!summary) return null;

  const { employees, activity, live } = summary;

  return (
    <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-6 gap-4">
      <Link href="/employees" className="block">
        <StatsCard
          title="Employees"
          value={employees.total}
          icon={Users}
          subtitle={`Tracked ${employees.tracked} · Untracked ${employees.untracked}`}
          delay={0.05}
        />
      </Link>
      <Link href="/employees?status=tracked" className="block">
        <StatsCard
          title="Tracked"
          value={employees.tracked}
          icon={UserCheck}
          subtitle={employees.untracked > 0 ? `${employees.untracked} need install` : 'All tracked'}
          subtitleColor={employees.untracked > 0 ? 'text-warning' : 'text-muted-foreground'}
          delay={0.08}
        />
      </Link>
      <Link href="/live-stream" className="block">
        <StatsCard
          title="Online now"
          value={live.presenceAvailable ? live.online : '—'}
          icon={Wifi}
          subtitle={live.presenceAvailable ? (live.streaming > 0 ? `${live.streaming} streaming` : 'Presence') : 'Live stream off'}
          delay={0.11}
        />
      </Link>
      <StatsCard
        title="Sessions"
        value={activity.sessions}
        icon={MonitorSmartphone}
        subtitle={`${activity.openSessions} open now`}
        delay={0.14}
      />
      <StatsCard
        title="Web tabs"
        value={activity.webPages}
        icon={Globe}
        subtitle="In selected range"
        delay={0.17}
      />
      <StatsCard
        title="Open sessions"
        value={activity.openSessions}
        icon={Radio}
        subtitle={activity.staleSessions > 0 ? `${activity.staleSessions} stale` : 'Live status'}
        subtitleColor={activity.staleSessions > 0 ? 'text-warning' : undefined}
        delay={0.2}
      />
    </div>
  );
}

export function AttentionStrip({ summary }: { summary?: DashboardSummaryResponse }) {
  if (!summary) return null;
  const items: { label: string; href: string; tone: 'warning' | 'muted' }[] = [];
  if (summary.employees.untracked > 0) {
    items.push({
      label: `${summary.employees.untracked} untracked employees`,
      href: '/employees',
      tone: 'warning',
    });
  }
  if (summary.monitoring.unclassifiedApps > 0) {
    items.push({
      label: `${summary.monitoring.unclassifiedApps} unclassified apps`,
      href: '/configuration/apps',
      tone: 'warning',
    });
  }
  if (summary.monitoring.unclassifiedSites > 0) {
    items.push({
      label: `${summary.monitoring.unclassifiedSites} unclassified websites`,
      href: '/configuration/websites',
      tone: 'warning',
    });
  }
  if (summary.devices.stale7d > 0) {
    items.push({
      label: `${summary.devices.stale7d} stale devices (7d+)`,
      href: '/employees',
      tone: 'warning',
    });
  }
  if (summary.activity.staleSessions > 0) {
    items.push({
      label: `${summary.activity.staleSessions} stale sessions`,
      href: '/employee-journey/timeline',
      tone: 'muted',
    });
  }
  if (items.length === 0) return null;

  return (
    <div className="bg-card rounded-xl border border-border p-4 flex flex-wrap items-center gap-3">
      <AlertTriangle className="w-4 h-4 text-warning flex-shrink-0" />
      <span className="text-sm font-medium text-foreground">Needs attention</span>
      <div className="flex flex-wrap gap-2">
        {items.map((item) => (
          <Link
            key={item.label}
            href={item.href}
            className={`text-xs px-2.5 py-1 rounded-md border ${
              item.tone === 'warning'
                ? 'border-warning/40 text-warning bg-warning/5'
                : 'border-border text-muted-foreground'
            } hover:opacity-80`}
          >
            {item.label}
          </Link>
        ))}
      </div>
    </div>
  );
}

export function AttendanceCard({ summary, loading }: { summary?: DashboardSummaryResponse; loading?: boolean }) {
  const attendance = summary?.attendance;
  const rows = attendance
    ? [
        { label: 'Present', value: attendance.present, color: 'text-success' },
        { label: 'Late', value: attendance.late, color: attendance.late > 0 ? 'text-warning' : '' },
        { label: 'Absent', value: attendance.absent, color: attendance.absent > 0 ? 'text-destructive' : '' },
        { label: 'Off shift', value: attendance.offShift, color: '' },
        // Surfaced only when non-zero so an unconfigured shift doesn't render
        // as a silently all-zero card (the /attendance page hides it too).
        ...(attendance.halfDay > 0 ? [{ label: 'Half day', value: attendance.halfDay, color: 'text-warning' }] : []),
        ...(attendance.unknown > 0 ? [{ label: 'Not recorded', value: attendance.unknown, color: 'text-muted-foreground' }] : []),
      ]
    : [];

  return (
    <div className="bg-card rounded-xl border border-border p-5 h-full">
      <h3 className="font-display font-bold text-foreground mb-3 flex items-center gap-2">
        <CalendarCheck className="w-4 h-4 text-primary" /> Attendance
        <span className="ml-auto text-xs font-normal text-muted-foreground">Today</span>
      </h3>
      {loading && !summary ? (
        <div className="flex justify-center py-8"><Loader2 className="w-5 h-5 animate-spin text-primary" /></div>
      ) : !attendance ? (
        <p className="text-sm text-muted-foreground">No attendance data.</p>
      ) : (
        <div className="space-y-3 text-sm">
          {rows.map((row) => (
            <div key={row.label} className="flex justify-between">
              <span className="text-muted-foreground">{row.label}</span>
              <span className={`font-medium ${row.color}`}>{row.value}</span>
            </div>
          ))}
          <div className="flex items-center justify-between border-t border-border pt-2">
            <span className="text-xs text-muted-foreground">{attendance.total} employees</span>
            <Link href="/attendance" className="text-xs text-primary hover:underline">
              View attendance →
            </Link>
          </div>
        </div>
      )}
    </div>
  );
}

export function ClassificationPulse({ summary, loading }: { summary?: DashboardSummaryResponse; loading?: boolean }) {
  return (
    <div className="bg-card rounded-xl border border-border p-5 h-full">
      <h3 className="font-display font-bold text-foreground mb-3 flex items-center gap-2">
        <AppWindow className="w-4 h-4 text-primary" /> Classification backlog
      </h3>
      {loading && !summary ? (
        <div className="flex justify-center py-8"><Loader2 className="w-5 h-5 animate-spin text-primary" /></div>
      ) : !summary ? (
        <p className="text-sm text-muted-foreground">No monitoring data.</p>
      ) : (
        <div className="space-y-3 text-sm">
          <Link href="/configuration/apps" className="flex justify-between hover:opacity-80">
            <span className="text-muted-foreground">Unclassified apps</span>
            <span className={`font-medium ${summary.monitoring.unclassifiedApps > 0 ? 'text-warning' : ''}`}>
              {summary.monitoring.unclassifiedApps}
            </span>
          </Link>
          <Link href="/configuration/websites" className="flex justify-between hover:opacity-80">
            <span className="text-muted-foreground">Unclassified websites</span>
            <span className={`font-medium ${summary.monitoring.unclassifiedSites > 0 ? 'text-warning' : ''}`}>
              {summary.monitoring.unclassifiedSites}
            </span>
          </Link>
          {(summary.monitoring.unclassifiedApps > 0 || summary.monitoring.unclassifiedSites > 0) && (
            <p className="text-xs text-muted-foreground pt-1">
              Classify apps and sites so productivity scoring can work later.
            </p>
          )}
        </div>
      )}
    </div>
  );
}

export function QuickActions({ canAccess }: { canAccess: (module: string) => boolean }) {
  const actions = [
    { label: 'Employees', href: '/employees', module: 'users', icon: Users },
    { label: 'Live Stream', href: '/live-stream', module: 'live-stream', icon: Radio },
    { label: 'Session Timeline', href: '/employee-journey/timeline', module: 'employee-journey', icon: MonitorSmartphone },
    { label: 'App Usage', href: '/employee-journey/apps', module: 'employee-journey', icon: AppWindow },
    { label: 'Web Activity', href: '/employee-journey/web', module: 'employee-journey', icon: Globe },
    { label: 'Classify Apps', href: '/configuration/apps', module: 'configuration/apps', icon: AppWindow },
    { label: 'Classify Sites', href: '/configuration/websites', module: 'configuration/websites', icon: Globe },
    { label: 'Departments', href: '/departments', module: 'departments', icon: Building2 },
  ].filter((a) => canAccess(a.module));

  return (
    <div className="bg-card rounded-xl border border-border p-5 h-full">
      <h3 className="font-display font-bold text-foreground mb-3">Quick actions</h3>
      {actions.length === 0 ? (
        <p className="text-sm text-muted-foreground">No modules available for your role.</p>
      ) : (
        <div className="grid grid-cols-2 gap-2">
          {actions.map((a) => {
            const Icon = a.icon;
            return (
              <Link
                key={a.href}
                href={a.href}
                className="flex items-center gap-2 text-sm px-3 py-2 rounded-lg border border-border hover:bg-accent transition-colors"
              >
                <Icon className="w-3.5 h-3.5 text-primary flex-shrink-0" />
                <span className="truncate">{a.label}</span>
              </Link>
            );
          })}
        </div>
      )}
    </div>
  );
}
