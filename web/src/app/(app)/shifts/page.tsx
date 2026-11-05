'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, Edit2, Trash2, Loader2, Search } from 'lucide-react';
import { useQuery, useInfiniteQuery, useMutation, useQueryClient, keepPreviousData } from '@tanstack/react-query';
import { toast } from 'sonner';
import { shiftsApi, type Shift, type CreateShiftPayload } from '@/lib/api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { TimezoneCombobox, browserTimezone } from '@/components/TimezoneCombobox';
import { useUrlQueryState } from '@/hooks/use-url-query-state';

const ALL_DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] as const;

const DEFAULT_FORM: CreateShiftPayload = {
  name: '',
  startTime: '09:00',
  endTime: '17:00',
  workingDays: 'Mon,Tue,Wed,Thu,Fri',
  timezone: 'UTC',
  graceMinutes: 5,
  overtimeHours: 8,
  description: '',
};

const PER_PAGE = 12;

export default function ShiftManagement() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <ShiftManagementInner />
    </Suspense>
  );
}

function ShiftManagementInner() {
  const queryClient = useQueryClient();
  // URL-synced search (debounced locally so the input stays responsive).
  const [filters, setFilters] = useUrlQueryState(
    { search: {} },
    { search: '' },
  );
  const [searchInput, setSearchInput] = useState(filters.search);
  useEffect(() => {
    const t = setTimeout(() => {
      if (searchInput !== filters.search) setFilters({ search: searchInput });
    }, 400);
    return () => clearTimeout(t);
  }, [searchInput, filters.search, setFilters]);
  useEffect(() => {
    setSearchInput(filters.search);
  }, [filters.search]);

  const [showDialog, setShowDialog] = useState(false);
  const [editing, setEditing] = useState<Shift | null>(null);
  const [form, setForm] = useState<CreateShiftPayload>(DEFAULT_FORM);

  // ── Paginated list (server-side infinite scroll — AGENTS.md §6) ──
  const {
    data: shiftsData,
    isLoading,
    error,
    isFetchingNextPage,
    fetchNextPage,
    hasNextPage,
  } = useInfiniteQuery({
    queryKey: ['shifts', { search: filters.search, perPage: PER_PAGE }],
    queryFn: ({ pageParam }) =>
      shiftsApi.list({
        page: pageParam as number,
        perPage: PER_PAGE,
        search: filters.search || undefined,
      }),
    initialPageParam: 1,
    getNextPageParam: (last) =>
      last.page < last.totalPages ? last.page + 1 : undefined,
    placeholderData: keepPreviousData,
  });

  const shifts = useMemo(
    () =>
      // Defensive filter: a malformed server response or a partially-migrated
      // DB row can surface as a null/undefined entry inside any page's `data`
      // array (e.g. a row whose scan produced all-NULL columns, or a
      // server-side projection that returned a placeholder). We never want
      // such a row to crash the card render — drop it and let the empty
      // state handle the case.
      shiftsData?.pages.flatMap(p => p.data ?? []).filter((s): s is Shift => s != null) ?? [],
    [shiftsData],
  );
  const total = shiftsData?.pages[0]?.total ?? 0;

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

  // Unpaged list (used for the day-chip default state when adding).
  const { data: allShifts } = useQuery({
    queryKey: ['shifts', 'all'],
    queryFn: () => shiftsApi.listAll(),
    staleTime: 60_000,
  });

  // ── Mutations ──
  const createMutation = useMutation({
    mutationFn: (data: CreateShiftPayload) => shiftsApi.create(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['shifts'] });
      toast.success('Shift created');
      setShowDialog(false);
    },
    onError: (err: Error) => toast.error('Failed to create shift', { description: err.message }),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: number; data: CreateShiftPayload }) =>
      shiftsApi.update(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['shifts'] });
      toast.success('Shift updated');
      setShowDialog(false);
    },
    onError: (err: Error) => toast.error('Failed to update shift', { description: err.message }),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => shiftsApi.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['shifts'] });
      toast.success('Shift deleted');
    },
    onError: (err: Error) => toast.error('Failed to delete shift', { description: err.message }),
  });

  // ── Form helpers ──
  const openNew = () => {
    setEditing(null);
    setForm({ ...DEFAULT_FORM, timezone: browserTimezone() });
    setShowDialog(true);
  };

  const openEdit = (s: Shift) => {
    setEditing(s);
    setForm({
      name: s.name,
      startTime: s.startTime,
      endTime: s.endTime,
      workingDays: s.workingDays,
      timezone: s.timezone || 'UTC',
      graceMinutes: s.graceMinutes,
      overtimeHours: s.overtimeHours,
      description: s.description,
    });
    setShowDialog(true);
  };

  const toggleDay = (day: string) => {
    const set = new Set(form.workingDays.split(',').map(d => d.trim()).filter(Boolean));
    if (set.has(day)) set.delete(day); else set.add(day);
    // Preserve stable ordering matching ALL_DAYS.
    const ordered = ALL_DAYS.filter(d => set.has(d));
    setForm({ ...form, workingDays: ordered.join(',') });
  };

  const activeDays = useMemo(
    () => new Set(form.workingDays.split(',').map(d => d.trim()).filter(Boolean)),
    [form.workingDays],
  );

  const validate = (): string | null => {
    if (!form.name.trim()) return 'Shift name is required';
    if (!form.startTime || !form.endTime) return 'Start and end time are required';
    if (!form.timezone.trim()) return 'IANA timezone is required';
    if (form.graceMinutes < 0 || form.graceMinutes > 120) return 'Grace minutes must be 0–120';
    if (form.overtimeHours < 0 || form.overtimeHours > 24) return 'Overtime hours must be 0–24';
    return null;
  };

  const save = () => {
    const err = validate();
    if (err) {
      toast.error('Validation error', { description: err });
      return;
    }
    if (editing) {
      updateMutation.mutate({ id: editing.id, data: form });
    } else {
      createMutation.mutate(form);
    }
  };

  const handleDelete = (s: Shift) => {
    if (s.employeeCount > 0) {
      toast.error('Shift is in use', {
        description: `${s.employeeCount} employee(s) are assigned to "${s.name}". Reassign them before deleting.`,
      });
      return;
    }
    if (typeof window !== 'undefined' && !window.confirm(`Delete shift "${s.name}"?`)) return;
    deleteMutation.mutate(s.id);
  };

  if (isLoading && !shiftsData) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="flex flex-col items-center gap-3">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
          <p className="text-sm text-muted-foreground">Loading shifts…</p>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="text-center">
          <p className="text-destructive font-medium mb-2">Failed to load shifts</p>
          <p className="text-sm text-muted-foreground">{(error as Error).message}</p>
          <button
            onClick={() => queryClient.invalidateQueries({ queryKey: ['shifts'] })}
            className="mt-4 text-sm text-primary hover:underline"
          >
            Try again
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <div>
          <h3 className="font-display font-bold text-lg text-foreground">Shift Management</h3>
          <p className="text-xs text-muted-foreground mt-0.5">
            {total === 0
              ? 'No shifts defined yet — add one to assign employees.'
              : `${total} shift${total === 1 ? '' : 's'} in the catalog.`}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 w-full sm:w-64">
            <Search className="w-4 h-4 text-muted-foreground" />
            <input
              value={searchInput}
              onChange={e => setSearchInput(e.target.value)}
              placeholder="Search shifts"
              className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
            />
          </div>
          <Button onClick={openNew} size="sm" className="gap-1 gradient-primary text-primary-foreground">
            <Plus className="w-4 h-4" /> Add Shift
          </Button>
        </div>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        {shifts.map((shift, i) => {
          // Defensive defaults: the server projects every shift with safe
          // string/number fallbacks, but a UI crash is never acceptable
          // from a malformed row (partial migration, older API version,
          // or a placeholder from the network). Every field used in the
          // render is defaulted to a UI-safe empty value.
          const days = new Set((shift.workingDays || '').split(',').map(d => d.trim()).filter(Boolean));
          return (
            <motion.div
              key={shift.id}
              initial={{ opacity: 0, y: 10 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ delay: i * 0.03 }}
              className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all"
            >
              <div className="flex items-center justify-between mb-3">
                <div className="min-w-0">
                  <h4 className="font-display font-bold text-foreground truncate">{shift.name}</h4>
                  <p className="text-[11px] text-muted-foreground mt-0.5">
                    {shift.employeeCount} employee{shift.employeeCount === 1 ? '' : 's'} assigned
                  </p>
                </div>
                <div className="flex gap-1 shrink-0">
                  <button
                    onClick={() => openEdit(shift)}
                    className="p-1.5 rounded hover:bg-muted"
                    aria-label={`Edit ${shift.name}`}
                  >
                    <Edit2 className="w-3.5 h-3.5 text-muted-foreground" />
                  </button>
                  <button
                    onClick={() => handleDelete(shift)}
                    className="p-1.5 rounded hover:bg-destructive/10"
                    aria-label={`Delete ${shift.name}`}
                  >
                    <Trash2 className="w-3.5 h-3.5 text-destructive" />
                  </button>
                </div>
              </div>
              <div className="space-y-2 text-sm">
                <div className="flex justify-between">
                  <span className="text-muted-foreground">Hours</span>
                  <span className="text-foreground font-medium">{shift.startTime} – {shift.endTime}</span>
                </div>
                <div className="flex justify-between">
                  <span className="text-muted-foreground">Grace Period</span>
                  <span className="text-foreground">{shift.graceMinutes} min</span>
                </div>
                <div className="flex justify-between">
                  <span className="text-muted-foreground">Timezone</span>
                  <span className="text-foreground">{shift.timezone || 'UTC'}</span>
                </div>
                <div className="flex justify-between">
                  <span className="text-muted-foreground">Overtime After</span>
                  <span className="text-foreground">{shift.overtimeHours}h</span>
                </div>
                <div className="flex gap-1 mt-2 flex-wrap">
                  {ALL_DAYS.map(d => (
                    <span
                      key={d}
                      className={`px-2 py-0.5 rounded text-[10px] font-medium ${
                        days.has(d)
                          ? 'bg-primary/15 text-primary'
                          : 'bg-muted text-muted-foreground'
                      }`}
                    >
                      {d}
                    </span>
                  ))}
                </div>
                {shift.description && (
                  <p className="text-xs text-muted-foreground mt-2 line-clamp-2">
                    {shift.description}
                  </p>
                )}
              </div>
            </motion.div>
          );
        })}
        {shifts.length === 0 && (
          <div className="col-span-full text-center py-12 text-sm text-muted-foreground bg-card border border-dashed border-border rounded-xl">
            No shifts match your search.
          </div>
        )}
      </div>

      {/* Infinite-scroll footer */}
      {hasNextPage ? (
        <div
          ref={sentinelRef}
          className="h-12 flex items-center justify-center text-xs text-muted-foreground"
        >
          {isFetchingNextPage ? (
            <span className="flex items-center gap-2 text-sm text-muted-foreground">
              <Loader2 className="w-4 h-4 animate-spin" /> Loading more…
            </span>
          ) : (
            'Scroll for more'
          )}
        </div>
      ) : (
        shifts.length > 0 && (
          <p className="text-sm text-muted-foreground text-center">
            Showing all {total.toLocaleString()} shift{total === 1 ? '' : 's'}
          </p>
        )
      )}

      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent className="bg-card">
          <DialogHeader>
            <DialogTitle className="font-display">{editing ? 'Edit Shift' : 'New Shift'}</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            <div>
              <label className="text-sm font-semibold text-foreground mb-1 block">Shift Name *</label>
              <Input
                value={form.name}
                onChange={e => setForm({ ...form, name: e.target.value })}
                placeholder="e.g. Morning Shift"
              />
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="text-sm font-semibold text-foreground mb-1 block">Start Time</label>
                <Input
                  type="time"
                  value={form.startTime}
                  onChange={e => setForm({ ...form, startTime: e.target.value })}
                />
              </div>
              <div>
                <label className="text-sm font-semibold text-foreground mb-1 block">End Time</label>
                <Input
                  type="time"
                  value={form.endTime}
                  onChange={e => setForm({ ...form, endTime: e.target.value })}
                />
              </div>
            </div>
            <div>
              <label className="text-sm font-semibold text-foreground mb-2 block">Working Days</label>
              <div className="flex gap-2 flex-wrap">
                {ALL_DAYS.map(d => (
                  <button
                    key={d}
                    onClick={() => toggleDay(d)}
                    className={`px-3 py-1.5 rounded-lg text-xs font-medium border transition-all ${
                      activeDays.has(d)
                        ? 'border-primary bg-primary/10 text-primary'
                        : 'border-border text-muted-foreground'
                    }`}
                  >
                    {d}
                  </button>
                ))}
              </div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="text-sm font-semibold text-foreground mb-1 block">Grace Period (min)</label>
                <Input
                  type="number"
                  min={0}
                  max={120}
                  value={form.graceMinutes}
                  onChange={e => setForm({ ...form, graceMinutes: Number(e.target.value) })}
                />
              </div>
              <div>
                <label className="text-sm font-semibold text-foreground mb-1 block">Overtime Threshold (hrs)</label>
                <Input
                  type="number"
                  min={0}
                  max={24}
                  value={form.overtimeHours}
                  onChange={e => setForm({ ...form, overtimeHours: Number(e.target.value) })}
                />
              </div>
            </div>
            <div>
              <label className="text-sm font-semibold text-foreground mb-1 block">IANA Timezone</label>
              <TimezoneCombobox
                value={form.timezone}
                onChange={(tz) => setForm({ ...form, timezone: tz })}
              />
            </div>
            <div>
              <label className="text-sm font-semibold text-foreground mb-1 block">Description</label>
              <Input
                value={form.description ?? ''}
                onChange={e => setForm({ ...form, description: e.target.value })}
                placeholder="Optional note shown on the shift card"
              />
            </div>
            <Button
              onClick={save}
              disabled={createMutation.isPending || updateMutation.isPending}
              className="w-full gradient-primary text-primary-foreground"
            >
              {createMutation.isPending || updateMutation.isPending
                ? 'Saving…'
                : editing
                ? 'Update Shift'
                : 'Create Shift'}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
