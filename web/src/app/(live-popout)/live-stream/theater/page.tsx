'use client';

import { Suspense, useCallback, useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Loader2 } from 'lucide-react';
import { liveStreamApi, type LiveStreamEmployee } from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { LiveStreamEmployeeSidebar } from '@/components/live-stream/LiveStreamEmployeeSidebar';
import { LiveStreamPreviewGrid } from '@/components/live-stream/LiveStreamPreviewGrid';
import { LiveStreamTheaterNavbar } from '@/components/live-stream/LiveStreamTheaterNavbar';
import {
  LIVE_STREAM_THEATER_MAX_TILES,
  parseLiveStreamCols,
  parseLiveStreamIds,
  toggleLiveStreamId,
  type LiveStreamCols,
} from '@/components/live-stream/live-stream-selection';

/**
 * Full-window multi-monitor theater opened via ExternalLink from /live-stream.
 * Own route + layout (no app chrome). Selection + layout synced via URL.
 */
export default function LiveStreamTheaterPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center h-screen bg-background">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <LiveStreamTheaterInner />
    </Suspense>
  );
}

function LiveStreamTheaterInner() {
  const [filters, setFilters] = useUrlQueryState(
    { ids: {}, q: {}, cols: {}, sidebar: {}, online: {} },
    { ids: '', q: '', cols: '2', sidebar: '1', online: '' },
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
    () => parseLiveStreamIds(filters.ids, LIVE_STREAM_THEATER_MAX_TILES),
    [filters.ids],
  );
  const cols = useMemo(() => parseLiveStreamCols(filters.cols, 2), [filters.cols]);
  const sidebarCollapsed = filters.sidebar === '0';
  const onlineOnly = filters.online === '1';

  const [isFullscreen, setIsFullscreen] = useState(false);
  useEffect(() => {
    const onFs = () => setIsFullscreen(Boolean(document.fullscreenElement));
    document.addEventListener('fullscreenchange', onFs);
    return () => document.removeEventListener('fullscreenchange', onFs);
  }, []);

  const { data, isLoading, isError } = useQuery({
    queryKey: ['live-stream-employees'],
    queryFn: () => liveStreamApi.employees(),
    refetchInterval: 2_000,
    staleTime: 1_000,
  });

  const employees = useMemo(() => {
    const list = data?.data ?? [];
    const q = filters.q.trim().toLowerCase();
    let filtered = q
      ? list.filter(
          (e) =>
            e.name.toLowerCase().includes(q) ||
            e.employeeId.toLowerCase().includes(q) ||
            (e.department || '').toLowerCase().includes(q),
        )
      : list;
    if (onlineOnly) filtered = filtered.filter((e) => e.online);
    return [...filtered].sort((a, b) => {
      if (a.online !== b.online) return a.online ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
  }, [data?.data, filters.q, onlineOnly]);

  const byId = useMemo(() => {
    const m = new Map<string, LiveStreamEmployee>();
    for (const e of data?.data ?? []) m.set(e.employeeId, e);
    return m;
  }, [data?.data]);

  const toggleEmployee = (employeeId: string) => {
    setFilters({
      ids: toggleLiveStreamId(
        selectedIds,
        employeeId,
        LIVE_STREAM_THEATER_MAX_TILES,
      ).join(','),
    });
  };

  const removeEmployee = (employeeId: string) => {
    setFilters({ ids: selectedIds.filter((id) => id !== employeeId).join(',') });
  };

  const setCols = (next: LiveStreamCols) => {
    setFilters({ cols: String(next) });
  };

  const toggleSidebar = () => {
    setFilters({ sidebar: sidebarCollapsed ? '1' : '0' });
  };

  const toggleFullscreen = useCallback(async () => {
    try {
      if (document.fullscreenElement) {
        await document.exitFullscreen();
      } else {
        await document.documentElement.requestFullscreen();
      }
    } catch {
      /* ignored — browser may deny */
    }
  }, []);

  const tileCount = selectedIds.length;

  return (
    <div className="flex flex-col h-full min-h-0 bg-background">
      <LiveStreamTheaterNavbar
        tileCount={tileCount}
        maxTiles={LIVE_STREAM_THEATER_MAX_TILES}
        cols={cols}
        onColsChange={setCols}
        sidebarCollapsed={sidebarCollapsed}
        onToggleSidebar={toggleSidebar}
        onlineOnly={onlineOnly}
        onOnlineOnlyChange={(v) => setFilters({ online: v ? '1' : '' })}
        onClear={() => setFilters({ ids: '' })}
        onToggleFullscreen={toggleFullscreen}
        isFullscreen={isFullscreen}
      />

      <div className="flex flex-1 gap-0 min-h-0 p-2 pt-2">
        <LiveStreamEmployeeSidebar
          employees={employees}
          selectedIds={selectedIds}
          maxTiles={LIVE_STREAM_THEATER_MAX_TILES}
          searchInput={searchInput}
          onSearchChange={setSearchInput}
          isLoading={isLoading}
          isError={isError}
          onToggle={toggleEmployee}
          title="Employees"
          subtitle={`${tileCount} watching · max ${LIVE_STREAM_THEATER_MAX_TILES}`}
          collapsed={sidebarCollapsed}
          onCollapse={toggleSidebar}
        />

        <section
          className={`flex-1 min-w-0 min-h-0 flex flex-col overflow-hidden rounded-xl border border-border bg-card ${
            sidebarCollapsed ? 'ml-2' : 'ml-2'
          }`}
        >
          <LiveStreamPreviewGrid
            selectedIds={selectedIds}
            byId={byId}
            maxTiles={LIVE_STREAM_THEATER_MAX_TILES}
            onRemove={removeEmployee}
            columns={cols}
            emptyTitle="No screens selected"
            emptyBody="Pick employees from the sidebar. Use the toolbar to set how many screens appear per row — the wall scrolls when you open more."
          />
        </section>
      </div>
    </div>
  );
}
