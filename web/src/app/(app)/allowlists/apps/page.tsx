'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import { AppWindow, Check, Loader2, Pencil, Plus, Search, Trash2 } from 'lucide-react';
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
import {
  dlpApi,
  monitoringApi,
  type AllowAppRow,
  type MonitoredApp,
} from '@/lib/api';
import { formatDateTime } from '@/lib/format';
import { useUrlQueryState } from '@/hooks/use-url-query-state';

const PER_PAGE = 30;
const CATALOG_PER_PAGE = 20;

function identitySummary(row: AllowAppRow): string {
  const parts: string[] = [];
  if (row.installPath) parts.push(row.installPath);
  if (row.publisher) parts.push(row.publisher);
  if (row.sha256) parts.push(row.sha256.slice(0, 12) + '…');
  return parts.length > 0 ? parts.join(' · ') : '—';
}

function AppAllowListInner() {
  const qc = useQueryClient();
  const [filters, setFilters] = useUrlQueryState(
    { q: {}, createdBy: {}, enabled: {} },
    { q: '', createdBy: '', enabled: '' },
    { debounceMs: 300 },
  );
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editing, setEditing] = useState<AllowAppRow | null>(null);
  const [note, setNote] = useState('');
  const [catalogSearch, setCatalogSearch] = useState('');
  const [selectedCatalog, setSelectedCatalog] = useState<MonitoredApp | null>(null);
  const [pathOverride, setPathOverride] = useState('');
  const [useWindowsSettingsPreset, setUseWindowsSettingsPreset] = useState(false);

  const WIN10_SETTINGS_PATH =
    'C:\\Windows\\ImmersiveControlPanel\\SystemSettings.exe';

  const isLinuxSettingsBinary = (binaryName?: string) => {
    const b = (binaryName || '').toLowerCase();
    return (
      b.includes('cinnamon-settings') ||
      b.includes('gnome-control-center') ||
      b === 'env' ||
      b.includes('wslsettings')
    );
  };

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

  const catalogQuery = useInfiniteQuery({
    queryKey: ['allowlist-catalog-apps', catalogSearch],
    queryFn: ({ pageParam }) =>
      monitoringApi.apps.list({
        search: catalogSearch || undefined,
        page: pageParam,
        perPage: CATALOG_PER_PAGE,
      }),
    initialPageParam: 1,
    getNextPageParam: (last) =>
      last.page < last.totalPages ? last.page + 1 : undefined,
    enabled: dialogOpen && !editing,
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

  const catalogSentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = catalogSentinelRef.current;
    if (!el || !dialogOpen || editing) return;
    const obs = new IntersectionObserver(
      (entries) => {
        if (
          entries[0]?.isIntersecting &&
          catalogQuery.hasNextPage &&
          !catalogQuery.isFetchingNextPage
        ) {
          catalogQuery.fetchNextPage();
        }
      },
      { rootMargin: '120px' },
    );
    obs.observe(el);
    return () => obs.disconnect();
  }, [
    dialogOpen,
    editing,
    catalogQuery.hasNextPage,
    catalogQuery.isFetchingNextPage,
    catalogQuery.fetchNextPage,
  ]);

  const apps = useMemo(
    () => (listQuery.data?.pages ?? []).flatMap((p) => p.apps),
    [listQuery.data],
  );
  const total = listQuery.data?.pages[0]?.total ?? 0;

  const catalogApps = useMemo(
    () => (catalogQuery.data?.pages ?? []).flatMap((p) => p.data),
    [catalogQuery.data],
  );

  const alreadyAllowed = useMemo(() => {
    const set = new Set<string>();
    for (const a of apps) {
      if (a.installedApplicationId) set.add(a.installedApplicationId);
    }
    return set;
  }, [apps]);

  const creators = useMemo(() => {
    const map = new Map<string, string>();
    for (const a of apps) {
      if (a.createdBy && a.createdByName) map.set(a.createdBy, a.createdByName);
    }
    return [...map.entries()].sort((a, b) => a[1].localeCompare(b[1]));
  }, [apps]);

  const openCreate = () => {
    setEditing(null);
    setNote('');
    setCatalogSearch('');
    setSelectedCatalog(null);
    setPathOverride('');
    setUseWindowsSettingsPreset(false);
    setDialogOpen(true);
  };

  const openEdit = (row: AllowAppRow) => {
    setEditing(row);
    setNote(row.note ?? '');
    setSelectedCatalog(null);
    setPathOverride('');
    setUseWindowsSettingsPreset(false);
    setDialogOpen(true);
  };

  const pathTrimmed = pathOverride.trim();
  const noteTrimmed = note.trim();
  /** Path-only allow: no catalog row and not the Windows Settings preset. */
  const isPathOnlyAllow =
    !editing && !selectedCatalog && !useWindowsSettingsPreset && pathTrimmed.length > 0;
  const canSubmitAllow =
    !!editing ||
    useWindowsSettingsPreset ||
    !!selectedCatalog ||
    (pathTrimmed.length > 0 && noteTrimmed.length > 0);

  const saveMutation = useMutation({
    mutationFn: async () => {
      const wasEdit = !!editing;
      if (editing) {
        await dlpApi.lockdown.apps.update(editing.id, { note });
      } else if (useWindowsSettingsPreset) {
        await dlpApi.lockdown.apps.create({
          path: WIN10_SETTINGS_PATH,
          product: 'windows.immersivecontrolpanel_cw5n1h2txyewy',
          note: note || 'Windows Settings (Win10/11)',
        });
      } else if (selectedCatalog) {
        if (isLinuxSettingsBinary(selectedCatalog.binaryName) && !pathOverride.trim()) {
          throw new Error(
            'That catalog entry is Linux (cinnamon/gnome), not Windows Settings. Use “Windows Settings (Win10/11)” or enter a path like C:\\Windows\\ImmersiveControlPanel\\SystemSettings.exe',
          );
        }
        await dlpApi.lockdown.apps.create({
          installedApplicationId: selectedCatalog.id,
          path: pathOverride.trim() || undefined,
          note: note || undefined,
        });
      } else {
        const path = pathOverride.trim();
        const pathNote = note.trim();
        if (!path) {
          throw new Error('Enter an install path, pick a catalog app, or use Windows Settings');
        }
        if (!pathNote) {
          throw new Error('Note is required when allowing by path only');
        }
        await dlpApi.lockdown.apps.create({ path, note: pathNote });
      }
      const pub = await dlpApi.rules.publish();
      return { wasEdit, pub };
    },
    onSuccess: ({ wasEdit, pub }) => {
      qc.invalidateQueries({ queryKey: ['dlp-allow-apps'] });
      qc.invalidateQueries({ queryKey: ['dlp-lockdown'] });
      setDialogOpen(false);
      toast.success(
        wasEdit
          ? `App updated · policy v${pub.policyVersion}`
          : `App allowed · policy v${pub.policyVersion}`,
        { description: 'Employee PCs will refresh on the next policy poll.' },
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
      toast.success(`App removed · policy v${pub.policyVersion}`);
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
    <div className="space-y-4 animate-fade-in">
      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div>
          <h1 className="text-xl font-display font-bold text-foreground flex items-center gap-2">
            <AppWindow className="w-5 h-5 text-primary" />
            App Allow List
          </h1>
          <p className="text-sm text-muted-foreground mt-1">
            Under lockdown, Windows PCs cannot start applications except this product, the
            desktop shell, and apps you allow here. Pick from the same catalog as Configuration
            → Applications — each change publishes automatically.
          </p>
          {settings.data && !settings.data.lockdown && (
            <p className="text-xs text-warning mt-1">
              Default-deny is off — turn it on under Web Allow Lists to enforce app and web
              lockdown.
            </p>
          )}
        </div>
        <Button onClick={openCreate} className="gradient-primary text-primary-foreground">
          <Plus className="w-4 h-4 mr-1" /> Allow app
        </Button>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 min-w-[14rem] flex-1 max-w-sm">
          <Search className="w-4 h-4 text-muted-foreground shrink-0" />
          <input
            value={filters.q}
            onChange={(e) => setFilters({ q: e.target.value })}
            placeholder="Search application, path, publisher…"
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
                {['Application', 'Allow identity', 'Created by', 'Created', ''].map((h) => (
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
                    No allowed apps yet. Calculator, Task Manager, and other programs stay
                    blocked until you allow them.
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
                  <td className="px-4 py-3 text-sm font-medium">
                    {row.appName || row.product || '—'}
                    {row.note ? (
                      <div className="text-xs text-muted-foreground font-normal mt-0.5 truncate max-w-[16rem]">
                        {row.note}
                      </div>
                    ) : null}
                  </td>
                  <td className="px-4 py-3 text-[11px] font-mono break-all max-w-[18rem] text-muted-foreground">
                    {identitySummary(row)}
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
                      aria-label="Edit note"
                    >
                      <Pencil className="w-4 h-4" />
                    </Button>
                    <Button
                      size="icon"
                      variant="ghost"
                      className="h-8 w-8 text-destructive"
                      disabled={deleteMutation.isPending}
                      onClick={() => {
                        if (confirm(`Remove ${row.appName || 'this app'} from the allow list?`)) {
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
              {editing ? 'Edit allow note' : 'Allow application'}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-3 mt-2">
            {!editing && (
              <div className="space-y-3">
                <div>
                  <Button
                    type="button"
                    variant={useWindowsSettingsPreset ? 'default' : 'outline'}
                    className="w-full justify-start"
                    onClick={() => {
                      setUseWindowsSettingsPreset(true);
                      setSelectedCatalog(null);
                      setPathOverride(WIN10_SETTINGS_PATH);
                    }}
                  >
                    <Check className={`w-4 h-4 mr-2 ${useWindowsSettingsPreset ? '' : 'opacity-0'}`} />
                    Windows Settings (Win10/11)
                  </Button>
                  <p className="text-[11px] text-muted-foreground mt-1.5 leading-snug">
                    Use this for Windows Settings. Catalog rows named Settings / Accessibility with{' '}
                    <span className="font-mono">cinnamon-settings</span> or{' '}
                    <span className="font-mono">gnome-control-center</span> are <strong>Linux</strong>, not
                    Windows.
                  </p>
                </div>
                <div>
                  <label className="text-xs font-medium text-muted-foreground">
                    Or pick from inventory catalog
                  </label>
                  <div className="mt-1 flex items-center border border-border rounded-lg px-3 py-2 gap-2">
                    <Search className="w-4 h-4 text-muted-foreground shrink-0" />
                    <input
                      value={catalogSearch}
                      onChange={(e) => setCatalogSearch(e.target.value)}
                      placeholder="Search app name…"
                      className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
                    />
                  </div>
                  <div className="mt-2 max-h-56 overflow-y-auto rounded-lg border border-border divide-y divide-border">
                    {catalogQuery.isLoading && (
                      <div className="flex justify-center py-6">
                        <Loader2 className="w-4 h-4 animate-spin text-primary" />
                      </div>
                    )}
                    {!catalogQuery.isLoading && catalogApps.length === 0 && (
                      <p className="px-3 py-4 text-xs text-muted-foreground text-center">
                        No applications found. Sync inventory from employee PCs first.
                      </p>
                    )}
                    {catalogApps.map((app) => {
                      const taken = alreadyAllowed.has(app.id);
                      const selected = selectedCatalog?.id === app.id && !useWindowsSettingsPreset;
                      const linux = isLinuxSettingsBinary(app.binaryName);
                      return (
                        <button
                          key={app.id}
                          type="button"
                          disabled={taken}
                          onClick={() => {
                            setSelectedCatalog(app);
                            setUseWindowsSettingsPreset(false);
                            if (!pathOverride.trim()) setPathOverride('');
                          }}
                          className={`w-full text-left px-3 py-2 text-sm flex items-start gap-2 transition-colors ${
                            taken
                              ? 'opacity-40 cursor-not-allowed'
                              : selected
                                ? 'bg-primary/10'
                                : 'hover:bg-muted/40'
                          }`}
                        >
                          <span className="mt-0.5 w-4 shrink-0">
                            {selected ? <Check className="w-4 h-4 text-primary" /> : null}
                          </span>
                          <span className="min-w-0">
                            <span className="font-medium block truncate">{app.appName}</span>
                            <span className="text-[11px] text-muted-foreground font-mono truncate block">
                              {app.binaryName || app.id}
                              {taken ? ' · already allowed' : ''}
                              {linux ? ' · Linux (not Win10 Settings)' : ''}
                            </span>
                          </span>
                        </button>
                      );
                    })}
                    <div ref={catalogSentinelRef} className="h-6 flex items-center justify-center">
                      {catalogQuery.isFetchingNextPage && (
                        <Loader2 className="w-3 h-3 animate-spin text-primary" />
                      )}
                    </div>
                  </div>
                </div>
                <div>
                  <label className="text-xs font-medium text-muted-foreground">
                    {selectedCatalog || useWindowsSettingsPreset
                      ? 'Install path override (optional)'
                      : 'Install path (standalone OK)'}
                  </label>
                  <Input
                    value={pathOverride}
                    onChange={(e) => setPathOverride(e.target.value)}
                    placeholder="C:\\Users\\…\\Downloads\\v1.4.2.exe"
                    className="mt-1 font-mono text-xs"
                  />
                  {!selectedCatalog && !useWindowsSettingsPreset && (
                    <p className="text-[11px] text-muted-foreground mt-1.5 leading-snug">
                      You can allow by path alone (no catalog pick). A note is then required
                      (e.g. one-off installers under Downloads).
                    </p>
                  )}
                </div>
              </div>
            )}
            {editing && (
              <p className="text-sm text-foreground">
                <span className="font-medium">{editing.appName || editing.product || 'App'}</span>
                <span className="block text-xs text-muted-foreground font-mono mt-1 break-all">
                  {identitySummary(editing)}
                </span>
              </p>
            )}
            <div>
              <label className="text-xs font-medium text-muted-foreground">
                {isPathOnlyAllow ? 'Note (required)' : 'Note (optional)'}
              </label>
              <Input
                value={note}
                onChange={(e) => setNote(e.target.value)}
                placeholder={
                  isPathOnlyAllow
                    ? 'Required — why this path is allowed'
                    : 'Why this app is allowed'
                }
                className="mt-1"
              />
            </div>
            <div className="flex justify-end gap-2 pt-2">
              <Button variant="ghost" onClick={() => setDialogOpen(false)}>
                Cancel
              </Button>
              <Button
                disabled={!canSubmitAllow || saveMutation.isPending}
                onClick={() => saveMutation.mutate()}
              >
                {saveMutation.isPending && <Loader2 className="w-4 h-4 animate-spin mr-1" />}
                {editing ? 'Save' : 'Allow & publish'}
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
