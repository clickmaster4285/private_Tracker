'use client';

import { Suspense, useEffect, useRef } from 'react';
import { motion } from 'framer-motion';
import { Search, Loader2 } from 'lucide-react';
import { keepPreviousData, useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { shadowItApi } from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { formatDateTime } from '@/lib/format';

const authColors: Record<string, string> = {
  approved: 'bg-success/15 text-success',
  unauthorized: 'bg-destructive/15 text-destructive',
  under_review: 'bg-warning/15 text-warning',
};

const riskColors: Record<string, string> = {
  high: 'bg-destructive/15 text-destructive',
  medium: 'bg-warning/15 text-warning',
  low: 'bg-success/15 text-success',
};

const PER_PAGE = 30;

function ShadowITInner() {
  const qc = useQueryClient();
  const [filters, setFilters] = useUrlQueryState(
    { q: {}, authStatus: {} },
    { q: '', authStatus: '' },
    { debounceMs: 300 },
  );

  const query = useInfiniteQuery({
    queryKey: ['shadow-it', filters],
    queryFn: ({ pageParam }) =>
      shadowItApi.list({
        q: filters.q || undefined,
        authStatus: filters.authStatus || undefined,
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

  const updateMutation = useMutation({
    mutationFn: ({ id, action }: { id: number; action: 'approve' | 'block' | 'flag' }) =>
      shadowItApi.update(id, { action }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['shadow-it'] }),
  });

  const pages = query.data?.pages ?? [];
  const apps = pages.flatMap((p) => p.apps);
  const counts = pages[0];

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="grid grid-cols-2 sm:grid-cols-3 gap-4">
        <div className="bg-card rounded-xl border border-border p-4 text-center">
          <p className="text-2xl font-display font-bold text-success">{counts?.approved ?? 0}</p>
          <p className="text-xs text-muted-foreground mt-1">Approved</p>
        </div>
        <div className="bg-card rounded-xl border border-border p-4 text-center">
          <p className="text-2xl font-display font-bold text-destructive">{counts?.unauthorized ?? 0}</p>
          <p className="text-xs text-muted-foreground mt-1">Unauthorized</p>
        </div>
        <div className="bg-card rounded-xl border border-border p-4 text-center">
          <p className="text-2xl font-display font-bold text-warning">{counts?.underReview ?? 0}</p>
          <p className="text-xs text-muted-foreground mt-1">Under Review</p>
        </div>
      </div>

      <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 max-w-xs">
        <Search className="w-4 h-4 text-muted-foreground" />
        <input
          value={filters.q}
          onChange={(e) => setFilters({ q: e.target.value })}
          placeholder="Search apps..."
          className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
        />
      </div>

      {query.isLoading && (
        <div className="flex justify-center py-12"><Loader2 className="w-6 h-6 animate-spin text-primary" /></div>
      )}
      {query.isError && <p className="text-sm text-destructive">Failed to load Shadow IT catalog</p>}

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[700px]">
          <thead>
            <tr className="border-b border-border">
              {['App Name', 'Status', 'Users', 'First Detected', 'Risk Level', 'Action'].map((h) => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {apps.map((app, i) => (
              <motion.tr
                key={app.id}
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                transition={{ delay: Math.min(i, 20) * 0.02 }}
                className="border-b border-border last:border-0 hover:bg-muted/30"
              >
                <td className="px-4 py-3 text-sm font-medium text-foreground">{app.name}</td>
                <td className="px-4 py-3">
                  <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${authColors[app.authStatus] ?? ''}`}>
                    {app.authStatus === 'under_review' ? 'Under Review' : app.authStatus === 'unauthorized' ? 'Unauthorized' : 'Approved'}
                  </span>
                </td>
                <td className="px-4 py-3 text-sm text-foreground">{app.userCount}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{formatDateTime(app.firstDetectedAt)}</td>
                <td className="px-4 py-3">
                  <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${riskColors[app.risk] ?? ''}`}>
                    {app.risk}
                  </span>
                </td>
                <td className="px-4 py-3">
                  <Select
                    value={
                      app.authStatus === 'approved' ? 'approve'
                        : app.authStatus === 'unauthorized' ? 'block'
                          : 'flag'
                    }
                    onValueChange={(v) =>
                      updateMutation.mutate({ id: app.id, action: v as 'approve' | 'block' | 'flag' })
                    }
                  >
                    <SelectTrigger className="h-7 text-xs w-24"><SelectValue /></SelectTrigger>
                    <SelectContent>
                      <SelectItem value="approve">Approve</SelectItem>
                      <SelectItem value="block">Block</SelectItem>
                      <SelectItem value="flag">Flag</SelectItem>
                    </SelectContent>
                  </Select>
                </td>
              </motion.tr>
            ))}
            {!query.isLoading && apps.length === 0 && (
              <tr>
                <td colSpan={6} className="px-4 py-10 text-center text-sm text-muted-foreground">
                  No Shadow IT apps yet — inventory sync will populate this catalog (P3).
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      <div ref={sentinelRef} className="h-8 flex items-center justify-center text-xs text-muted-foreground">
        {query.isFetchingNextPage && <Loader2 className="w-4 h-4 animate-spin" />}
        {!query.hasNextPage && apps.length > 0 && `Showing all ${counts?.total ?? apps.length}`}
      </div>
    </div>
  );
}

export default function ShadowITPage() {
  return (
    <Suspense fallback={<div className="flex justify-center py-16"><Loader2 className="w-6 h-6 animate-spin text-primary" /></div>}>
      <ShadowITInner />
    </Suspense>
  );
}
