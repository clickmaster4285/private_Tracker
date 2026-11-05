'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import {
  ChevronRight,
  Loader2,
  ShieldCheck,
} from 'lucide-react';
import { keepPreviousData, useInfiniteQuery } from '@tanstack/react-query';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import ActivityFilters from '@/components/journey/ActivityFilters';
import DlpAlertDetailDialog from '@/components/dlp/DlpAlertDetailDialog';
import EmployeeSelector from '@/components/EmployeeSelector';
import { dlpApi, type DlpAlert } from '@/lib/api';
import { useUrlActivityFilter } from '@/hooks/use-url-activity-filter';
import {
  actionLabel,
  alertTypeLabel,
  evidenceDomainLabel,
  evidenceSummary,
  severityLabel,
  statusLabel,
} from '@/lib/dlp-labels';
import { formatDateTime, formatRelative } from '@/lib/format';

const PER_PAGE = 30;

const SEVERITY_BADGE: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive',
  high: 'bg-warning/15 text-warning',
  medium: 'bg-info/15 text-info',
  low: 'bg-muted text-muted-foreground',
};

const STATUS_BADGE: Record<string, string> = {
  open: 'bg-destructive/15 text-destructive',
  investigating: 'bg-warning/15 text-warning',
  resolved: 'bg-success/15 text-success',
  false_positive: 'bg-muted text-muted-foreground',
};

/** Status chip → count + accent. The count is the number under the OTHER active filters. */
function useStatusCounts(counts?: {
  open: number;
  investigating: number;
  resolved: number;
  falsePositive: number;
}) {
  return useMemo(
    () => [
      { key: 'open' as const, label: 'Open', count: counts?.open ?? 0, color: 'text-destructive' },
      {
        key: 'investigating' as const,
        label: 'Investigating',
        count: counts?.investigating ?? 0,
        color: 'text-warning',
      },
      { key: 'resolved' as const, label: 'Resolved', count: counts?.resolved ?? 0, color: 'text-success' },
      {
        key: 'false_positive' as const,
        label: 'False positive',
        count: counts?.falsePositive ?? 0,
        color: 'text-muted-foreground',
      },
    ],
    [counts],
  );
}

