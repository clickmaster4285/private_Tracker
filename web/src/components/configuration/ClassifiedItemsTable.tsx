'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { Search, Loader2, Plus, X, Filter, Globe, Monitor } from 'lucide-react';
import { useQuery, useInfiniteQuery, useMutation, useQueryClient, keepPreviousData } from '@tanstack/react-query';
import { toast } from 'sonner';
import { monitoringApi, type MonitoringCategoryKind, type ClassificationPayload } from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';

export interface ClassifiedItemRow {
  id: string | number;
  typeId?: number;
  typeName: string;
  typeColor: string;
  categoryId?: number;
  categoryName: string;
}

export interface ClassifiedListResponse<T> {
  data: T[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

interface InfiniteListData<T> {
  pages: ClassifiedListResponse<T>[];
  pageParams: unknown[];
}

interface Props<T extends ClassifiedItemRow> {
  title: string;
  description: string;
  queryKeyPrefix: string;
  scope: Exclude<MonitoringCategoryKind, 'both'>;
  listFn: (params: {
    search?: string;
    typeId?: number;
    categoryId?: number;
    unclassified?: boolean;
    page: number;
    perPage: number;
  }) => Promise<ClassifiedListResponse<T>>;
  classifyFn: (id: string | number, payload: ClassificationPayload) => Promise<{ message: string }>;
  nameOf: (item: T) => string;
  nameHeader: string;
  badge?: (item: T) => React.ReactNode;
  createButton?: React.ReactNode;
}

const PER_PAGE = 30;

type ClassificationStatus = 'all' | 'classified' | 'unclassified';

/**
 * The server's `unclassified` query param only narrows the result set
 * down to "neither typeId nor categoryId is set" — there is no
 * `classified` shortcut. So the `classified` and `all` tabs are
 * implemented here as a client-side predicate over the merged pages.
 * `getStatusBadge` already encodes the same three states
 * (Classified / Partial / Unclassified); this predicate matches the
 * `Classified` and `Unclassified` chips the user clicks.
 */
function matchesStatusFilter(item: ClassifiedItemRow, status: ClassificationStatus): boolean {
  if (status === 'all') return true;
  const hasType = item.typeId !== undefined && item.typeId !== null;
  const hasCategory = item.categoryId !== undefined && item.categoryId !== null;
  if (status === 'classified') return hasType && hasCategory;
  if (status === 'unclassified') return !hasType && !hasCategory;
  return true;
}

export default function ClassifiedItemsTable<T extends ClassifiedItemRow>({
  title,
  description,
  queryKeyPrefix,
  scope,
  listFn,
  classifyFn,
  nameOf,
  nameHeader,
  badge,
  createButton,
}: Props<T>) {
  const queryClient = useQueryClient();

  // URL-synced filter state (q / type / category / status). On first load the
  // URL is empty so the defaults below apply. A user pasting a deep link like
  // `?type=2&status=unclassified` lands on the matching view.
  const [urlFilters, setUrlFilters] = useUrlQueryState(
    { q: {}, type: {}, category: {}, status: {} },
    { q: '', type: '', category: '', status: 'all' },
  );
  const search = urlFilters.q;
  const setSearch = (next: string) => setUrlFilters({ q: next });
  const typeFilter = urlFilters.type;
  const setTypeFilter = (next: string) => setUrlFilters({ type: next });
  const categoryFilter = urlFilters.category;
  const setCategoryFilter = (next: string) => setUrlFilters({ category: next });
  const statusFilter = urlFilters.status as ClassificationStatus;
  const setStatusFilter = (next: ClassificationStatus) => setUrlFilters({ status: next });

  // Local debounced search input. The URL holds the canonical value, but we
  // only commit a write on the 400ms quiet window so rapid keystrokes don't
  // spam router.replace + re-fetch.
  const [searchInput, setSearchInput] = useState(search);
  useEffect(() => {
    setSearchInput(search);
  }, [search]);
  useEffect(() => {
    const t = setTimeout(() => {
      if (searchInput !== search) setSearch(searchInput);
    }, 400);
    return () => clearTimeout(t);
  }, [searchInput, search, setSearch]);

  const filters = useMemo(
    () => ({
      search,
      typeId: typeFilter || undefined,
      categoryId: categoryFilter || undefined,
      // The server only knows an `unclassified=true` shortcut — there is
      // no `classified=true` parameter, and `unclassified=false` would be
      // the same as omitting it (and confusing the cache key). For the
      // "Classified" / "Unclassified" tabs we therefore let the server
      // return every page and filter the result rows client-side below
      // (the `isClassifiedRow` predicate) — the page size is small
      // enough (30/page) that the extra rows are negligible, and the
      // server can still narrow the very-largest tenant with the
      // `unclassified=true` shortcut when the user has explicitly asked
      // for the Unclassified tab.
      unclassified: statusFilter === 'unclassified' ? true : undefined,
      page: 1,
      perPage: PER_PAGE,
    }),
    [search, typeFilter, categoryFilter, statusFilter],
  );

  const { data: typesResponse } = useQuery({
    queryKey: ['monitoring-types'],
    queryFn: () => monitoringApi.types.list(),
  });
  const { data: categoriesResponse } = useQuery({
    queryKey: ['monitoring-categories'],
    queryFn: () => monitoringApi.categories.list(),
  });
  const types = typesResponse?.types ?? [];
  const categories = (categoriesResponse?.categories ?? []).filter(
    c => c.kind === scope || c.kind === 'both',
  );

  const typeMap = useMemo(() => new Map(types.map(t => [t.id, t])), [types]);
  const categoryMap = useMemo(() => new Map(categories.map(c => [c.id, c])), [categories]);

  const {
    data: listData,
    isLoading,
    error,
    isFetchingNextPage,
    fetchNextPage,
    hasNextPage,
  } = useInfiniteQuery({
    queryKey: [queryKeyPrefix, filters],
    queryFn: ({ pageParam }) =>
      listFn({
        search,
        typeId: typeFilter ? Number(typeFilter) : undefined,
        categoryId: categoryFilter ? Number(categoryFilter) : undefined,
        unclassified: statusFilter === 'unclassified' || undefined,
        page: pageParam as number,
        perPage: PER_PAGE,
      }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
    placeholderData: keepPreviousData,
  });

  const rawRows = useMemo(() => (listData?.pages.flatMap(p => p.data) ?? []) as T[], [listData]);
  // Apply the `classified` / `unclassified` / `all` client-side filter on
  // top of the server's response. Without this, the "Classified" tab
  // shows unclassified + partial rows (the server has no `classified`
  // parameter — see the `matchesStatusFilter` doc).
  const rows = useMemo(
    () => rawRows.filter(r => matchesStatusFilter(r, statusFilter)),
    [rawRows, statusFilter],
  );
  // `total` is the unfiltered server total. When the user is on the
  // `all` tab that matches; on the `classified` / `unclassified` tabs
  // the visible count is `rows.length` (the count after the client-side
  // filter) — we surface that in the "X results" badge so the badge
  // doesn't lie about how many rows the user can actually see.
  const total = listData?.pages[0]?.total ?? 0;
  const visibleTotal = statusFilter === 'all' ? total : rows.length;

  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && hasNextPage && !isFetchingNextPage) {
          fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  const classifyMutation = useMutation({
    mutationFn: ({ id, payload }: { id: string | number; payload: ClassificationPayload }) =>
      classifyFn(id, payload),
    onSuccess: (_data, vars) => {
      updateCache(vars.id, vars.payload);
      toast.success('Classification updated');
    },
    onError: (err: Error) => {
      toast.error('Failed to update classification', { description: err.message });
    },
  });

  const isRowPending = (id: string | number) =>
    classifyMutation.isPending && classifyMutation.variables?.id === id;

  const updateCache = (id: string | number, payload: ClassificationPayload) => {
    queryClient.setQueryData<InfiniteListData<T>>([queryKeyPrefix, filters], (old) => {
      if (!old) return old;
      // The server's PATCH can clear one or both of typeId / categoryId,
      // so a row that was "classified" can become "unclassified" (or
      // vice versa) in a single mutation. Re-derive the post-patch
      // typeId / categoryId from the payload so the next step can
      // decide whether the row still belongs in the current cache entry.
      const nextTypeId = payload.typeId !== undefined ? (payload.typeId ?? undefined) : undefined;
      const nextCategoryId = payload.categoryId !== undefined ? (payload.categoryId ?? undefined) : undefined;
      return {
        ...old,
        pages: old.pages.map(page => ({
          ...page,
          data: page.data
            .map(row => {
              if (row.id !== id) return row;
              const type = payload.typeId !== undefined && payload.typeId !== null ? typeMap.get(payload.typeId) : undefined;
              const cat = payload.categoryId !== undefined && payload.categoryId !== null ? categoryMap.get(payload.categoryId) : undefined;
              return {
                ...row,
                typeId: nextTypeId,
                typeName: type?.name ?? '',
                typeColor: type?.color ?? '',
                categoryId: nextCategoryId,
                categoryName: cat?.name ?? '',
              } as T;
            })
            // Drop the row if the user is currently filtering by
            // Classified / Unclassified and the new state no longer
            // matches. Without this, classifying an unclassified row
            // while the Unclassified tab is active would leave the
            // now-classified row visible in the list until the next
            // refetch — the chip would say "Unclassified" but the row
            // would be Classified.
            .filter(row => row.id !== id || matchesStatusFilter(row, statusFilter)),
        })),
      };
    });
  };

  const handleTypeChange = (item: T, value: string) => {
    const typeId = value === '' ? null : Number(value);
    classifyMutation.mutate({ id: item.id, payload: { typeId, categoryId: item.categoryId ?? null } });
  };

  const handleCategoryChange = (item: T, value: string) => {
    const categoryId = value === '' ? null : Number(value);
    classifyMutation.mutate({ id: item.id, payload: { typeId: item.typeId ?? null, categoryId } });
  };

  const clearFilters = () => {
    setSearchInput('');
    setUrlFilters({ q: '', type: '', category: '', status: 'all' });
  };

  const hasActiveFilters = searchInput || typeFilter || categoryFilter || statusFilter !== 'all';

  const getStatusBadge = (item: T) => {
    if (item.typeId && item.categoryId) {
      return (
        <span className="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium bg-emerald-500/10 text-emerald-600 dark:text-emerald-400">
          Classified
        </span>
      );
    }
    if (!item.typeId && !item.categoryId) {
      return (
        <span className="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium bg-amber-500/10 text-amber-600 dark:text-amber-400">
          Unclassified
        </span>
      );
    }
    return (
      <span className="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium bg-blue-500/10 text-blue-600 dark:text-blue-400">
        Partial
      </span>
    );
  };

  if (isLoading && !listData) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <Loader2 className="w-8 h-8 animate-spin text-primary" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <p className="text-destructive">Failed to load {title.toLowerCase()}: {(error as Error).message}</p>
      </div>
    );
  }

  return (
    <div className="space-y-5 animate-fade-in">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-4">
        <div>
          <h1 className="font-display font-bold text-2xl text-foreground">{title}</h1>
          <p className="text-sm text-muted-foreground mt-1">{description}</p>
        </div>
        {createButton}
      </div>

      {/* Filters */}
      <div className="bg-card rounded-xl border border-border p-4 space-y-4">
        <div className="flex flex-col md:flex-row gap-3">
          {/* Search */}
          <div className="flex items-center bg-background border border-border rounded-lg px-3 py-2 gap-2 flex-1 min-w-[240px]">
            <Search className="w-4 h-4 text-muted-foreground shrink-0" />
            <input
              value={searchInput}
              onChange={e => setSearchInput(e.target.value)}
              placeholder="Search..."
              className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
            />
            {searchInput && (
              <button onClick={() => { setSearchInput(''); setUrlFilters({ q: '' }); }} className="text-muted-foreground hover:text-foreground">
                <X className="w-3.5 h-3.5" />
              </button>
            )}
          </div>

          {/* Status filter */}
          <div className="flex items-center gap-1 bg-background border border-border rounded-lg p-0.5">
            {[
              { key: 'all', label: 'All', icon: null },
              { key: 'classified', label: 'Classified', icon: null },
              { key: 'unclassified', label: 'Unclassified', icon: null },
            ].map(opt => (
              <button
                key={opt.key}
                onClick={() => setStatusFilter(opt.key as ClassificationStatus)}
                className={`px-3 py-1.5 rounded-md text-xs font-medium transition-colors ${
                  statusFilter === opt.key
                    ? 'bg-primary text-primary-foreground shadow-sm'
                    : 'text-muted-foreground hover:text-foreground hover:bg-muted/50'
                }`}
              >
                {opt.label}
              </button>
            ))}
          </div>
        </div>

        <div className="flex flex-col sm:flex-row gap-3">
          {/* Type filter */}
          <select
            value={typeFilter}
            onChange={e => setTypeFilter(e.target.value)}
            className="bg-background border border-border rounded-lg px-3 py-2 text-sm text-foreground"
          >
            <option value="">All Types</option>
            {types.map(t => (
              <option key={t.id} value={t.id}>
                <span className="inline-flex items-center gap-1.5">
                  <span className="w-2 h-2 rounded-full" style={{ backgroundColor: t.color || '#888' }} />
                  {t.name}
                </span>
              </option>
            ))}
          </select>

          {/* Category filter */}
          <select
            value={categoryFilter}
            onChange={e => setCategoryFilter(e.target.value)}
            className="bg-background border border-border rounded-lg px-3 py-2 text-sm text-foreground"
          >
            <option value="">All Categories</option>
            {categories.map(c => (
              <option key={c.id} value={c.id}>{c.name}</option>
            ))}
          </select>

          {/* Clear filters */}
          {hasActiveFilters && (
            <button
              onClick={clearFilters}
              className="inline-flex items-center gap-1.5 px-3 py-2 text-xs font-medium text-muted-foreground hover:text-foreground transition-colors"
            >
              <X className="w-3.5 h-3.5" />
              Clear filters
            </button>
          )}

          {/* Active filter count */}
          {hasActiveFilters && (
            <span className="inline-flex items-center px-2 py-1 rounded-md bg-primary/10 text-primary text-xs font-medium">
              {visibleTotal.toLocaleString()} result{visibleTotal === 1 ? '' : 's'}
            </span>
          )}
        </div>
      </div>

      {/* Table */}
      <div className="bg-card rounded-xl border border-border overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full min-w-[720px]">
            <thead>
              <tr className="border-b border-border bg-muted/30">
                <th className="text-left px-5 py-3.5 text-sm font-semibold text-muted-foreground">{nameHeader}</th>
                <th className="text-left px-5 py-3.5 text-sm font-semibold text-muted-foreground">Status</th>
                <th className="text-left px-5 py-3.5 text-sm font-semibold text-muted-foreground">Type</th>
                <th className="text-left px-5 py-3.5 text-sm font-semibold text-muted-foreground">Category</th>
              </tr>
            </thead>
            <tbody>
              {rows.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-5 py-16">
                    <div className="flex flex-col items-center justify-center text-center">
                      <div className="w-16 h-16 rounded-full bg-muted/50 flex items-center justify-center mb-4">
                        {scope === 'application' ? (
                          <Monitor className="w-8 h-8 text-muted-foreground" />
                        ) : (
                          <Globe className="w-8 h-8 text-muted-foreground" />
                        )}
                      </div>
                      <p className="text-sm font-medium text-foreground mb-1">No {title.toLowerCase()} found</p>
                      <p className="text-xs text-muted-foreground max-w-sm">
                        {searchInput || typeFilter || categoryFilter || statusFilter !== 'all'
                          ? 'Try adjusting your filters to see more results.'
                          : scope === 'application'
                          ? 'Applications will appear here once detected on employee machines.'
                          : 'Websites will appear here once employees visit them.'}
                      </p>
                    </div>
                  </td>
                </tr>
              ) : (
                rows.map((item, i) => (
                  <tr
                    key={item.id}
                    className="border-b border-border last:border-0 hover:bg-muted/20 transition-colors group"
                  >
                    <td className="px-5 py-3.5">
                      <div className="flex items-center gap-3">
                        <span className="text-sm font-medium text-foreground">{nameOf(item)}</span>
                        {badge?.(item)}
                      </div>
                    </td>
                    <td className="px-5 py-3.5">
                      {getStatusBadge(item)}
                    </td>
                    <td className="px-5 py-3.5">
                      <div className="flex items-center gap-2">
                        {item.typeColor && (
                          <span
                            className="w-2.5 h-2.5 rounded-full shrink-0 ring-2 ring-transparent group-hover:ring-primary/20 transition-all"
                            style={{ backgroundColor: item.typeColor }}
                          />
                        )}
                        <select
                          value={item.typeId ? String(item.typeId) : ''}
                          onChange={e => handleTypeChange(item, e.target.value)}
                          disabled={isRowPending(item.id)}
                          className="bg-background border border-border rounded-lg px-2.5 py-1.5 text-sm text-foreground disabled:opacity-50 hover:border-primary/30 focus:border-primary focus:ring-1 focus:ring-primary/20 transition-colors"
                          aria-label={`Type for ${nameOf(item)}`}
                        >
                          <option value="">None</option>
                          {types.map(t => (
                            <option key={t.id} value={t.id}>
                              <span className="inline-flex items-center gap-1.5">
                                <span className="w-2 h-2 rounded-full inline-block" style={{ backgroundColor: t.color || '#888' }} />
                                {t.name}
                              </span>
                            </option>
                          ))}
                        </select>
                      </div>
                    </td>
                    <td className="px-5 py-3.5">
                      <select
                        value={item.categoryId ? String(item.categoryId) : ''}
                        onChange={e => handleCategoryChange(item, e.target.value)}
                        disabled={isRowPending(item.id)}
                        className="bg-background border border-border rounded-lg px-2.5 py-1.5 text-sm text-foreground disabled:opacity-50 hover:border-primary/30 focus:border-primary focus:ring-1 focus:ring-primary/20 transition-colors"
                        aria-label={`Category for ${nameOf(item)}`}
                      >
                        <option value="">None</option>
                        {categories.map(c => (
                          <option key={c.id} value={c.id}>{c.name}</option>
                        ))}
                      </select>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      {/* Infinite scroll footer */}
      {hasNextPage ? (
        <div ref={sentinelRef} className="h-12 flex items-center justify-center text-xs text-muted-foreground">
          {isFetchingNextPage ? (
            <span className="flex items-center gap-2 text-sm text-muted-foreground">
              <Loader2 className="w-4 h-4 animate-spin" /> Loading more…
            </span>
          ) : (
            'Scroll for more'
          )}
        </div>
      ) : (
        rows.length > 0 && (
          <p className="text-sm text-muted-foreground text-center">
            Showing all {visibleTotal.toLocaleString()} {title.toLowerCase()}
          </p>
        )
      )}
    </div>
  );
}
