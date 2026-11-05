'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import { Globe2, Loader2, Lock, Pencil, Plus, Search, Trash2, Upload } from 'lucide-react';
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
import { dlpApi, type AllowHostRow } from '@/lib/api';
import { formatDateTime } from '@/lib/format';
import { useUrlQueryState } from '@/hooks/use-url-query-state';

const PER_PAGE = 30;

function WebAllowListsInner() {
  const qc = useQueryClient();
  const [filters, setFilters] = useUrlQueryState(
    { q: {}, createdBy: {}, enabled: {} },
    { q: '', createdBy: '', enabled: '' },
    { debounceMs: 300 },
  );
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editing, setEditing] = useState<AllowHostRow | null>(null);
  const [host, setHost] = useState('');
  const [note, setNote] = useState('');

  const settings = useQuery({
    queryKey: ['dlp-lockdown'],
    queryFn: () => dlpApi.lockdown.get(),
  });

  const listQuery = useInfiniteQuery({
    queryKey: ['dlp-allow-hosts', filters],
    queryFn: ({ pageParam }) =>
      dlpApi.lockdown.hosts.list({
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

  const hosts = useMemo(
    () => (listQuery.data?.pages ?? []).flatMap((p) => p.hosts),
    [listQuery.data],
  );
  const total = listQuery.data?.pages[0]?.total ?? 0;

  const creators = useMemo(() => {
    const map = new Map<string, string>();
    for (const h of hosts) {
      if (h.createdBy && h.createdByName) map.set(h.createdBy, h.createdByName);
    }
    return [...map.entries()].sort((a, b) => a[1].localeCompare(b[1]));
  }, [hosts]);

  const openCreate = () => {
    setEditing(null);
    setHost('');
    setNote('');
    setDialogOpen(true);
  };

  const openEdit = (row: AllowHostRow) => {
    setEditing(row);
    setHost(row.host);
    setNote(row.note ?? '');
    setDialogOpen(true);
  };

  const switchMutation = useMutation({
    mutationFn: (lockdown: boolean) => dlpApi.lockdown.update({ lockdown }),
    onSuccess: (res) => {
      qc.setQueryData(['dlp-lockdown'], res);
      toast.success(res.lockdown ? 'Default-deny on (draft)' : 'Open browsing (draft)', {
        description: 'Click Publish to push this to employee PCs.',
      });
    },
    onError: (err: Error) => toast.error('Failed to update', { description: err.message }),
  });

  const saveMutation = useMutation({
    mutationFn: async () => {
      if (editing) {
        return dlpApi.lockdown.hosts.update(editing.id, { host, note });
      }
      return dlpApi.lockdown.hosts.create({ host, note });
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['dlp-allow-hosts'] });
      qc.invalidateQueries({ queryKey: ['dlp-lockdown'] });
      setDialogOpen(false);
      toast.success(editing ? 'Host updated (draft)' : 'Host added (draft)', {
        description: 'Publish so employee PCs can reach it.',
      });
    },
    onError: (err: Error) => toast.error('Save failed', { description: err.message }),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => dlpApi.lockdown.hosts.remove(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['dlp-allow-hosts'] });
      qc.invalidateQueries({ queryKey: ['dlp-lockdown'] });
      toast.success('Host removed (draft)');
    },
    onError: (err: Error) => toast.error('Delete failed', { description: err.message }),
  });

  // Active/Inactive is draft-only until Publish (same as Web Blocker).
  const toggleMutation = useMutation({
    mutationFn: ({ id, enabled }: { id: number; enabled: boolean }) =>
      dlpApi.lockdown.hosts.update(id, { enabled }),
    onSuccess: (row) => {
      qc.invalidateQueries({ queryKey: ['dlp-allow-hosts'] });
      qc.invalidateQueries({ queryKey: ['dlp-lockdown'] });
      toast.success(row.enabled ? 'Host active (draft)' : 'Host inactive (draft)', {
        description: 'Publish so employee PCs pick this up.',
      });
    },
    onError: (err: Error) => toast.error('Failed to update status', { description: err.message }),
  });

  const publishMutation = useMutation({
    mutationFn: () => dlpApi.rules.publish(),
    onSuccess: (res) => {
      toast.success(`Published policy v${res.policyVersion}`, {
        description: 'Employee PCs will pull the allow list on the next policy poll.',
      });
    },
    onError: (err: Error) => toast.error('Publish failed', { description: err.message }),
  });

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div>
          <h1 className="text-xl font-display font-bold text-foreground flex items-center gap-2">
            <Globe2 className="w-5 h-5 text-primary" />
            Web Allow Lists
          </h1>
          <p className="text-sm text-muted-foreground mt-1">
            After login, Windows PCs block every site by default. Add hosts here, then Publish
            so only these destinations open (plus platform hosts).
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            onClick={() => publishMutation.mutate()}
            disabled={publishMutation.isPending}
          >
            {publishMutation.isPending ? (
              <Loader2 className="w-4 h-4 animate-spin mr-1" />
            ) : (
              <Upload className="w-4 h-4 mr-1" />
            )}
            Publish
          </Button>
          <Button onClick={openCreate} className="gradient-primary text-primary-foreground">
            <Plus className="w-4 h-4 mr-1" /> Add host
          </Button>
        </div>
      </div>

      <div className="bg-card rounded-xl border border-border p-4 shadow-card flex items-start justify-between gap-3 flex-wrap">
        <div className="min-w-0">
          <h2 className="font-display font-bold text-foreground flex items-center gap-2 text-sm">
            <Lock className="w-4 h-4 text-primary" /> Default-deny web
          </h2>
          <p className="text-xs text-muted-foreground mt-0.5">
            Leave On. Turn Off + Publish only to open general browsing.
          </p>
          {(settings.data?.platformAllow?.length ?? 0) > 0 && (
            <div className="flex flex-wrap gap-1 mt-2">
              {settings.data!.platformAllow.map((d) => (
                <code
                  key={d}
                  className="bg-muted px-1.5 py-0.5 rounded text-[10px] text-muted-foreground"
                >
                  {d}
                </code>
              ))}
            </div>
          )}
        </div>
        <label className="inline-flex items-center gap-2 cursor-pointer">
          <Switch
            checked={settings.data?.lockdown ?? false}
            disabled={settings.isLoading || switchMutation.isPending}
            onCheckedChange={(checked) => switchMutation.mutate(checked)}
            aria-label="Toggle default-deny web"
          />
          <span
            className={`text-xs font-medium ${settings.data?.lockdown ? 'text-success' : 'text-muted-foreground'}`}
          >
            {settings.data?.lockdown ? 'On' : 'Off'}
          </span>
        </label>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 min-w-[14rem] flex-1 max-w-sm">
          <Search className="w-4 h-4 text-muted-foreground shrink-0" />
          <input
            value={filters.q}
            onChange={(e) => setFilters({ q: e.target.value })}
            placeholder="Search hosts or notes…"
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
          {total} / {settings.data?.maxAllowHosts ?? '—'} hosts
        </span>
      </div>

      {listQuery.isLoading && (
        <div className="flex justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      )}
      {listQuery.isError && (
        <p className="text-sm text-destructive">Failed to load web allow list</p>
      )}

      {!listQuery.isLoading && !listQuery.isError && (
        <div className="bg-card rounded-xl border border-border overflow-x-auto shadow-card">
          <table className="w-full min-w-[720px]">
            <thead>
              <tr className="border-b border-border">
                {['Host', 'Note', 'Created by', 'Created', ''].map((h) => (
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
              {hosts.length === 0 && (
                <tr>
                  <td colSpan={5} className="px-4 py-10 text-center text-sm text-muted-foreground">
                    No allowed hosts yet. Add a site, then Publish.
                  </td>
                </tr>
              )}
              {hosts.map((row, i) => (
                <motion.tr
                  key={row.id}
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  transition={{ delay: Math.min(i, 20) * 0.02 }}
                  className="border-b border-border last:border-0 hover:bg-muted/30"
                >
                  <td className="px-4 py-3 font-mono text-sm break-all">{row.host}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground max-w-[12rem] truncate">
                    {row.note || '—'}
                  </td>
                  <td className="px-4 py-3 text-sm">
                    {row.createdByName || <span className="text-muted-foreground">—</span>}
                  </td>
                  <td className="px-4 py-3 text-xs text-muted-foreground whitespace-nowrap">
                    {formatDateTime(row.createdAt)}
                  </td>
                  <td className="px-4 py-3 text-right whitespace-nowrap">
                    <label className="inline-flex items-center gap-2 cursor-pointer mr-2 align-middle">
                      <Switch
                        checked={row.enabled}
                        disabled={toggleMutation.isPending}
                        onCheckedChange={(checked) =>
                          toggleMutation.mutate({ id: row.id, enabled: checked })
                        }
                        aria-label={row.enabled ? 'Deactivate host' : 'Activate host'}
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
                        if (confirm(`Remove ${row.host} from the allow list?`)) {
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
        <DialogContent className="sm:max-w-md">
          <DialogHeader>
            <DialogTitle className="font-display">
              {editing ? 'Edit host' : 'Add host'}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-3 mt-2">
            <div>
              <label className="text-xs font-medium text-muted-foreground">Host</label>
              <Input
                value={host}
                onChange={(e) => setHost(e.target.value)}
                placeholder="contoso.sharepoint.com"
                className="mt-1 font-mono text-sm"
              />
            </div>
            <div>
              <label className="text-xs font-medium text-muted-foreground">Note (optional)</label>
              <Input
                value={note}
                onChange={(e) => setNote(e.target.value)}
                placeholder="Why this site is allowed"
                className="mt-1"
              />
            </div>
            <div className="flex justify-end gap-2 pt-2">
              <Button variant="ghost" onClick={() => setDialogOpen(false)}>
                Cancel
              </Button>
              <Button
                disabled={!host.trim() || saveMutation.isPending}
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

export default function WebAllowListsPage() {
  return (
    <Suspense
      fallback={
        <div className="flex justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      }
    >
      <WebAllowListsInner />
    </Suspense>
  );
}
