'use client';

import { Fragment, Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import {
  ChevronDown,
  ChevronRight,
  Laptop,
  ListTree,
  Loader2,
  Play,
  Search,
  ScrollText,
  Square,
} from 'lucide-react';
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import TrackingModeBadge from '@/components/employees/TrackingModeBadge';
import {
  deviceConfigurationApi,
  type RoamingEmployee,
  type RoamingMachineDetail,
  type TrackingEventLog,
} from '@/lib/api';
import { formatDateTime, formatRelative } from '@/lib/format';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { cn } from '@/lib/utils';

type TabKey = 'roaming' | 'modes' | 'logs';

const TABS: { key: TabKey; label: string; icon: React.ElementType }[] = [
  { key: 'roaming', label: 'Roaming PC list', icon: Laptop },
  { key: 'modes', label: 'Tracker mode types', icon: ListTree },
  { key: 'logs', label: 'Logs', icon: ScrollText },
];

const ROAMING_PER_PAGE = 20;
const LOGS_PER_PAGE = 30;

export default function DeviceConfigurationPage() {
  return (
    <Suspense
      fallback={
        <div className="flex min-h-[400px] items-center justify-center">
          <Loader2 className="h-8 w-8 animate-spin text-primary" />
        </div>
      }
    >
      <DeviceConfigurationInner />
    </Suspense>
  );
}

function DeviceConfigurationInner() {
  const [filters, setFilters] = useUrlQueryState(
    { tab: {}, q: {}, eventType: {} },
    { tab: 'roaming', q: '', eventType: '' },
  );

  const tab = (['roaming', 'modes', 'logs'].includes(filters.tab)
    ? filters.tab
    : 'roaming') as TabKey;

  // Debounced search mirror for the text inputs (URL writes stay snappy).
  const [qLocal, setQLocal] = useState(filters.q);
  useEffect(() => {
    setQLocal(filters.q);
  }, [filters.q]);
  useEffect(() => {
    const t = setTimeout(() => {
      if (qLocal !== filters.q) setFilters({ q: qLocal });
    }, 400);
    return () => clearTimeout(t);
  }, [qLocal, filters.q, setFilters]);

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight text-foreground">
          Device Configuration
        </h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Roaming PCs, Fixed vs Roaming mode types, and Start/Stop tracking logs.
        </p>
      </div>

      <div
        role="tablist"
        aria-label="Device configuration sections"
        className="flex items-center gap-1 overflow-x-auto pb-1 -mx-1 px-1"
      >
        {TABS.map(({ key, label, icon: Icon }) => (
          <button
            key={key}
            type="button"
            role="tab"
            aria-selected={tab === key}
            onClick={() => setFilters({ tab: key })}
            className={cn(
              'flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium whitespace-nowrap transition-all duration-200',
              tab === key
                ? 'bg-primary text-primary-foreground shadow-card-hover'
                : 'text-muted-foreground hover:bg-muted hover:text-foreground',
            )}
          >
            <Icon className="h-4 w-4" />
            {label}
          </button>
        ))}
      </div>

      <motion.div
        key={tab}
        initial={{ opacity: 0, y: 6 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.2 }}
      >
        {tab === 'roaming' && (
          <RoamingPcList search={filters.q} searchLocal={qLocal} onSearchLocal={setQLocal} />
        )}
        {tab === 'modes' && <TrackerModeTypes />}
        {tab === 'logs' && (
          <TrackingLogs
            search={filters.q}
            searchLocal={qLocal}
            onSearchLocal={setQLocal}
            eventType={filters.eventType}
            onEventType={(v) => setFilters({ eventType: v })}
          />
        )}
      </motion.div>
    </div>
  );
}

function SearchBox({
  value,
  onChange,
  placeholder,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder: string;
}) {
  return (
    <div className="flex max-w-sm items-center gap-2 rounded-lg border border-border bg-card px-3 py-2">
      <Search className="h-4 w-4 text-muted-foreground" />
      <input
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        className="flex-1 border-none bg-transparent text-sm text-foreground outline-none placeholder:text-muted-foreground"
      />
    </div>
  );
}

function InfiniteSentinel({
  hasMore,
  loading,
  onVisible,
}: {
  hasMore: boolean;
  loading: boolean;
  onVisible: () => void;
}) {
  const ref = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = ref.current;
    if (!el || !hasMore) return;
    const obs = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) onVisible();
      },
      { rootMargin: '200px' },
    );
    obs.observe(el);
    return () => obs.disconnect();
  }, [hasMore, onVisible]);
  return (
    <div ref={ref} className="flex justify-center py-4 text-sm text-muted-foreground">
      {loading ? (
        <span className="inline-flex items-center gap-2">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading more…
        </span>
      ) : null}
    </div>
  );
}

