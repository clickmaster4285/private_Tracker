'use client';

import { Fragment, Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { Globe, Loader2, ExternalLink, ChevronRight, ChevronDown } from 'lucide-react';
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import ActivityFilters, { type ActivityFilter } from '@/components/journey/ActivityFilters';
import { appItemsApi, type AppItem, type WebUsageRow } from '@/lib/api';
import { formatDateTime, formatDuration, formatSeconds } from '@/lib/format';

const USAGE_PER_PAGE = 100;
const PAGES_PER_PAGE = 20;

/** Browser/source of a journey row, read from metadata_json.processName (e.g. "chrome"). */
function browserOf(item: AppItem): string | null {
  try {
    const meta = item.metadataJson ? JSON.parse(item.metadataJson) as { processName?: string; source?: string } : null;
    const name = meta?.processName;
    if (!name) return null;
    if (meta.source === 'webview') return name;
    return item.browserName || name;
  } catch { return null; }
}

function BrowserBadge({ item }: { item: AppItem }) {
  const browser = browserOf(item);
  if (!browser) return null;
  return (
    <span className="px-1.5 py-0.5 rounded-md text-[10px] font-medium bg-muted text-muted-foreground whitespace-nowrap">
      {browser}
    </span>
  );
}

export default function EmployeeJourneyWeb() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="Web Activity"
        subtitle="Websites the employee visited, captured from browser journeys."
        icon={Globe}
      >
        {({ employee, filter, setFilter }) => (
          <WebBody employeeId={employee.employeeId} filter={filter} setFilter={setFilter} />
        )}
      </EmployeePage>
    </Suspense>
  );
}

