'use client';

import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Coffee,
  EyeOff,
  Lock,
  LogIn,
  LogOut,
  Monitor,
  Power,
  PowerOff,
  RotateCcw,
  Timer,
  Unlock,
  Loader2,
  AlertTriangle,
} from 'lucide-react';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import EmptyState from '@/components/employees/EmptyState';
import { attendanceApi, type AttendanceRecord, type SessionEventRow } from '@/lib/api';
import { SESSION_EVENT_TYPES } from '@/lib/eventTypes';
import { formatSeconds, formatTimeInZone } from '@/lib/format';
import { cn } from '@/lib/utils';

const EVENT_META: Record<
  string,
  { label: string; hint: string; className: string; icon: typeof Power }
> = {
  [SESSION_EVENT_TYPES.POWER_ON]: {
    label: 'Power on',
    hint: 'Machine started or woke into a session',
    className: 'bg-success/15 text-success border-success/25',
    icon: Power,
  },
  [SESSION_EVENT_TYPES.POWER_OFF]: {
    label: 'Power off',
    hint: 'Shutdown or restart',
    className: 'bg-destructive/15 text-destructive border-destructive/25',
    icon: PowerOff,
  },
  [SESSION_EVENT_TYPES.RESUME]: {
    label: 'Resume',
    hint: 'Woke from sleep',
    className: 'bg-info/15 text-info border-info/25',
    icon: RotateCcw,
  },
  [SESSION_EVENT_TYPES.OS_LOGIN]: {
    label: 'OS login',
    hint: 'Signed into the desktop',
    className: 'bg-primary/10 text-primary border-primary/20',
    icon: LogIn,
  },
  [SESSION_EVENT_TYPES.OS_LOGOUT]: {
    label: 'OS logout',
    hint: 'Signed out of the desktop',
    className: 'bg-muted text-muted-foreground border-border',
    icon: LogOut,
  },
  [SESSION_EVENT_TYPES.SCREEN_LOCK]: {
    label: 'Screen lock',
    hint: 'Workstation locked',
    className: 'bg-warning/15 text-warning border-warning/25',
    icon: Lock,
  },
  [SESSION_EVENT_TYPES.SCREEN_UNLOCK]: {
    label: 'Screen unlock',
    hint: 'Workstation unlocked',
    className: 'bg-success/15 text-success border-success/25',
    icon: Unlock,
  },
  [SESSION_EVENT_TYPES.TRACKER_LOGIN]: {
    label: 'Tracker login',
    hint: 'Desktop client authenticated',
    className: 'bg-primary/10 text-primary border-primary/20',
    icon: Monitor,
  },
  [SESSION_EVENT_TYPES.UI_HIDDEN]: {
    label: 'UI hidden',
    hint: 'Tracker window sent to tray',
    className: 'bg-muted text-muted-foreground border-border',
    icon: EyeOff,
  },
  [SESSION_EVENT_TYPES.IDLE_START]: {
    label: 'Idle start',
    hint: 'No input — idle period began',
    className: 'bg-warning/15 text-warning border-warning/25',
    icon: Coffee,
  },
  [SESSION_EVENT_TYPES.IDLE_END]: {
    label: 'Idle end',
    hint: 'Activity resumed',
    className: 'bg-success/15 text-success border-success/25',
    icon: Timer,
  },
  [SESSION_EVENT_TYPES.OLD_DATA_DROPPED]: {
    label: 'Old data dropped',
    hint: 'Local queue rolled off unsynced rows',
    className: 'bg-destructive/15 text-destructive border-destructive/25',
    icon: AlertTriangle,
  },
  [SESSION_EVENT_TYPES.LOGIN]: {
    label: 'Login (legacy)',
    hint: 'Older tracker_login alias',
    className: 'bg-muted text-muted-foreground border-border',
    icon: LogIn,
  },
};

function metaFor(type: string) {
  return (
    EVENT_META[type] ?? {
      label: type.replace(/_/g, ' '),
      hint: 'Session event',
      className: 'bg-muted text-muted-foreground border-border',
      icon: Timer,
    }
  );
}

function formatWorkDate(ymd: string): string {
  const d = new Date(`${ymd}T00:00:00`);
  if (Number.isNaN(d.getTime())) return ymd;
  return d.toLocaleDateString(undefined, {
    weekday: 'long',
    month: 'long',
    day: 'numeric',
    year: 'numeric',
  });
}