function RoamingPcList({
  search,
  searchLocal,
  onSearchLocal,
}: {
  search: string;
  searchLocal: string;
  onSearchLocal: (v: string) => void;
}) {
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const toggle = (employeeId: string) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(employeeId)) next.delete(employeeId);
      else next.add(employeeId);
      return next;
    });
  };

  const query = useInfiniteQuery({
    // v2 = employee-grouped payload (`employees[]`); busts any cached flat `machines[]`.
    queryKey: ['device-config-roaming-v2', search],
    queryFn: ({ pageParam }) =>
      deviceConfigurationApi.roamingMachines({
        page: pageParam,
        perPage: ROAMING_PER_PAGE,
        search: search || undefined,
      }),
    initialPageParam: 1,
    getNextPageParam: (last) =>
      last.page < last.totalPages ? last.page + 1 : undefined,
    placeholderData: keepPreviousData,
  });

  const employees = useMemo(() => {
    const pages = query.data?.pages ?? [];
    return pages.flatMap((p) => p?.employees ?? []).filter(
      (emp): emp is NonNullable<typeof emp> => !!emp?.employeeId,
    );
  }, [query.data]);
  const total = query.data?.pages[0]?.total ?? 0;

  return (
    <div className="space-y-4">
      <SearchBox
        value={searchLocal}
        onChange={onSearchLocal}
        placeholder="Search employee, hostname, machine…"
      />

      {query.isLoading ? (
        <div className="flex min-h-[200px] items-center justify-center">
          <Loader2 className="h-6 w-6 animate-spin text-primary" />
        </div>
      ) : employees.length === 0 ? (
        <EmptyBlock
          title="No roaming PCs"
          body="Machines set to Roaming (via Employees → Set tracking mode) appear here."
        />
      ) : (
        <div className="overflow-hidden rounded-lg border border-border">
          <table className="w-full text-sm">
            <thead className="bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
              <tr>
                <th className="px-4 py-3 font-medium">Employee</th>
                <th className="px-4 py-3 font-medium">Current device</th>
                <th className="px-4 py-3 font-medium">Mode</th>
                <th className="px-4 py-3 font-medium">State</th>
                <th className="px-4 py-3 font-medium">Last tracked</th>
              </tr>
            </thead>
            <tbody>
              {employees.map((emp) => (
                <RoamingEmployeeRows
                  key={emp.employeeId}
                  emp={emp}
                  expanded={expanded.has(emp.employeeId)}
                  onToggle={() => toggle(emp.employeeId)}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}

      <InfiniteSentinel
        hasMore={!!query.hasNextPage}
        loading={query.isFetchingNextPage}
        onVisible={() => {
          if (query.hasNextPage && !query.isFetchingNextPage) void query.fetchNextPage();
        }}
      />
      {!query.hasNextPage && employees.length > 0 && (
        <p className="text-center text-xs text-muted-foreground">Showing all {total}</p>
      )}
    </div>
  );
}

function deviceLabel(hostname: string, machineId: string, employeeName: string) {
  if (hostname) return `${employeeName} (${hostname})`;
  return `${employeeName} (${machineId})`;
}

function RoamingEmployeeRows({
  emp,
  expanded,
  onToggle,
}: {
  emp: RoamingEmployee;
  expanded: boolean;
  onToggle: () => void;
}) {
  const cur = emp.current;
  const history = emp.history ?? [];
  const expandable = history.length > 0;

  return (
    <Fragment>
      <tr className="border-t border-border hover:bg-muted/30">
        <td className="px-4 py-3">
          <div
            className={cn('flex items-start gap-2', expandable && 'cursor-pointer select-none')}
            onClick={expandable ? onToggle : undefined}
            title={
              expandable
                ? expanded
                  ? 'Hide previous devices'
                  : `Show ${history.length} previous device${history.length === 1 ? '' : 's'}`
                : undefined
            }
          >
            {expandable ? (
              expanded ? (
                <ChevronDown className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
              ) : (
                <ChevronRight className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
              )
            ) : (
              <span className="mt-0.5 inline-block w-4 shrink-0" />
            )}
            <div className="min-w-0">
              <div className="font-medium text-foreground">{emp.employeeName}</div>
              <div className="text-xs text-muted-foreground">
                {emp.employeeId}
                {emp.department ? ` · ${emp.department}` : ''}
                {expandable ? ` · ${history.length} previous` : ''}
              </div>
            </div>
          </div>
        </td>
        <MachineCells machine={cur} employeeName={emp.employeeName} />
      </tr>
      {expanded &&
        history.map((h) => (
          <tr
            key={`${emp.employeeId}:${h.machineId}`}
            className="border-t border-border bg-muted/20"
          >
            <td className="px-4 py-2.5 pl-12 text-xs text-muted-foreground">
              Previous device
            </td>
            <MachineCells machine={h} employeeName={emp.employeeName} history />
          </tr>
        ))}
    </Fragment>
  );
}

function MachineCells({
  machine,
  employeeName,
  history,
}: {
  machine: RoamingMachineDetail;
  employeeName: string;
  history?: boolean;
}) {
  return (
    <>
      <td className={cn('px-4', history ? 'py-2.5' : 'py-3')}>
        <div className="text-foreground">
          {deviceLabel(machine.hostname, machine.machineId, employeeName)}
        </div>
        <div className="font-mono text-[11px] text-muted-foreground">{machine.machineId}</div>
        {machine.status === 'old' && (
          <span className="mt-1 inline-block rounded bg-muted px-1.5 py-0.5 text-[10px] uppercase text-muted-foreground">
            old · removed
          </span>
        )}
        {machine.status === 'active' && !history && (
          <span className="mt-1 inline-block rounded bg-emerald-500/10 px-1.5 py-0.5 text-[10px] uppercase text-emerald-700 dark:text-emerald-300">
            active
          </span>
        )}
      </td>
      <td className={cn('px-4', history ? 'py-2.5' : 'py-3')}>
        <TrackingModeBadge mode="roaming" source={machine.effectiveModeSource} showSource />
      </td>
      <td className={cn('px-4', history ? 'py-2.5' : 'py-3')}>
        <div className="flex flex-wrap gap-1">
          <StateChip value={machine.desiredState} />
          {machine.phase ? <StateChip value={machine.phase} muted /> : null}
        </div>
      </td>
      <td className={cn('px-4 text-muted-foreground', history ? 'py-2.5' : 'py-3')}>
        {machine.lastTrackAt ? (
          <>
            <div>{formatRelative(machine.lastTrackAt)}</div>
            <div className="text-[11px]">{formatDateTime(machine.lastTrackAt)}</div>
          </>
        ) : (
          '—'
        )}
      </td>
    </>
  );
}

function StateChip({ value, muted }: { value: string; muted?: boolean }) {
  const stopped = value === 'stopped';
  return (
    <span
      className={cn(
        'inline-flex rounded-md border px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide',
        muted
          ? 'border-border text-muted-foreground'
          : stopped
            ? 'border-amber-500/30 bg-amber-500/10 text-amber-800 dark:text-amber-300'
            : 'border-border bg-muted text-muted-foreground',
      )}
    >
      {value}
    </span>
  );
}

function TrackerModeTypes() {
  const query = useQuery({
    queryKey: ['device-config-mode-types'],
    queryFn: () => deviceConfigurationApi.modeTypes(),
  });

  if (query.isLoading) {
    return (
      <div className="flex min-h-[200px] items-center justify-center">
        <Loader2 className="h-6 w-6 animate-spin text-primary" />
      </div>
    );
  }
  if (query.isError || !query.data) {
    return (
      <EmptyBlock title="Could not load mode types" body="Retry after the API is reachable." />
    );
  }

  const { modes, orgDefaultMode, orgStopWindow, keepDlpWhenStopped } = query.data;

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">
        Organization default:{' '}
        <span className="font-medium text-foreground capitalize">{orgDefaultMode}</span>
        {' · '}
        stop window <span className="font-medium text-foreground">{orgStopWindow}</span>
        {' · '}
        keep DLP when stopped:{' '}
        <span className="font-medium text-foreground">{keepDlpWhenStopped ? 'yes' : 'no'}</span>
      </p>

      <div className="grid gap-4 md:grid-cols-2">
        {modes.map((mode) => (
          <div
            key={mode.mode}
            className="rounded-lg border border-border bg-card p-5 space-y-3"
          >
            <div className="flex items-start justify-between gap-3">
              <div className="space-y-1">
                <div className="flex items-center gap-2">
                  <TrackingModeBadge mode={mode.mode === 'roaming' ? 'roaming' : 'fixed'} />
                  {mode.orgDefault && (
                    <span className="rounded-md bg-primary/10 px-1.5 py-0.5 text-[10px] font-semibold uppercase text-primary">
                      Org default
                    </span>
                  )}
                </div>
                <h2 className="text-lg font-semibold text-foreground">{mode.label}</h2>
                <p className="text-sm text-muted-foreground">{mode.summary}</p>
              </div>
            </div>
            <p className="text-sm leading-relaxed text-foreground/90">{mode.details}</p>
            <p className="text-xs text-muted-foreground">
              Employee Start/Stop:{' '}
              <span className="font-medium text-foreground">
                {mode.canStop ? 'allowed' : 'blocked'}
              </span>
            </p>
          </div>
        ))}
      </div>
    </div>
  );
}

function TrackingLogs({
  search,
  searchLocal,
  onSearchLocal,
  eventType,
  onEventType,
}: {
  search: string;
  searchLocal: string;
  onSearchLocal: (v: string) => void;
  eventType: string;
  onEventType: (v: string) => void;
}) {
  const query = useInfiniteQuery({
    queryKey: ['device-config-logs', search, eventType],
    queryFn: ({ pageParam }) =>
      deviceConfigurationApi.trackingEvents({
        page: pageParam,
        perPage: LOGS_PER_PAGE,
        search: search || undefined,
        eventType: eventType || undefined,
      }),
    initialPageParam: 1,
    getNextPageParam: (last) =>
      last.page < last.totalPages ? last.page + 1 : undefined,
    placeholderData: keepPreviousData,
  });

  const events = useMemo(
    () => query.data?.pages.flatMap((p) => p.events) ?? [],
    [query.data],
  );
  const total = query.data?.pages[0]?.total ?? 0;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <SearchBox
          value={searchLocal}
          onChange={onSearchLocal}
          placeholder="Search employee or machine…"
        />
        <Select
          value={eventType || 'all'}
          onValueChange={(v) => onEventType(v === 'all' ? '' : v)}
        >
          <SelectTrigger className="w-[160px]">
            <SelectValue placeholder="Event type" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All events</SelectItem>
            <SelectItem value="start">Start</SelectItem>
            <SelectItem value="stop">Stop</SelectItem>
          </SelectContent>
        </Select>
      </div>

      {query.isLoading ? (
        <div className="flex min-h-[200px] items-center justify-center">
          <Loader2 className="h-6 w-6 animate-spin text-primary" />
        </div>
      ) : events.length === 0 ? (
        <EmptyBlock
          title="No tracking logs yet"
          body="Start/Stop clicks from Roaming clients appear here after the desktop app reports them."
        />
      ) : (
        <div className="overflow-hidden rounded-lg border border-border">
          <table className="w-full text-sm">
            <thead className="bg-muted/40 text-left text-xs uppercase tracking-wide text-muted-foreground">
              <tr>
                <th className="px-4 py-3 font-medium">When</th>
                <th className="px-4 py-3 font-medium">Event</th>
                <th className="px-4 py-3 font-medium">Employee / device</th>
                <th className="px-4 py-3 font-medium">Reason / flush</th>
                <th className="px-4 py-3 font-medium">Client</th>
              </tr>
            </thead>
            <tbody>
              {events.map((e) => (
                <LogRow key={e.id} e={e} />
              ))}
            </tbody>
          </table>
        </div>
      )}

      <InfiniteSentinel
        hasMore={!!query.hasNextPage}
        loading={query.isFetchingNextPage}
        onVisible={() => {
          if (query.hasNextPage && !query.isFetchingNextPage) void query.fetchNextPage();
        }}
      />
      {!query.hasNextPage && events.length > 0 && (
        <p className="text-center text-xs text-muted-foreground">Showing all {total}</p>
      )}
    </div>
  );
}

function LogRow({ e }: { e: TrackingEventLog }) {
  const isStart = e.eventType === 'start';
  const device = e.hostname
    ? `${e.employeeName} (${e.hostname})`
    : `${e.employeeName} (${e.machineId})`;
  return (
    <tr className="border-t border-border hover:bg-muted/30">
      <td className="px-4 py-3 text-muted-foreground">
        <div className="text-foreground">{formatRelative(e.occurredAt)}</div>
        <div className="text-[11px]">{formatDateTime(e.occurredAt)}</div>
      </td>
      <td className="px-4 py-3">
        <span
          className={cn(
            'inline-flex items-center gap-1 rounded-md border px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide',
            isStart
              ? 'border-emerald-500/30 bg-emerald-500/10 text-emerald-800 dark:text-emerald-300'
              : 'border-amber-500/30 bg-amber-500/10 text-amber-800 dark:text-amber-300',
          )}
        >
          {isStart ? <Play className="h-3 w-3" /> : <Square className="h-3 w-3" />}
          {e.eventType}
        </span>
      </td>
      <td className="px-4 py-3">
        <div className="font-medium text-foreground">{device}</div>
        <div className="text-xs text-muted-foreground">
          {e.employeeId} · <span className="font-mono">{e.machineId}</span>
        </div>
      </td>
      <td className="px-4 py-3 text-muted-foreground">
        <div>{e.reason || '—'}</div>
        {e.flushResult && (
          <div className="text-[11px]">flush: {e.flushResult}</div>
        )}
      </td>
      <td className="px-4 py-3 text-muted-foreground">{e.clientVersion || '—'}</td>
    </tr>
  );
}

function EmptyBlock({ title, body }: { title: string; body: string }) {
  return (
    <div className="rounded-lg border border-dashed border-border px-6 py-12 text-center">
      <p className="font-medium text-foreground">{title}</p>
      <p className="mt-1 text-sm text-muted-foreground">{body}</p>
    </div>
  );
}