function DLPAlertsInner() {
  // ONE URL hook for the whole page: the activity filter (search + date)
  // plus the sibling keys (status, severity, type, employee). Same
  // instance → no race between the filter and the dropdowns.
  //
  // Default preset is "all" — DLP incidents are rare and historical, so
  // defaulting to "today" would show an empty list on first paint.
  const { filter, setFilter, extra, setExtra } = useUrlActivityFilter(
    { status: {}, severity: {}, type: {}, employeeId: {} },
    { status: '', severity: '', type: '', employeeId: '' },
    'all',
  );

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [detailOpen, setDetailOpen] = useState(false);
  // Snapshot of the clicked row. The dialog prefers the LIVE row from the
  // query cache (so a status change made inside the dialog is reflected
  // immediately) and falls back to this snapshot when the row drops out of
  // the current result set (e.g. the user filtered by "Open" and then
  // marked the alert "Resolved").
  const [snapshot, setSnapshot] = useState<DlpAlert | null>(null);

  const query = useInfiniteQuery({
    queryKey: [
      'dlp-alerts',
      {
        q: filter.search,
        status: extra.status,
        severity: extra.severity,
        type: extra.type,
        employeeId: extra.employeeId,
        dateFrom: filter.dateFrom,
        dateTo: filter.dateTo,
      },
    ],
    queryFn: ({ pageParam }) =>
      dlpApi.alerts.list({
        q: filter.search || undefined,
        status: extra.status || undefined,
        severity: extra.severity || undefined,
        type: extra.type || undefined,
        employeeId: extra.employeeId || undefined,
        dateFrom: filter.preset === 'all' ? undefined : filter.dateFrom,
        dateTo: filter.preset === 'all' ? undefined : filter.dateTo,
        page: pageParam,
        perPage: PER_PAGE,
      }),
    initialPageParam: 1,
    getNextPageParam: (last) =>
      last.page * last.perPage < last.total ? last.page + 1 : undefined,
    placeholderData: keepPreviousData,
  });

  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const obs = new IntersectionObserver((entries) => {
      if (entries[0]?.isIntersecting && query.hasNextPage && !query.isFetchingNextPage) {
        query.fetchNextPage();
      }
    }, { rootMargin: '200px' });
    obs.observe(el);
    return () => obs.disconnect();
  }, [query.hasNextPage, query.isFetchingNextPage, query.fetchNextPage]);

  const pages = query.data?.pages ?? [];
  const alerts = pages.flatMap((p) => p.alerts);
  const counts = pages[0];
  const statusCounts = useStatusCounts(counts);
  const types = counts?.types ?? [];

  const openDetail = (alert: DlpAlert) => {
    setSelectedId(alert.id);
    setSnapshot(alert);
    setDetailOpen(true);
  };

  // The live row wins over the snapshot (a status edit inside the dialog
  // invalidates the list and re-fetches the row).
  const selected = useMemo(() => {
    if (!selectedId) return null;
    return alerts.find((a) => a.id === selectedId) ?? snapshot;
  }, [alerts, selectedId, snapshot]);

  const totalFiltered = counts?.total ?? alerts.length;
  // The page's date default is "all", so only a NON-default preset counts as
  // an active filter.
  const hasFilters =
    Boolean(filter.search) ||
    Boolean(extra.status) ||
    Boolean(extra.severity) ||
    Boolean(extra.type) ||
    Boolean(extra.employeeId) ||
    filter.preset !== 'all';

  return (
    <div className="space-y-4 animate-fade-in">
      {/* ── Summary tiles (also status filters) ───────────────── */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        {statusCounts.map((s) => {
          const active = extra.status === s.key;
          return (
            <button
              key={s.key}
              type="button"
              onClick={() => setExtra({ status: active ? '' : s.key })}
              className={`bg-card rounded-xl border p-4 text-center transition-colors ${
                active ? 'border-primary ring-2 ring-primary/20' : 'border-border hover:bg-muted/30'
              }`}
            >
              <p className={`text-2xl font-display font-bold ${s.color}`}>{s.count}</p>
              <p className="text-xs text-muted-foreground mt-1">{s.label}</p>
            </button>
          );
        })}
      </div>

      {/* ── Filters ────────────────────────────────────────────── */}
      <div className="bg-card rounded-xl border border-border px-4 py-3 space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <ActivityFilters
            value={filter}
            onChange={setFilter}
            loading={query.isFetching}
            searchPlaceholder="Search alerts, employees, evidence…"
          />
          <EmployeeSelector
            value={extra.employeeId}
            onChange={(emp) => setExtra({ employeeId: emp?.employeeId ?? '' })}
            placeholder="All employees"
            allowAll
            allLabel="All employees"
            className="w-full sm:w-64"
          />
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <FilterSelect
            label="Type"
            value={extra.type}
            onChange={(v) => setExtra({ type: v })}
            allLabel="All types"
            options={types.map((t) => ({ value: t, label: alertTypeLabel(t) }))}
          />
          <FilterSelect
            label="Severity"
            value={extra.severity}
            onChange={(v) => setExtra({ severity: v })}
            allLabel="Any severity"
            options={(['critical', 'high', 'medium', 'low'] as const).map((s) => ({
              value: s,
              label: severityLabel(s),
            }))}
          />
          <FilterSelect
            label="Status"
            value={extra.status}
            onChange={(v) => setExtra({ status: v })}
            allLabel="Any status"
            options={statusCounts.map((s) => ({ value: s.key, label: s.label }))}
          />
          {hasFilters && (
            <span className="text-xs text-muted-foreground ml-auto">
              {totalFiltered} alert{totalFiltered === 1 ? '' : 's'} match
            </span>
          )}
        </div>
      </div>

      {/* ── Alert list ─────────────────────────────────────────── */}
      {query.isLoading && alerts.length === 0 ? (
        <div className="flex justify-center py-16">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : query.isError ? (
        <p className="text-sm text-destructive">Failed to load alerts</p>
      ) : alerts.length === 0 ? (
        <div className="bg-card rounded-xl border border-border py-14 text-center px-6">
          <ShieldCheck className="w-8 h-8 text-muted-foreground mx-auto mb-3" />
          <p className="text-sm font-medium text-foreground">
            {hasFilters ? 'No alerts match these filters' : 'No DLP alerts yet'}
          </p>
          <p className="text-xs text-muted-foreground mt-1">
            {hasFilters
              ? 'Try widening the date range or clearing a filter.'
              : 'Windows agents sync incidents here once the DLP policy is published.'}
          </p>
        </div>
      ) : (
        <div className="bg-card rounded-xl border border-border divide-y divide-border overflow-hidden">
          {alerts.map((a, i) => {
            const evidence = evidenceSummary(a.evidence);
            const blockedDomain = evidenceDomainLabel(a.evidence);
            return (
              <motion.button
                key={a.id}
                type="button"
                onClick={() => openDetail(a)}
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                transition={{ delay: Math.min(i, 20) * 0.015 }}
                className="w-full text-left px-4 py-3 hover:bg-muted/30 transition-colors flex items-start gap-3"
              >
                {/* Severity rail */}
                <span
                  className={`mt-1 w-1 h-9 rounded-full shrink-0 ${
                    a.severity === 'critical'
                      ? 'bg-destructive'
                      : a.severity === 'high'
                        ? 'bg-warning'
                        : a.severity === 'medium'
                          ? 'bg-info'
                          : 'bg-muted-foreground/40'
                  }`}
                />

                {/* What happened */}
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2 flex-wrap">
                    <span className="text-sm font-medium text-foreground">
                      {alertTypeLabel(a.type)}
                    </span>
                    {blockedDomain && (
                      <code className="px-1.5 py-0.5 rounded bg-muted text-[11px] font-mono text-foreground max-w-[14rem] truncate">
                        {blockedDomain}
                      </code>
                    )}
                    <span
                      className={`px-2 py-0.5 rounded-full text-[11px] font-medium ${SEVERITY_BADGE[a.severity] ?? ''}`}
                    >
                      {severityLabel(a.severity)}
                    </span>
                    <span
                      className={`px-2 py-0.5 rounded-full text-[11px] font-medium ${STATUS_BADGE[a.status] ?? ''}`}
                    >
                      {statusLabel(a.status)}
                    </span>
                  </div>
                  <p className="text-xs text-muted-foreground mt-1 truncate">
                    {[
                      a.employeeName || a.employeeId,
                      actionLabel(a.actionTaken),
                      !blockedDomain && evidence ? evidence : null,
                    ]
                      .filter(Boolean)
                      .join(' · ')}
                  </p>
                </div>

                {/* When */}
                <div className="text-right shrink-0 hidden sm:block">
                  <p className="text-xs text-muted-foreground">{formatRelative(a.occurredAt)}</p>
                  <p className="text-[11px] text-muted-foreground/70">{formatDateTime(a.occurredAt)}</p>
                </div>

                <ChevronRight className="w-4 h-4 text-muted-foreground shrink-0 mt-1" />
              </motion.button>
            );
          })}
        </div>
      )}

      <div ref={sentinelRef} className="h-8 flex items-center justify-center text-xs text-muted-foreground">
        {query.isFetchingNextPage && <Loader2 className="w-4 h-4 animate-spin" />}
        {!query.hasNextPage && alerts.length > 0 && `Showing all ${counts?.total ?? alerts.length}`}
      </div>

      <DlpAlertDetailDialog
        alert={selected}
        open={detailOpen}
        onOpenChange={(open) => {
          setDetailOpen(open);
          if (!open) {
            setSelectedId(null);
            setSnapshot(null);
          }
        }}
      />
    </div>
  );
}

/** Compact label + dropdown filter. */
function FilterSelect({
  label,
  value,
  onChange,
  allLabel,
  options,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  allLabel: string;
  options: { value: string; label: string }[];
}) {
  return (
    <div className="flex items-center gap-1.5">
      <span className="text-xs text-muted-foreground">{label}</span>
      <Select value={value || 'all'} onValueChange={(v) => onChange(v === 'all' ? '' : v)}>
        <SelectTrigger className="h-8 text-xs w-40">
          <SelectValue placeholder={allLabel} />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="all">{allLabel}</SelectItem>
          {options.map((o) => (
            <SelectItem key={o.value} value={o.value}>
              {o.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}

export default function DLPAlertsPage() {
  return (
    <Suspense fallback={<div className="flex justify-center py-16"><Loader2 className="w-6 h-6 animate-spin text-primary" /></div>}>
      <DLPAlertsInner />
    </Suspense>
  );
}