function WebBody({
  employeeId,
  filter,
  setFilter,
}: {
  employeeId: string;
  filter: ActivityFilter;
  setFilter: (next: ActivityFilter) => void;
}) {
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [isFiltering, setIsFiltering] = useState(false);

  const toggle = (domain: string) => {
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(domain)) next.delete(domain);
      else next.add(domain);
      return next;
    });
  };

  // Per-domain aggregate (server-side GROUP BY). Visits + Duration are
  // final for the filtered set — they must NOT grow as expand pages load.
  const query = useQuery({
    queryKey: ['app-items', 'usage', { employeeId, ...filter }],
    queryFn: () => appItemsApi.usage({
      employeeId,
      itemType: 'browser_tab',
      page: 1,
      perPage: USAGE_PER_PAGE,
      search: filter.search || undefined,
      dateFrom: filter.dateFrom,
      dateTo: filter.dateTo,
    }),
    placeholderData: keepPreviousData,
  });

  useEffect(() => { setIsFiltering(query.isFetching); }, [query.isFetching]);

  const usage: WebUsageRow[] = useMemo(
    () => query.data?.data ?? [],
    [query.data],
  );
  const totalVisits = query.data?.totalVisitCount ?? 0;
  const filtered = filter.search !== '' || filter.preset !== 'all';

  return (
    <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden">
      <div className="px-4 py-3 border-b border-border">
        <ActivityFilters value={filter} onChange={setFilter} loading={isFiltering} singleDay />
      </div>

      {query.isLoading && usage.length === 0 ? (
        <div className="flex items-center justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : query.isError ? (
        <div className="flex flex-col items-center justify-center py-12 gap-3 text-center">
          <p className="text-sm text-destructive font-medium">Failed to load web activity</p>
          <p className="text-xs text-muted-foreground">{(query.error as Error)?.message || 'Unknown error'}</p>
        </div>
      ) : usage.length === 0 ? (
        <div className="py-12">
          <EmptyState
            icon={Globe}
            text={filtered ? 'No web activity matches the current filters' : 'No web activity captured yet'}
          />
        </div>
      ) : (
        <div className="overflow-x-auto">
          <div className="px-4 py-3 border-b border-border flex items-center justify-between flex-wrap gap-2">
            <div className="flex items-center gap-2">
              <Globe className="w-4 h-4 text-primary" />
              <h3 className="text-sm font-semibold text-foreground">Visited Sites</h3>
            </div>
            <span className="text-xs text-muted-foreground">
              {totalVisits.toLocaleString()} page{totalVisits === 1 ? '' : 's'} · {usage.length} site{usage.length === 1 ? '' : 's'}
            </span>
          </div>
          <table className="w-full min-w-[820px]">
            <thead>
              <tr className="border-b border-border">
                {['Site', 'Visits', 'Duration', 'Last Visited'].map(h => (
                  <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {usage.map(g => {
                const isOpen = expanded.has(g.domain);
                const expandable = g.visitCount > 0;
                return (
                  <Fragment key={g.domain}>
                    <tr className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                      <td className="px-4 py-3">
                        <div
                          className={`flex items-center gap-2 ${expandable ? 'cursor-pointer select-none' : ''}`}
                          onClick={expandable ? () => toggle(g.domain) : undefined}
                          title={expandable ? (isOpen ? 'Collapse pages' : 'Expand pages') : undefined}
                        >
                          {expandable ? (
                            isOpen
                              ? <ChevronDown className="w-4 h-4 text-muted-foreground flex-shrink-0" />
                              : <ChevronRight className="w-4 h-4 text-muted-foreground flex-shrink-0" />
                          ) : (
                            <span className="w-4 flex-shrink-0" />
                          )}
                          <div className="w-8 h-8 rounded-lg bg-info/10 flex items-center justify-center flex-shrink-0">
                            <Globe className="w-4 h-4 text-info" />
                          </div>
                          <div className="min-w-0">
                            <p className="text-sm font-medium text-foreground truncate">{g.domain}</p>
                            <p className="text-xs text-muted-foreground truncate">
                              {g.visitCount} page visit{g.visitCount === 1 ? '' : 's'}
                              {g.visitCount > 1 && !isOpen ? ' — click to view' : ''}
                            </p>
                          </div>
                        </div>
                      </td>
                      <td className="px-4 py-3 text-sm text-foreground font-medium">{g.visitCount}</td>
                      <td className="px-4 py-3 text-sm text-info font-medium whitespace-nowrap">{formatSeconds(g.totalDurationSeconds)}</td>
                      <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(g.lastVisitedAt)}</td>
                    </tr>
                    {isOpen && (
                      <tr className="border-b border-border bg-muted/20">
                        {/* max-w-0 keeps the colspan cell from expanding the parent table when a long title/URL wraps. */}
                        <td colSpan={4} className="px-0 py-0 max-w-0">
                          <ExpandedPages
                            domain={g.domain}
                            employeeId={employeeId}
                            filter={filter}
                          />
                        </td>
                      </tr>
                    )}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function ExpandedPages({
  domain,
  employeeId,
  filter,
}: {
  domain: string;
  employeeId: string;
  filter: ActivityFilter;
}) {
  const pagesQuery = useInfiniteQuery({
    queryKey: ['app-items', 'usage', 'pages', { domain, employeeId, ...filter }],
    queryFn: ({ pageParam }) => appItemsApi.list({
      employeeId,
      itemType: 'browser_tab',
      domain,
      page: pageParam as number,
      perPage: PAGES_PER_PAGE,
      search: filter.search || undefined,
      dateFrom: filter.dateFrom,
      dateTo: filter.dateTo,
    }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
  });

  const items: AppItem[] = useMemo(
    () => pagesQuery.data?.pages.flatMap(p => p.data) ?? [],
    [pagesQuery.data],
  );
  const totalPages = pagesQuery.data?.pages[0]?.total ?? 0;

  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && pagesQuery.hasNextPage && !pagesQuery.isFetchingNextPage) {
          pagesQuery.fetchNextPage();
        }
      },
      { rootMargin: '200px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [pagesQuery.hasNextPage, pagesQuery.isFetchingNextPage, pagesQuery.fetchNextPage]);

  return (
    <div className="px-4 py-3">
      <div className="text-xs font-semibold uppercase tracking-wide text-muted-foreground mb-2">
        {totalPages} page visit{totalPages === 1 ? '' : 's'}
        {pagesQuery.isFetching && (
          <Loader2 className="w-3 h-3 inline ml-2 animate-spin align-middle" />
        )}
      </div>
      {pagesQuery.isLoading ? (
        <div className="flex items-center justify-center py-6">
          <Loader2 className="w-5 h-5 animate-spin text-primary" />
        </div>
      ) : pagesQuery.isError ? (
        <p className="text-xs text-destructive py-2">
          Failed to load pages: {(pagesQuery.error as Error)?.message || 'Unknown error'}
        </p>
      ) : items.length === 0 ? (
        <p className="text-xs text-muted-foreground py-2">No pages for this site in the selected range.</p>
      ) : (
        <>
          <table className="w-full table-fixed">
            <colgroup>
              <col className="w-[34%]" />
              <col className="w-[36%]" />
              <col className="w-[18%]" />
              <col className="w-[12%]" />
            </colgroup>
            <thead>
              <tr className="border-b border-border">
                {['Page', 'URL', 'Visited', 'Duration'].map(h => (
                  <th key={h} className="text-left px-3 py-2 text-xs font-semibold text-muted-foreground whitespace-nowrap">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {items.map(item => <PageRow key={item.id} item={item} />)}
              {pagesQuery.isFetchingNextPage && (
                <tr>
                  <td colSpan={4} className="px-3 py-3">
                    <div className="flex items-center justify-center gap-2 text-xs text-muted-foreground">
                      <Loader2 className="w-3 h-3 animate-spin" /> Loading more…
                    </div>
                  </td>
                </tr>
              )}
            </tbody>
          </table>
          {pagesQuery.hasNextPage && (
            <div ref={sentinelRef} className="h-8 flex items-center justify-center text-[10px] text-muted-foreground">
              Scroll for more
            </div>
          )}
        </>
      )}
    </div>
  );
}

/** One exact page visit row (Page + browser badge, URL, visited, duration). */
function PageRow({ item }: { item: AppItem }) {
  return (
    <tr className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
      <td className="px-3 py-2 align-top">
        <div className="flex items-start gap-2 min-w-0">
          <div className="w-7 h-7 rounded-lg bg-info/10 flex items-center justify-center flex-shrink-0 mt-0.5">
            <Globe className="w-3.5 h-3.5 text-info" />
          </div>
          <div className="min-w-0 flex-1">
            <div className="flex items-start gap-1.5 flex-wrap">
              <p className="text-sm font-medium text-foreground break-words whitespace-normal">
                {item.title || 'Untitled page'}
              </p>
              <BrowserBadge item={item} />
            </div>
            {item.parentItemId && (
              <p className="text-xs text-muted-foreground font-mono break-all whitespace-normal">
                {item.identifier || '—'}
              </p>
            )}
          </div>
        </div>
      </td>
      <td className="px-3 py-2 align-top min-w-0">
        {item.url ? (
          <a
            href={item.url}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 text-sm text-primary hover:underline underline-offset-2 whitespace-nowrap"
            title={item.url}
          >
            <span>
              {item.url.length > 55 ? `${item.url.slice(0, 55)}...` : item.url}
            </span>
            <ExternalLink className="w-3 h-3 flex-shrink-0" />
          </a>
        ) : (
          <span className="text-sm text-muted-foreground">—</span>
        )}
      </td>
      <td className="px-3 py-2 text-sm text-muted-foreground whitespace-nowrap align-top">{formatDateTime(item.openedAt)}</td>
      <td className="px-3 py-2 text-sm text-foreground font-medium whitespace-nowrap align-top">
        {item.closedAt ? formatDuration(item.openedAt, item.closedAt) : '—'}
      </td>
    </tr>
  );
}
