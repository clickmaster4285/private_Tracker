'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import { AppWindow, Loader2, Pencil, Plus, Search, Trash2 } from 'lucide-react';
import {
  keepPreviousData,
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
} from '@tanstack/react-query';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Switch } from '@/components/ui/switch';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { dlpApi, type AllowAppRow } from '@/lib/api';
import { formatDateTime } from '@/lib/format';
import { useUrlQueryState } from '@/hooks/use-url-query-state';

const PER_PAGE = 30;

function AppAllowListInner() {
  const qc = useQueryClient();
  const [filters, setFilters] = useUrlQueryState(
    { q: {}, createdBy: {}, enabled: {} },
    { q: '', createdBy: '', enabled: '' },
    { debounceMs: 300 },
  );
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editing, setEditing] = useState<AllowAppRow | null>(null);
  const [publisher, setPublisher] = useState('');
  const [product, setProduct] = useState('');
  const [sha256, setSha256] = useState('');
  const [note, setNote] = useState('');

  const settings = useQuery({
    queryKey: ['dlp-lockdown'],
    queryFn: () => dlpApi.lockdown.get(),
  });

  const listQuery = useInfiniteQuery({
    queryKey: ['dlp-allow-apps', filters],
    queryFn: ({ pageParam }) =>
      dlpApi.lockdown.apps.list({
        q: filters.q || undefined,
        createdBy: filters.createdBy || undefined,
        enabled: filters.enabled || undefined,
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
        if (entries[0]?.isIntersecting && listQuery.hasNextPage && !listQuery.isFetchingNextPage) {
          listQuery.fetchNextPage();
        }
      },
      { rootMargin: '200px' },
    );
    obs.observe(el);
    return () => obs.disconnect();
  }, [listQuery.hasNextPage, listQuery.isFetchingNextPage, listQuery.fetchNextPage]);

  const apps = useMemo(
    () => (listQuery.data?.pages ?? []).flatMap((p) => p.apps),
    [listQuery.data],
  );
  const total = listQuery.data?.pages[0]?.total ?? 0;

  const creators = useMemo(() => {
    const map = new Map<string, string>();
    for (const a of apps) {
      if (a.createdBy && a.createdByName) map.set(a.createdBy, a.createdByName);
    }
    return [...map.entries()].sort((a, b) => a[1].localeCompare(b[1]));
  }, [apps]);

  const openCreate = () => {
    setEditing(null);
    setPublisher('');
    setProduct('');
    setSha256('');
    setNote('');
    setDialogOpen(true);
  };

  const openEdit = (row: AllowAppRow) => {
    setEditing(row);
    setPublisher(row.publisher ?? '');
    setProduct(row.product ?? '');
    setSha256(row.sha256 ?? '');
    setNote(row.note ?? '');
    setDialogOpen(true);
  };

  const saveMutation = useMutation({
    mutationFn: async () => {
      const wasEdit = !!editing;
      const body = { publisher, product, sha256, note };
      if (editing) await dlpApi.lockdown.apps.update(editing.id, body);
      else await dlpApi.lockdown.apps.create(body);
      const pub = await dlpApi.rules.publish();
      return { wasEdit, pub };
    },
    onSuccess: ({ wasEdit, pub }) => {
      qc.invalidateQueries({ queryKey: ['dlp-allow-apps'] });
      qc.invalidateQueries({ queryKey: ['dlp-lockdown'] });
      setDialogOpen(false);
      toast.success(
        wasEdit
          ? `App entry updated · policy v${pub.policyVersion}`
          : `App entry added · policy v${pub.policyVersion}`,
      );
    },
    onError: (err: Error) => toast.error('Save failed', { description: err.message }),
  });

  const deleteMutation = useMutation({
    mutationFn: async (id: number) => {
      await dlpApi.lockdown.apps.remove(id);
      return dlpApi.rules.publish();
    },
    onSuccess: (pub) => {
      qc.invalidateQueries({ queryKey: ['dlp-allow-apps'] });
      qc.invalidateQueries({ queryKey: ['dlp-lockdown'] });
      toast.success(`App entry removed · policy v${pub.policyVersion}`);
    },
    onError: (err: Error) => toast.error('Delete failed', { description: err.message }),
  });

  const toggleMutation = useMutation({
    mutationFn: async ({ id, enabled }: { id: number; enabled: boolean }) => {
      const row = await dlpApi.lockdown.apps.update(id, { enabled });
      const pub = await dlpApi.rules.publish();
      return { row, pub };
    },
    onSuccess: ({ row, pub }) => {
      qc.invalidateQueries({ queryKey: ['dlp-allow-apps'] });
      qc.invalidateQueries({ queryKey: ['dlp-lockdown'] });
      toast.success(
        row.enabled
          ? `App active · policy v${pub.policyVersion}`
          : `App inactive · policy v${pub.policyVersion}`,
      );
    },
    onError: (err: Error) => toast.error('Failed to update status', { description: err.message }),
  });

  return (
    <div className="space-y-4  animate-fade-in">
      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div>
          <h1 className="text-xl font-display font-bold text-foreground flex items-center gap-2">
            <AppWindow className="w-5 h-5 text-primary" />
            App Allow List
          </h1>
          <p className="text-sm text-muted-foreground mt-1">
            Allow programs by code-signing publisher and/or file SHA-256 — never by display name
            alone. Each change publishes into the policy bundle; Windows enforce mode ships later.
          </p>
          {settings.data && !settings.data.lockdown && (
            <p className="text-xs text-warning mt-1">
              Web default-deny is off — turn it on under Web Allow Lists for network lockdown.
            </p>
          )}
        </div>
        <Button onClick={openCreate} className="gradient-primary text-primary-foreground">
          <Plus className="w-4 h-4 mr-1" /> Add app
        </Button>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 min-w-[14rem] flex-1 max-w-sm">
          <Search className="w-4 h-4 text-muted-foreground shrink-0" />
          <input
            value={filters.q}
            onChange={(e) => setFilters({ q: e.target.value })}
            placeholder="Search publisher, product, hash…"
            className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
          />
        </div>
        <Select
          value={filters.createdBy || '__all__'}
          onValueChange={(v) => setFilters({ createdBy: v === '__all__' ? '' : v })}
        >
          <SelectTrigger className="w-[180px] h-9">
            <SelectValue placeholder="Created by" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="__all__">All creators</SelectItem>
            {creators.map(([id, name]) => (
              <SelectItem key={id} value={id}>
                {name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={filters.enabled || '__all__'}
          onValueChange={(v) => setFilters({ enabled: v === '__all__' ? '' : v })}
        >
          <SelectTrigger className="w-[140px] h-9">
            <SelectValue placeholder="Status" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="__all__">All status</SelectItem>
            <SelectItem value="true">Active</SelectItem>
            <SelectItem value="false">Inactive</SelectItem>
          </SelectContent>
        </Select>
        <span className="text-xs text-muted-foreground ml-auto">
          {total} / {settings.data?.maxAllowApps ?? '—'} entries
        </span>
      </div>

      {listQuery.isLoading && (
        <div className="flex justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      )}
      {listQuery.isError && (
        <p className="text-sm text-destructive">Failed to load app allow list</p>
      )}

      {!listQuery.isLoading && !listQuery.isError && (
        <div className="bg-card rounded-xl border border-border overflow-x-auto shadow-card">
          <table className="w-full min-w-[800px]">
            <thead>
              <tr className="border-b border-border">
                {['Publisher', 'Product', 'SHA-256', 'Created by', ''].map((h) => (
                  <th
                    key={h || 'actions'}
                    className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground"
                  >
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {apps.length === 0 && (
                <tr>
                  <td colSpan={5} className="px-4 py-10 text-center text-sm text-muted-foreground">
                    No allowed apps yet.
                  </td>
                </tr>
              )}
              {apps.map((row, i) => (
                <motion.tr
                  key={row.id}
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  transition={{ delay: Math.min(i, 20) * 0.02 }}
                  className="border-b border-border last:border-0 hover:bg-muted/30"
                >
                  <td className="px-4 py-3 text-xs font-mono break-all max-w-[14rem]">
                    {row.publisher || '—'}
                    {row.note ? (
                      <div className="text-muted-foreground font-sans mt-0.5 truncate">{row.note}</div>
                    ) : null}
                  </td>
                  <td className="px-4 py-3 text-sm">{row.product || '—'}</td>
                  <td className="px-4 py-3 text-[11px] font-mono break-all max-w-[12rem]">
                    {row.sha256 || '—'}
                  </td>
                  <td className="px-4 py-3 text-sm">
                    {row.createdByName || <span className="text-muted-foreground">—</span>}
                  </td>
                  <td className="px-4 py-3 text-right whitespace-nowrap">
                    <label className="inline-flex items-center gap-2 cursor-pointer mr-2 align-middle">
                      <Switch
                        checked={row.enabled}
                        disabled={toggleMutation.isPending}
                        onCheckedChange={(checked) =>
                          toggleMutation.mutate({ id: row.id, enabled: checked })
                        }
                        aria-label={row.enabled ? 'Deactivate app' : 'Activate app'}
                      />
                      <span
                        className={`text-xs font-medium ${
                          row.enabled ? 'text-success' : 'text-muted-foreground'
                        }`}
                      >
                        {row.enabled ? 'Active' : 'Inactive'}
                      </span>
                    </label>
                    <Button
                      size="icon"
                      variant="ghost"
                      className="h-8 w-8"
                      onClick={() => openEdit(row)}
                      aria-label="Edit"
                    >
                      <Pencil className="w-4 h-4" />
                    </Button>
                    <Button
                      size="icon"
                      variant="ghost"
                      className="h-8 w-8 text-destructive"
                      disabled={deleteMutation.isPending}
                      onClick={() => {
                        if (confirm('Remove this app allow entry?')) {
                          deleteMutation.mutate(row.id);
                        }
                      }}
                      aria-label="Delete"
                    >
                      <Trash2 className="w-4 h-4" />
                    </Button>
                  </td>
                </motion.tr>
              ))}
            </tbody>
          </table>
          <div ref={sentinelRef} className="h-8 flex items-center justify-center">
            {listQuery.isFetchingNextPage && (
              <Loader2 className="w-4 h-4 animate-spin text-primary" />
            )}
          </div>
        </div>
      )}

      <Dialog open={dialogOpen} onOpenChange={setDialogOpen}>
        <DialogContent className="sm:max-w-lg">
          <DialogHeader>
            <DialogTitle className="font-display">
              {editing ? 'Edit app entry' : 'Add app entry'}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-3 mt-2">
            <div>
              <label className="text-xs font-medium text-muted-foreground">Publisher (subject)</label>
              <Input
                value={publisher}
                onChange={(e) => setPublisher(e.target.value)}
                placeholder="CN=Microsoft Corporation, …"
                className="mt-1 font-mono text-xs"
              />
            </div>
            <div>
              <label className="text-xs font-medium text-muted-foreground">Product (optional)</label>
              <Input
                value={product}
                onChange={(e) => setProduct(e.target.value)}
                placeholder="Microsoft Office"
                className="mt-1"
              />
            </div>
            <div>
              <label className="text-xs font-medium text-muted-foreground">SHA-256 (64 hex)</label>
              <Input
                value={sha256}
                onChange={(e) => setSha256(e.target.value)}
                placeholder="abcdef…"
                className="mt-1 font-mono text-xs"
              />
            </div>
            <div>
              <label className="text-xs font-medium text-muted-foreground">Note (optional)</label>
              <Input
                value={note}
                onChange={(e) => setNote(e.target.value)}
                placeholder="Why this binary is allowed"
                className="mt-1"
              />
            </div>
            <div className="flex justify-end gap-2 pt-2">
              <Button variant="ghost" onClick={() => setDialogOpen(false)}>
                Cancel
              </Button>
              <Button
                disabled={(!publisher.trim() && !sha256.trim()) || saveMutation.isPending}
                onClick={() => saveMutation.mutate()}
              >
                {saveMutation.isPending && <Loader2 className="w-4 h-4 animate-spin mr-1" />}
                Save
              </Button>
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}

export default function AppAllowListPage() {
  return (
    <Suspense
      fallback={
        <div className="flex justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      }
    >
      <AppAllowListInner />
    </Suspense>
  );
}