export default function DayEventsDialog({
  open,
  onOpenChange,
  employeeName,
  row,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  employeeName: string;
  row: AttendanceRecord | null;
}) {
  const employeeId = row?.employeeId ?? '';
  const workDate = row?.workDate ?? '';

  const query = useQuery({
    queryKey: ['attendance', 'events', employeeId, workDate],
    queryFn: () => attendanceApi.events(employeeId, workDate),
    enabled: open && Boolean(employeeId && workDate),
  });

  const events = query.data?.data ?? [];
  const timezone = query.data?.timezone || row?.timezone;

  const counts = useMemo(() => {
    const c: Record<string, number> = {};
    for (const e of events) {
      c[e.eventType] = (c[e.eventType] ?? 0) + Math.max(1, e.count || 1);
    }
    return c;
  }, [events]);

  const chips = [
    { key: SESSION_EVENT_TYPES.POWER_ON, label: 'Power on' },
    { key: SESSION_EVENT_TYPES.POWER_OFF, label: 'Power off' },
    { key: SESSION_EVENT_TYPES.SCREEN_LOCK, label: 'Lock' },
    { key: SESSION_EVENT_TYPES.SCREEN_UNLOCK, label: 'Unlock' },
    { key: SESSION_EVENT_TYPES.IDLE_START, label: 'Idle' },
  ].filter(chip => (counts[chip.key] ?? 0) > 0);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg max-h-[85vh] overflow-hidden p-0 gap-0 sm:rounded-2xl">
        <div className="border-b border-border bg-card px-6 py-5">
          <DialogHeader>
            <DialogTitle className="font-display text-lg">Day timeline</DialogTitle>
            <DialogDescription className="text-sm text-muted-foreground">
              {employeeName}
              {workDate ? ` · ${formatWorkDate(workDate)}` : ''}
              {timezone ? ` · ${timezone}` : ''}
            </DialogDescription>
          </DialogHeader>
          {row && (
            <div className="mt-4 grid grid-cols-3 gap-2">
              <MiniStat label="Active" value={formatSeconds(row.activeSeconds)} className="text-success" />
              <MiniStat label="Idle / locked" value={formatSeconds(row.idleSeconds)} className="text-muted-foreground" />
              <MiniStat label="Off shift" value={formatSeconds(row.offShiftSeconds)} className="text-info" />
            </div>
          )}
          {chips.length > 0 && (
            <div className="mt-3 flex flex-wrap gap-1.5">
              {chips.map(chip => (
                <span
                  key={chip.key}
                  className="rounded-full border border-border bg-background px-2.5 py-0.5 text-[11px] font-medium text-muted-foreground"
                >
                  {chip.label} {counts[chip.key]}
                </span>
              ))}
            </div>
          )}
        </div>

        <div className="max-h-[min(52vh,480px)] overflow-y-auto px-6 py-5">
          {query.isLoading ? (
            <div className="flex items-center justify-center py-16">
              <Loader2 className="h-6 w-6 animate-spin text-primary" />
            </div>
          ) : query.isError ? (
            <p className="py-8 text-center text-sm text-destructive">
              {(query.error as Error).message || 'Failed to load events'}
            </p>
          ) : events.length === 0 ? (
            <EmptyState
              icon={Timer}
              text="No power, lock, or idle events were recorded on this day."
            />
          ) : (
            <ol className="relative space-y-0 border-l border-border ml-3">
              {events.map((event, i) => (
                <EventRow key={event.id || `${event.eventType}-${i}`} event={event} timezone={timezone} />
              ))}
            </ol>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}

function MiniStat({ label, value, className }: { label: string; value: string; className?: string }) {
  return (
    <div className="rounded-xl border border-border bg-background/80 px-3 py-2">
      <p className={cn('text-sm font-display font-semibold', className)}>{value}</p>
      <p className="text-[11px] text-muted-foreground mt-0.5">{label}</p>
    </div>
  );
}

function EventRow({ event, timezone }: { event: SessionEventRow; timezone?: string }) {
  const meta = metaFor(event.eventType);
  const Icon = meta.icon;
  const count = event.count || 1;
  const span =
    count > 1 && event.firstAt && event.lastAt && event.firstAt !== event.lastAt
      ? `${formatTimeInZone(event.firstAt, timezone)} – ${formatTimeInZone(event.lastAt, timezone)}`
      : null;

  return (
    <li className="relative pb-5 last:pb-0 pl-5">
      <span
        className={cn(
          'absolute -left-[13px] top-0.5 flex h-6 w-6 items-center justify-center rounded-full border',
          meta.className,
        )}
      >
        <Icon className="h-3.5 w-3.5" />
      </span>
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-sm font-medium text-foreground leading-6">{meta.label}</p>
          <p className="text-xs text-muted-foreground">{meta.hint}</p>
          {span && (
            <p className="mt-1 text-[11px] font-mono text-muted-foreground">
              {count} events · {span}
            </p>
          )}
        </div>
        <time className="shrink-0 font-mono text-xs font-medium text-foreground tabular-nums">
          {formatTimeInZone(event.eventAt, timezone)}
        </time>
      </div>
    </li>
  );
}
