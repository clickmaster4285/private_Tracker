'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { Camera, Loader2, X } from 'lucide-react';
import { useInfiniteQuery } from '@tanstack/react-query';
import EmptyState from '@/components/employees/EmptyState';
import { screenshotsApi, type ScreenshotRow } from '@/lib/api';
import { formatDateTime } from '@/lib/format';

const PAGE_SIZE = 40;

export function ScreenshotGrid({
  employeeId,
  from,
  to,
}: {
  employeeId?: string;
  from?: string;
  to?: string;
}) {
  const sentinelRef = useRef<HTMLDivElement | null>(null);
  const [lightbox, setLightbox] = useState<ScreenshotRow | null>(null);

  const query = useInfiniteQuery({
    queryKey: ['screenshots', { employeeId, from, to }],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) =>
      screenshotsApi.list({
        employeeId: employeeId || undefined,
        from: from || undefined,
        to: to || undefined,
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

  const closeLightbox = useCallback(() => setLightbox(null), []);

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
        {rows.map((row) => (
          <ScreenshotCard key={row.id} row={row} onOpen={() => setLightbox(row)} />
        ))}
      </div>
      <div ref={sentinelRef} className="h-8 flex items-center justify-center mt-3">
        {query.isFetchingNextPage && (
          <Loader2 className="w-4 h-4 animate-spin text-muted-foreground" />
        )}
      </div>

      {lightbox && (
        <div
          className="fixed inset-0 z-50 bg-black/80 flex items-center justify-center p-4"
          role="dialog"
          aria-modal="true"
          onClick={closeLightbox}
        >
          <button
            type="button"
            className="absolute top-4 right-4 h-9 w-9 rounded-full bg-black/60 text-white inline-flex items-center justify-center"
            onClick={closeLightbox}
            aria-label="Close"
          >
            <X className="w-5 h-5" />
          </button>
          <div
            className="max-w-[95vw] max-h-[90vh] flex flex-col gap-2"
            onClick={(e) => e.stopPropagation()}
          >
            <img
              src={screenshotsApi.imageSrc(lightbox.id)}
              alt={`Screenshot ${lightbox.employeeId}`}
              className="max-w-full max-h-[80vh] object-contain rounded-md bg-black"
            />
            <p className="text-sm text-white/90 text-center">
              {lightbox.employeeName || lightbox.employeeId} · {formatDateTime(lightbox.capturedAt)}
              {lightbox.width > 0 ? ` · ${lightbox.width}×${lightbox.height}` : ''}
            </p>
          </div>
        </div>
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
        <p className="text-[10px] text-white/70 truncate">{formatDateTime(row.capturedAt)}</p>
      </div>
    </button>
  );
}
