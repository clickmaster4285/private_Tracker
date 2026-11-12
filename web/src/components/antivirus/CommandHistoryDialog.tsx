'use client';

import { useEffect, useRef, useState } from 'react';
import { keepPreviousData, useInfiniteQuery } from '@tanstack/react-query';
import { History, Loader2 } from 'lucide-react';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import {
  antivirusApi,
  type AntivirusCommandRow,
  type AntivirusCommandStatus,
  type AntivirusStatusRow,
} from '@/lib/api';
import { formatDateTime, formatRelative } from '@/lib/format';
import { COMMAND_INFO, COMMAND_STATUS } from './labels';

const STATUS_CHIPS: { key: AntivirusCommandStatus | ''; label: string }[] = [
  { key: '', label: 'All' },
  { key: 'pending', label: 'Waiting' },
  { key: 'running', label: 'Running' },
  { key: 'done', label: 'Finished' },
  { key: 'failed', label: 'Failed' },
  { key: 'expired', label: 'Expired' },
];

function duration(c: AntivirusCommandRow): string {
  if (!c.dispatchedAt || !c.completedAt) return '';
  const s = Math.max(0, (new Date(c.completedAt).getTime() - new Date(c.dispatchedAt).getTime()) / 1000);
  if (s < 60) return `${Math.round(s)}s`;
  if (s < 3600) return `${Math.round(s / 60)} min`;
  return `${(s / 3600).toFixed(1)} h`;
}

/** Every remote antivirus command sent to one PC: who asked, when, and what happened. */
export function CommandHistoryDialog({
  row,
  open,
  onOpenChange,
}: {
  row: AntivirusStatusRow;
  open: boolean;
  onOpenChange: (v: boolean) => void;
}) {
  const [status, setStatus] = useState<AntivirusCommandStatus | ''>('');

  const query = useInfiniteQuery({
    queryKey: ['antivirus-commands', row.employeeId, row.machineId, status],
    queryFn: ({ pageParam }) =>
      antivirusApi.listCommands({
        employeeId: row.employeeId,
        machineId: row.machineId,
        status,
        page: pageParam,
        perPage: 20,
      }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
    placeholderData: keepPreviousData,
    enabled: open,
    // Live progress while the dialog is open.
    refetchInterval: open ? 10_000 : false,
  });

  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el || !open) return;
    const obs = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && query.hasNextPage && !query.isFetchingNextPage) {
          query.fetchNextPage();
        }
      },
      { rootMargin: '200px' },
    );
    obs.observe(el);
    return () => obs.disconnect();
  }, [open, query.hasNextPage, query.isFetchingNextPage, query.fetchNextPage]);

  const rows = query.data?.pages.flatMap((p) => p.data) ?? [];
  const total = query.data?.pages[0]?.total ?? 0;
  const pc = `${row.employeeName || row.employeeId} (${row.hostname || row.machineId || 'unknown machine'})`;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-3xl max-h-[85vh] flex flex-col">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <History className="w-4 h-4" /> Command history — {pc}
          </DialogTitle>
        </DialogHeader>
        <p className="text-xs text-muted-foreground">
          Every scan or clean-up sent to this PC from the dashboard. A command waits until the PC is
          on and checks in, then runs and reports back. This list refreshes every 10 seconds.
        </p>

        <div className="flex flex-wrap gap-1.5">
          {STATUS_CHIPS.map((c) => (
            <button
              key={c.key || 'all'}
              type="button"
              onClick={() => setStatus(c.key)}
              className={`px-2.5 py-1 rounded-full text-xs font-medium border ${
                status === c.key
                  ? 'bg-primary text-primary-foreground border-primary'
                  : 'bg-card text-muted-foreground border-border hover:bg-muted/40'
              }`}
            >
              {c.label}
            </button>
          ))}
        </div>

        <div className="overflow-y-auto flex-1 -mx-1 px-1">
          {query.isLoading && (
            <div className="flex justify-center py-8"><Loader2 className="w-5 h-5 animate-spin text-primary" /></div>
          )}
          {query.isError && <p className="text-sm text-destructive py-4">Failed to load command history.</p>}
          {!query.isLoading && rows.length === 0 && (
            <p className="text-sm text-muted-foreground text-center py-8">
              No commands {status ? 'with this status ' : ''}have been sent to this PC yet.
            </p>
          )}
          <ul className="space-y-2">
            {rows.map((c) => {
              const info = COMMAND_INFO[c.type] ?? { label: c.type, help: '' };
              const st = COMMAND_STATUS[c.status] ?? COMMAND_STATUS.pending;
              const took = duration(c);
              return (
                <li key={c.id} className="border border-border rounded-lg p-3 text-sm">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium text-foreground" title={info.help}>{info.label}</span>
                    <span title={st.help} className={`px-2 py-0.5 rounded-full text-[11px] font-medium ${st.cls}`}>
                      {c.status === 'running' && <Loader2 className="w-3 h-3 inline mr-1 animate-spin" />}
                      {st.label}
                    </span>
                    <span className="text-xs text-muted-foreground ml-auto" title={formatDateTime(c.createdAt)}>
                      {formatRelative(c.createdAt)}
                    </span>
                  </div>
                  {c.arg && (
                    <div className="text-xs mt-1">
                      <span className="text-muted-foreground">Target: </span>
                      <span className="font-mono break-all">{c.arg}</span>
                    </div>
                  )}
                  <div className="text-xs text-muted-foreground mt-1 flex flex-wrap gap-x-4 gap-y-0.5">
                    <span>Requested by {c.requestedByName || 'unknown user'}</span>
                    <span>Queued {formatDateTime(c.createdAt)}</span>
                    {c.dispatchedAt && <span>Started {formatDateTime(c.dispatchedAt)}</span>}
                    {c.completedAt && <span>Ended {formatDateTime(c.completedAt)}{took && ` (${took})`}</span>}
                  </div>
                  {c.status === 'pending' && (
                    <p className="text-xs text-muted-foreground mt-1">{st.help}</p>
                  )}
                  {c.result && (
                    <div
                      className={`text-xs mt-2 rounded px-2 py-1 font-mono break-all ${
                        c.status === 'failed' ? 'bg-destructive/10 text-destructive' : 'bg-muted/50 text-foreground'
                      }`}
                    >
                      {c.result}
                    </div>
                  )}
                </li>
              );
            })}
          </ul>
          <div ref={sentinelRef} className="h-8 flex items-center justify-center text-xs text-muted-foreground">
            {query.isFetchingNextPage && <Loader2 className="w-4 h-4 animate-spin" />}
            {!query.hasNextPage && rows.length > 0 && `Showing all ${total}`}
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}
