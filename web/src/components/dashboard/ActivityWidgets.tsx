'use client';

import Link from 'next/link';
import { AppWindow, Globe, Clock } from 'lucide-react';
import SessionStatusBadge, { sessionStatus } from '@/components/sessions/SessionStatusBadge';
import type { DashboardRecentSession, DashboardTopApp, DashboardTopDomain } from '@/lib/api';
import { formatDateTime } from '@/lib/format';

export function TopAppsWidget({ apps }: { apps?: DashboardTopApp[] }) {
  return (
    <div className="bg-card rounded-xl border border-border p-5 h-full">
      <div className="flex items-center justify-between mb-3">
        <h3 className="font-display font-bold text-foreground flex items-center gap-2">
          <AppWindow className="w-4 h-4 text-primary" /> Top apps
        </h3>
        <Link href="/employee-journey/apps" className="text-xs text-primary hover:underline">App Usage</Link>
      </div>
      {!apps || apps.length === 0 ? (
        <p className="text-sm text-muted-foreground py-4">No app sessions in this range.</p>
      ) : (
        <ul className="space-y-2">
          {apps.map((a) => (
            <li key={`${a.appDisplayName}|${a.processName}`} className="flex items-center justify-between gap-2 text-sm">
              <span className="truncate font-medium">{a.appDisplayName || a.processName}</span>
              <span className="text-xs text-muted-foreground flex-shrink-0">
                {a.sessionCount} sess{a.openNow > 0 ? ` · ${a.openNow} open` : ''}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function TopDomainsWidget({ domains }: { domains?: DashboardTopDomain[] }) {
  return (
    <div className="bg-card rounded-xl border border-border p-5 h-full">
      <div className="flex items-center justify-between mb-3">
        <h3 className="font-display font-bold text-foreground flex items-center gap-2">
          <Globe className="w-4 h-4 text-primary" /> Top domains
        </h3>
        <Link href="/employee-journey/web" className="text-xs text-primary hover:underline">Web Activity</Link>
      </div>
      {!domains || domains.length === 0 ? (
        <p className="text-sm text-muted-foreground py-4">No web activity in this range.</p>
      ) : (
        <ul className="space-y-2">
          {domains.map((d) => (
            <li key={d.domain} className="flex items-center justify-between gap-2 text-sm">
              <span className="truncate font-medium font-mono text-xs sm:text-sm">{d.domain}</span>
              <span className="text-xs text-muted-foreground flex-shrink-0">{d.visits} visits</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function RecentSessions({ sessions }: { sessions?: DashboardRecentSession[] }) {
  return (
    <div className="bg-card rounded-xl border border-border p-5">
      <div className="flex items-center justify-between mb-3">
        <h3 className="font-display font-bold text-foreground flex items-center gap-2">
          <Clock className="w-4 h-4 text-primary" /> Recent sessions
        </h3>
        <Link href="/employee-journey/timeline" className="text-xs text-primary hover:underline">Timeline</Link>
      </div>
      {!sessions || sessions.length === 0 ? (
        <p className="text-sm text-muted-foreground py-4">No sessions in this range.</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-xs text-muted-foreground border-b border-border">
                <th className="pb-2 font-medium">Employee</th>
                <th className="pb-2 font-medium">Application</th>
                <th className="pb-2 font-medium">Status</th>
                <th className="pb-2 font-medium">Opened</th>
              </tr>
            </thead>
            <tbody>
              {sessions.map((s) => (
                <tr key={s.id} className="border-b border-border/60 last:border-0">
                  <td className="py-2.5 pr-3">
                    <Link
                      href={`/employee-journey/timeline?employeeId=${encodeURIComponent(s.employeeId)}`}
                      className="text-primary hover:underline"
                    >
                      {s.employeeName || s.employeeId}
                    </Link>
                  </td>
                  <td className="py-2.5 pr-3 truncate max-w-[200px]">{s.appDisplayName || s.processName}</td>
                  <td className="py-2.5 pr-3">
                    <SessionStatusBadge
                      status={sessionStatus({ status: s.status, endedAt: s.endedAt ?? undefined })}
                    />
                  </td>
                  <td className="py-2.5 text-muted-foreground whitespace-nowrap">
                    {formatDateTime(s.startedAt)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
