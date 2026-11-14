'use client';

import { Suspense, useEffect, useRef, useState } from 'react';
import { keepPreviousData, useInfiniteQuery } from '@tanstack/react-query';
import { Loader2, ScanFace } from 'lucide-react';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import ActivityFilters from '@/components/journey/ActivityFilters';
import EmployeeSelector from '@/components/EmployeeSelector';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { faceVerifyApi, type FaceVerifyEvent } from '@/lib/api';
import { poseShortLabel } from '@/lib/face-enrollment';
import { formatDateTime, formatRelative } from '@/lib/format';
import { useUrlActivityFilter } from '@/hooks/use-url-activity-filter';

const PER_PAGE = 30;

function resultBadge(result: string): string {
  switch (result) {
    case 'verified':
      return 'bg-success/15 text-success';
    case 'failed':
    case 'no_face':
    case 'align_failed':
      return 'bg-destructive/15 text-destructive';
    case 'camera_error':
    case 'model_error':
      return 'bg-warning/15 text-warning';
    default:
      return 'bg-muted text-muted-foreground';
  }
}

function FaceVerifyInner() {
  const { filter, setFilter, extra, setExtra } = useUrlActivityFilter(
    { result: {}, mode: {}, employeeId: {} },
    { result: '', mode: '', employeeId: '' },
    'all',
  );

  const [selected, setSelected] = useState<FaceVerifyEvent | null>(null);

  const query = useInfiniteQuery({
    queryKey: [
      'face-verify-events',
      {
        q: filter.search,
        result: extra.result,
        mode: extra.mode,
        employeeId: extra.employeeId,
        dateFrom: filter.dateFrom,
        dateTo: filter.dateTo,
      },
    ],
    queryFn: ({ pageParam }) =>
      faceVerifyApi.list({
        q: filter.search || undefined,
        result: extra.result || undefined,
        mode: extra.mode || undefined,
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
  }, [query.hasNextPage, query.isFetchingNextPage, query.fetchNextPage]);

  const events = query.data?.pages.flatMap((p) => p.events) ?? [];
  const total = query.data?.pages[0]?.total ?? 0;

  return (
    <div className="flex flex-col gap-4 p-6 max-w-[1400px] mx-auto w-full">
      <div className="flex items-start gap-3">
        <div className="rounded-lg bg-primary/10 p-2.5">
          <ScanFace className="w-6 h-6 text-primary" />
        </div>
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Face Verify</h1>
          <p className="text-sm text-muted-foreground mt-0.5">
            Client face-check activity (first-time multi-angle and simple restart / unlock).
            {total ? ` ${total.toLocaleString()} event${total === 1 ? '' : 's'}.` : ''}
          </p>
        </div>
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <ActivityFilters
          value={filter}
          onChange={setFilter}
          loading={query.isFetching}
          searchPlaceholder="Search employee, machine, detail…"
        />
        <div className="w-[220px]">
          <EmployeeSelector
            value={extra.employeeId || ''}
            allowAll
            allLabel="All employees"
            placeholder="All employees"
            onChange={(emp) =>
              setExtra({ ...extra, employeeId: emp?.employeeId || emp?.id || '' })
            }
          />
        </div>
        <Select
          value={extra.result || 'all'}
          onValueChange={(v) => setExtra({ ...extra, result: v === 'all' ? '' : v })}
        >
          <SelectTrigger className="w-[160px]">
            <SelectValue placeholder="Result" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All results</SelectItem>
            <SelectItem value="verified">Verified</SelectItem>
            <SelectItem value="failed">Failed</SelectItem>
            <SelectItem value="no_face">No face</SelectItem>
            <SelectItem value="camera_error">Camera error</SelectItem>
            <SelectItem value="model_error">Model error</SelectItem>
          </SelectContent>
        </Select>
        <Select
          value={extra.mode || 'all'}
          onValueChange={(v) => setExtra({ ...extra, mode: v === 'all' ? '' : v })}
        >
          <SelectTrigger className="w-[160px]">
            <SelectValue placeholder="Mode" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All modes</SelectItem>
            <SelectItem value="first">First (multi-angle)</SelectItem>
            <SelectItem value="simple">Simple</SelectItem>
          </SelectContent>
        </Select>
      </div>

      <div className="rounded-lg border border-border overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-muted/40 text-muted-foreground">
            <tr>
              <th className="text-left font-medium px-3 py-2">Employee</th>
              <th className="text-left font-medium px-3 py-2">Mode</th>
              <th className="text-left font-medium px-3 py-2">Trigger</th>
              <th className="text-left font-medium px-3 py-2">Pose</th>
              <th className="text-left font-medium px-3 py-2">Result</th>
              <th className="text-left font-medium px-3 py-2">Score</th>
              <th className="text-left font-medium px-3 py-2">When</th>
            </tr>
          </thead>
          <tbody>
            {query.isLoading ? (
              <tr>
                <td colSpan={7} className="px-3 py-10 text-center text-muted-foreground">
                  <Loader2 className="w-5 h-5 animate-spin inline mr-2" />
                  Loading…
                </td>
              </tr>
            ) : events.length === 0 ? (
              <tr>
                <td colSpan={7} className="px-3 py-10 text-center text-muted-foreground">
                  No face verify events yet.
                </td>
              </tr>
            ) : (
              events.map((ev) => (
                <tr
                  key={ev.id}
                  className="border-t border-border hover:bg-muted/30 cursor-pointer"
                  onClick={() => setSelected(ev)}
                >
                  <td className="px-3 py-2">
                    <div className="font-medium">{ev.employeeName || ev.employeeId}</div>
                    <div className="text-xs text-muted-foreground">{ev.employeeId}</div>
                  </td>
                  <td className="px-3 py-2 capitalize">{ev.mode}</td>
                  <td className="px-3 py-2">{ev.trigger || '—'}</td>
                  <td className="px-3 py-2">
                    {ev.poseId ? poseShortLabel(ev.poseId) : '—'}
                  </td>
                  <td className="px-3 py-2">
                    <span className={`inline-flex px-2 py-0.5 rounded text-xs font-medium ${resultBadge(ev.result)}`}>
                      {ev.result}
                    </span>
                  </td>
                  <td className="px-3 py-2 tabular-nums">
                    {ev.score != null ? ev.score.toFixed(3) : '—'}
                  </td>
                  <td className="px-3 py-2">
                    <div>{formatRelative(ev.clientCreatedAt)}</div>
                    <div className="text-xs text-muted-foreground">
                      {formatDateTime(ev.clientCreatedAt)}
                    </div>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
        <div ref={sentinelRef} className="h-8" />
        {query.isFetchingNextPage ? (
          <div className="py-3 text-center text-muted-foreground text-sm">
            <Loader2 className="w-4 h-4 animate-spin inline mr-2" />
            Loading more…
          </div>
        ) : null}
      </div>

      <Dialog open={!!selected} onOpenChange={(o) => !o && setSelected(null)}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>Face verify event</DialogTitle>
          </DialogHeader>
          {selected ? (
            <dl className="grid grid-cols-[120px_1fr] gap-x-3 gap-y-2 text-sm">
              <dt className="text-muted-foreground">Employee</dt>
              <dd>{selected.employeeName || selected.employeeId} ({selected.employeeId})</dd>
              <dt className="text-muted-foreground">Machine</dt>
              <dd className="break-all">{selected.machineId || '—'}</dd>
              <dt className="text-muted-foreground">Mode</dt>
              <dd className="capitalize">{selected.mode}</dd>
              <dt className="text-muted-foreground">Trigger</dt>
              <dd>{selected.trigger || '—'}</dd>
              <dt className="text-muted-foreground">Pose</dt>
              <dd>{selected.poseId ? poseShortLabel(selected.poseId) : '—'}</dd>
              <dt className="text-muted-foreground">Result</dt>
              <dd>
                <span className={`inline-flex px-2 py-0.5 rounded text-xs font-medium ${resultBadge(selected.result)}`}>
                  {selected.result}
                </span>
              </dd>
              <dt className="text-muted-foreground">Score</dt>
              <dd>{selected.score != null ? selected.score.toFixed(4) : '—'}</dd>
              <dt className="text-muted-foreground">Threshold</dt>
              <dd>{selected.threshold != null ? selected.threshold.toFixed(4) : '—'}</dd>
              <dt className="text-muted-foreground">Detail</dt>
              <dd className="break-words">{selected.detail || '—'}</dd>
              <dt className="text-muted-foreground">Client time</dt>
              <dd>{formatDateTime(selected.clientCreatedAt)}</dd>
              <dt className="text-muted-foreground">Synced</dt>
              <dd>{formatDateTime(selected.syncedAt)}</dd>
              <dt className="text-muted-foreground">Event id</dt>
              <dd className="break-all text-xs">{selected.id}</dd>
            </dl>
          ) : null}
        </DialogContent>
      </Dialog>
    </div>
  );
}

export default function FaceVerifyPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[400px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <FaceVerifyInner />
    </Suspense>
  );
}
