'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { Camera, Loader2 } from 'lucide-react';
import { useInfiniteQuery } from '@tanstack/react-query';
import EmptyState from '@/components/employees/EmptyState';
import { ScreenshotLightbox } from '@/components/screenshots/ScreenshotLightbox';
import { screenshotsApi, type ScreenshotRow } from '@/lib/api';
import { formatDateTimeSeconds } from '@/lib/format';

const PAGE_SIZE = 40;

export function ScreenshotGrid({
  employeeId,
  from,
  to,
  timeFrom,
  timeTo,
  timeZone,
}: {
  employeeId?: string;
  /** YYYY-MM-DD — server applies day bounds. */
  from?: string;
  to?: string;
  /** HH:mm — server clock filter; omit when empty. */
  timeFrom?: string;
  timeTo?: string;
  timeZone?: string;
}) {
  const sentinelRef = useRef<HTMLDivElement | null>(null);
  const [lightboxIndex, setLightboxIndex] = useState<number | null>(null);
  const pendingAdvanceRef = useRef(false);

  const query = useInfiniteQuery({
    queryKey: ['screenshots', { employeeId, from, to, timeFrom, timeTo, timeZone }],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) =>
      screenshotsApi.list({
        employeeId: employeeId || undefined,
        from: from || undefined,
        to: to || undefined,
        timeFrom: timeFrom || undefined,
        timeTo: timeTo || undefined,
        timeZone: timeZone || undefined,
        cursor: pageParam,
        limit: PAGE_SIZE,
      }),
    getNextPageParam: (last) => (last.hasMore && last.nextCursor ? last.nextCursor : undefined),
  });

  const rows = query.data?.pages.flatMap((p) => p.data) ?? [];

  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const obs = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting) && query.hasNextPage && !query.isFetchingNextPage) {
          void query.fetchNextPage();
        }
      },
      { rootMargin: '400px' },
    );
    obs.observe(el);
    return () => obs.disconnect();
  }, [query.hasNextPage, query.isFetchingNextPage, query.fetchNextPage]);

  // After "next" at end triggers fetchMore, advance once new rows arrive.
  useEffect(() => {
    if (!pendingAdvanceRef.current) return;
    if (lightboxIndex == null) return;
    if (lightboxIndex < rows.length - 1) {
      pendingAdvanceRef.current = false;
      setLightboxIndex(lightboxIndex + 1);
    }
  }, [rows.length, lightboxIndex]);

  const closeLightbox = useCallback(() => {
    pendingAdvanceRef.current = false;
    setLightboxIndex(null);
  }, []);

  const onNeedMore = useCallback(() => {
    if (!query.hasNextPage || query.isFetchingNextPage) return;
    pendingAdvanceRef.current = true;
    void query.fetchNextPage();
  }, [query]);

  if (query.isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[240px]">
        <Loader2 className="w-6 h-6 animate-spin text-primary" />
      </div>
    );
  }

  if (query.isError) {
    return (
      <EmptyState
        icon={Camera}
        text={query.error instanceof Error ? query.error.message : 'Failed to load screenshots'}
      />
    );
  }

  if (rows.length === 0) {
    return (
      <EmptyState
        icon={Camera}
        text="No screenshots yet — logged-in agents capture every 30s and upload when online."
      />
    );
  }

  return (
    <>
      <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-3">
        {rows.map((row, i) => (
          <ScreenshotCard key={row.id} row={row} onOpen={() => setLightboxIndex(i)} />
        ))}
      </div>
      <div ref={sentinelRef} className="h-8 flex items-center justify-center mt-3">
        {query.isFetchingNextPage && (
          <Loader2 className="w-4 h-4 animate-spin text-muted-foreground" />
        )}
      </div>

      {lightboxIndex != null && rows[lightboxIndex] && (
        <ScreenshotLightbox
          rows={rows}
          index={lightboxIndex}
          onIndexChange={setLightboxIndex}
          onClose={closeLightbox}
          hasMore={!!query.hasNextPage}
          loadingMore={query.isFetchingNextPage}
          onNeedMore={onNeedMore}
        />
      )}
    </>
  );
}

function ScreenshotCard({ row, onOpen }: { row: ScreenshotRow; onOpen: () => void }) {
  const [visible, setVisible] = useState(false);
  const ref = useRef<HTMLButtonElement | null>(null);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const obs = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setVisible(true);
          obs.disconnect();
        }
      },
      { rootMargin: '200px' },
    );
    obs.observe(el);
    return () => obs.disconnect();
  }, []);

  return (
    <button
      ref={ref}
      type="button"
      onClick={onOpen}
      className="group relative aspect-video rounded-lg overflow-hidden border border-border bg-muted/40 text-left focus:outline-none focus-visible:ring-2 focus-visible:ring-primary"
    >
      {visible ? (
        <img
          src={screenshotsApi.imageSrc(row.id)}
          alt=""
          loading="lazy"
          decoding="async"
          className="absolute inset-0 h-full w-full object-cover transition-transform group-hover:scale-[1.02]"
        />
      ) : (
        <div className="absolute inset-0 animate-pulse bg-muted" />
      )}
      <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/70 to-transparent p-2 pt-6">
        <p className="text-[11px] text-white truncate font-medium">
          {row.employeeName || row.employeeId}
        </p>
        <p className="text-[10px] text-white/70 truncate">{formatDateTimeSeconds(row.capturedAt)}</p>
      </div>
    </button>
  );
}
