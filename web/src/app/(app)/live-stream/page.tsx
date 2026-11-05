'use client';

import { Suspense, useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ExternalLink, Loader2 } from 'lucide-react';
import { liveStreamApi, type LiveStreamEmployee } from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { LiveStreamEmployeeSidebar } from '@/components/live-stream/LiveStreamEmployeeSidebar';
import { LiveStreamPreviewGrid } from '@/components/live-stream/LiveStreamPreviewGrid';
import {
  LIVE_STREAM_MAX_TILES,
  parseLiveStreamIds,
  toggleLiveStreamId,
} from '@/components/live-stream/live-stream-selection';

export default function LiveStreamPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[400px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <LiveStreamInner />
    </Suspense>
  );
}

function LiveStreamInner() {
  const [filters, setFilters] = useUrlQueryState(
    { ids: {}, q: {} },
    { ids: '', q: '' },
    { debounceMs: 0, history: 'replace' },
  );
  const [searchInput, setSearchInput] = useState(filters.q);
  useEffect(() => {
    const t = setTimeout(() => {
      if (searchInput !== filters.q) setFilters({ q: searchInput });
    }, 400);
    return () => clearTimeout(t);
  }, [searchInput, filters.q, setFilters]);
  useEffect(() => {
    setSearchInput(filters.q);
  }, [filters.q]);

  const selectedIds = useMemo(
    () => parseLiveStreamIds(filters.ids),
    [filters.ids],
  );

  const { data, isLoading, isError } = useQuery({
    queryKey: ['live-stream-employees'],
    queryFn: () => liveStreamApi.employees(),
    // Shared queryKey with theater — same-tab components dedupe. staleTime avoids
    // back-to-back fetches when both mounts share the cache window.
    refetchInterval: 2_000,
    staleTime: 1_000,
  });

  const employees = useMemo(() => {
    const list = data?.data ?? [];
    const q = filters.q.trim().toLowerCase();
    const filtered = q
      ? list.filter(
          (e) =>
            e.name.toLowerCase().includes(q) ||
            e.employeeId.toLowerCase().includes(q) ||
            (e.department || '').toLowerCase().includes(q),
        )
      : list;
    return [...filtered].sort((a, b) => {
      if (a.online !== b.online) return a.online ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
  }, [data?.data, filters.q]);

  const byId = useMemo(() => {
    const m = new Map<string, LiveStreamEmployee>();
    for (const e of data?.data ?? []) m.set(e.employeeId, e);
    return m;
  }, [data?.data]);

  const toggleEmployee = (employeeId: string) => {
    setFilters({ ids: toggleLiveStreamId(selectedIds, employeeId).join(',') });
  };

  const removeEmployee = (employeeId: string) => {
    setFilters({ ids: selectedIds.filter((id) => id !== employeeId).join(',') });
  };

  const openTheater = () => {
    const params = new URLSearchParams();
    if (selectedIds.length) params.set('ids', selectedIds.join(','));
    if (filters.q.trim()) params.set('q', filters.q.trim());
    const qs = params.toString();
    window.open(
      `/live-stream/theater${qs ? `?${qs}` : ''}`,
      '_blank',
      'noopener,noreferrer',
    );
  };

  const tileCount = selectedIds.length;

  return (
    <div className="flex gap-4 h-[calc(100vh-8rem)] min-h-[480px] animate-fade-in">
      <LiveStreamEmployeeSidebar
        employees={employees}
        selectedIds={selectedIds}
        maxTiles={LIVE_STREAM_MAX_TILES}
        searchInput={searchInput}
        onSearchChange={setSearchInput}
        isLoading={isLoading}
        isError={isError}
        onToggle={toggleEmployee}
      />

      <section className="flex-1 bg-card rounded-xl border border-border flex flex-col overflow-hidden min-w-0">
        <div className="px-4 py-3 border-b border-border flex items-center justify-between gap-3">
          <p className="text-sm text-muted-foreground">
            {tileCount === 0
              ? 'Select employees to open live WebRTC previews'
              : `Watching ${tileCount} employee${tileCount === 1 ? '' : 's'}`}
          </p>
          <button
            type="button"
            onClick={openTheater}
            disabled={tileCount === 0}
            className="inline-flex items-center gap-1.5 rounded-md px-2 py-1 text-xs text-muted-foreground hover:text-primary hover:bg-muted/60 transition-colors disabled:opacity-40 disabled:pointer-events-none"
            title="Open selected screens in a new tab"
          >
            <ExternalLink className="w-4 h-4" />
            <span className="hidden sm:inline">Open theater</span>
          </button>
        </div>

        <LiveStreamPreviewGrid
          selectedIds={selectedIds}
          byId={byId}
          maxTiles={LIVE_STREAM_MAX_TILES}
          onRemove={removeEmployee}
        />
      </section>
    </div>
  );
}
