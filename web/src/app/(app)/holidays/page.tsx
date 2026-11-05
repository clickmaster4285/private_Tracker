'use client';

import { Suspense, useMemo, useState } from 'react';
import { motion } from 'framer-motion';
import {
  Calendar,
  CalendarDays,
  Check,
  Clock3,
  Loader2,
  Plus,
  Trash2,
  Edit2,
  X,
  Ban,
} from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { format, parseISO, isValid } from 'date-fns';
import { toast } from 'sonner';
import {
  holidaysApi,
  type Holiday,
  type HolidayInput,
  type HolidayStatus,
} from '@/lib/api';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription } from '@/components/ui/dialog';
import EmptyState from '@/components/employees/EmptyState';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { cn } from '@/lib/utils';

const EMPTY_FORM: HolidayInput = { date: '', label: '', status: 'pending' };

const STATUS_OPTIONS: { value: HolidayStatus | 'all'; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'pending', label: 'Pending' },
  { value: 'approved', label: 'Approved' },
  { value: 'rejected', label: 'Rejected' },
];

const STATUS_META: Record<
  HolidayStatus,
  { label: string; hint: string; className: string; icon: typeof Clock3 }
> = {
  pending: {
    label: 'Pending',
    hint: 'Waiting for review — not used for attendance yet',
    className: 'bg-warning/15 text-warning border-warning/30',
    icon: Clock3,
  },
  approved: {
    label: 'Approved',
    hint: 'Active holiday — mirrored to desktop clients',
    className: 'bg-success/15 text-success border-success/30',
    icon: Check,
  },
  rejected: {
    label: 'Rejected',
    hint: 'Declined — ignored by schedule and attendance',
    className: 'bg-destructive/15 text-destructive border-destructive/30',
    icon: Ban,
  },
};

function normalizeStatus(raw: string | undefined): HolidayStatus {
  if (raw === 'approved' || raw === 'rejected' || raw === 'pending') return raw;
  return 'pending';
}

function formatHolidayDate(isoDate: string): {
  month: string;
  day: string;
  weekday: string;
  full: string;
} {
  try {
    const d = parseISO(isoDate);
    if (!isValid(d)) {
      return { month: '—', day: '—', weekday: '', full: isoDate };
    }
    return {
      month: format(d, 'MMM'),
      day: format(d, 'd'),
      weekday: format(d, 'EEEE'),
      full: format(d, 'MMM d, yyyy'),
    };
  } catch {
    return { month: '—', day: '—', weekday: '', full: isoDate };
  }
}

function StatusBadge({ status }: { status: HolidayStatus }) {
  const meta = STATUS_META[status];
  const Icon = meta.icon;
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 rounded-md border px-2 py-0.5 text-xs font-semibold',
        meta.className,
      )}
    >
      <Icon className="h-3 w-3" />
      {meta.label}
    </span>
  );
}

function HolidaysInner() {
  const queryClient = useQueryClient();
  const [filters, setFilters] = useUrlQueryState<{ status: string }>(
    { status: {} },
    { status: 'all' },
  );
  const statusFilter = (filters.status || 'all') as HolidayStatus | 'all';

  const [showDialog, setShowDialog] = useState(false);
  const [editing, setEditing] = useState<Holiday | null>(null);
  const [form, setForm] = useState<HolidayInput>(EMPTY_FORM);

  const { data, isLoading, error } = useQuery({
    queryKey: ['holidays'],
    queryFn: () => holidaysApi.list(),
  });

  const allHolidays = data?.data ?? [];

  const counts = useMemo(() => {
    const next = { pending: 0, approved: 0, rejected: 0, total: allHolidays.length };
    for (const h of allHolidays) {
      next[normalizeStatus(h.status)] += 1;
    }
    return next;
  }, [allHolidays]);

  const holidays = useMemo(() => {
    if (statusFilter === 'all') return allHolidays;
    return allHolidays.filter((h) => normalizeStatus(h.status) === statusFilter);
  }, [allHolidays, statusFilter]);

  const createMutation = useMutation({
    mutationFn: (payload: HolidayInput) => holidaysApi.create(payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['holidays'] });
      toast.success('Holiday created');
      setShowDialog(false);
    },
    onError: (err: Error) => toast.error('Failed to create holiday', { description: err.message }),
  });

  const updateMutation = useMutation({
    mutationFn: ({
      id,
      payload,
      successMessage,
    }: {
      id: number;
      payload: HolidayInput;
      successMessage?: string;
    }) => holidaysApi.update(id, payload),
    onSuccess: (_data, vars) => {
      queryClient.invalidateQueries({ queryKey: ['holidays'] });
      toast.success(vars.successMessage ?? 'Holiday updated');
      setShowDialog(false);
    },
    onError: (err: Error) => toast.error('Failed to update holiday', { description: err.message }),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => holidaysApi.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['holidays'] });
      toast.success('Holiday deleted');
    },
    onError: (err: Error) => toast.error('Failed to delete holiday', { description: err.message }),
  });

  const saving = createMutation.isPending || updateMutation.isPending;

  const setStatus = (holiday: Holiday, status: HolidayStatus) => {
    const label =
      status === 'approved' ? 'Holiday approved' : status === 'rejected' ? 'Holiday rejected' : 'Marked as pending';
    updateMutation.mutate({
      id: holiday.id,
      payload: { date: holiday.date, label: holiday.label, status },
      successMessage: label,
    });
  };

  const openNew = () => {
    setEditing(null);
    setForm(EMPTY_FORM);
    setShowDialog(true);
  };

  const openEdit = (holiday: Holiday) => {
    setEditing(holiday);
    setForm({
      date: holiday.date,
      label: holiday.label,
      status: normalizeStatus(holiday.status),
    });
    setShowDialog(true);
  };

  const save = () => {
    if (!form.date || !form.label.trim()) {
      toast.error('Date and label are required');
      return;
    }
    const payload: HolidayInput = {
      date: form.date,
      label: form.label.trim(),
      status: form.status,
    };
    if (editing) {
      updateMutation.mutate({ id: editing.id, payload });
    } else {
      createMutation.mutate(payload);
    }
  };

  const handleDelete = (holiday: Holiday) => {
    if (
      typeof window !== 'undefined' &&
      !window.confirm(`Delete holiday "${holiday.label}" on ${holiday.date}?`)
    ) {
      return;
    }
    deleteMutation.mutate(holiday.id);
  };

  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <Loader2 className="w-8 h-8 animate-spin text-primary" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="rounded-xl border border-border bg-card p-10 text-center shadow-card">
        <p className="text-destructive font-medium">Failed to load holidays</p>
        <p className="text-sm text-muted-foreground mt-1">{(error as Error).message}</p>
      </div>
    );
  }

  return (
    <div className="space-y-5 animate-fade-in">
      <header className="rounded-xl border border-border bg-card shadow-card">
        <div className="flex flex-col gap-4 px-4 py-4 sm:px-5 sm:flex-row sm:items-start sm:justify-between">
          <div className="flex items-start gap-3 min-w-0">
            <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl gradient-primary">
              <CalendarDays className="h-5 w-5 text-primary-foreground" />
            </div>
            <div className="min-w-0">
              <h2 className="font-display text-lg font-bold leading-tight text-foreground">
                Company Holidays
              </h2>
              <p className="mt-1 max-w-xl text-sm leading-relaxed text-muted-foreground">
                Define company-wide days off. Only approved holidays are mirrored to desktop
                clients and used for attendance.
              </p>
            </div>
          </div>
          <Button
            onClick={openNew}
            size="sm"
            className="gap-1.5 shrink-0 gradient-primary text-primary-foreground self-start"
          >
            <Plus className="w-4 h-4" />
            Add Holiday
          </Button>
        </div>

        <div className="grid grid-cols-2 sm:grid-cols-4 gap-px border-t border-border bg-border">
          {(
            [
              { key: 'total', label: 'Total', value: counts.total, tone: 'text-foreground' },
              { key: 'pending', label: 'Pending', value: counts.pending, tone: 'text-warning' },
              { key: 'approved', label: 'Approved', value: counts.approved, tone: 'text-success' },
              { key: 'rejected', label: 'Rejected', value: counts.rejected, tone: 'text-destructive' },
            ] as const
          ).map((stat) => (
            <button
              key={stat.key}
              type="button"
              onClick={() =>
                setFilters({ status: stat.key === 'total' ? '' : stat.key })
              }
              className={cn(
                'bg-card px-4 py-3 text-left transition-colors hover:bg-muted/40',
                (stat.key === 'total' ? statusFilter === 'all' : statusFilter === stat.key) &&
                  'bg-muted/50',
              )}
            >
              <p className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
                {stat.label}
              </p>
              <p className={cn('mt-0.5 font-display text-xl font-bold tabular-nums', stat.tone)}>
                {stat.value}
              </p>
            </button>
          ))}
        </div>
      </header>

      <div className="flex flex-col gap-3 rounded-xl border border-border bg-card p-3 shadow-card sm:flex-row sm:items-center sm:justify-between">
        <div className="flex flex-wrap items-center gap-1.5">
          {STATUS_OPTIONS.map((opt) => (
            <button
              key={opt.value}
              type="button"
              onClick={() => setFilters({ status: opt.value === 'all' ? '' : opt.value })}
              className={cn(
                'px-2.5 py-1.5 rounded-lg text-xs font-medium transition-colors border',
                statusFilter === opt.value
                  ? 'bg-primary text-primary-foreground border-primary shadow-sm'
                  : 'border-border text-muted-foreground hover:text-foreground hover:bg-muted',
              )}
            >
              {opt.label}
            </button>
          ))}
        </div>
        <p className="text-xs text-muted-foreground sm:text-right">
          Showing <span className="font-medium text-foreground">{holidays.length}</span>
          {counts.total !== holidays.length ? (
            <>
              {' '}
              of <span className="font-medium text-foreground">{counts.total}</span>
            </>
          ) : null}
        </p>
      </div>

      {holidays.length === 0 ? (
        <div className="rounded-xl border border-border bg-card py-6 shadow-card">
          <EmptyState
            icon={Calendar}
            text={
              statusFilter === 'all'
                ? 'No company holidays configured yet. Add your first holiday to get started.'
                : `No ${statusFilter} holidays in the calendar.`
            }
          />
          {statusFilter === 'all' && (
            <div className="flex justify-center pb-6">
              <Button
                onClick={openNew}
                size="sm"
                className="gap-1.5 gradient-primary text-primary-foreground"
              >
                <Plus className="w-4 h-4" />
                Add Holiday
              </Button>
            </div>
          )}
        </div>
      ) : (
        <div className="space-y-2">
          {holidays.map((holiday, i) => {
            const status = normalizeStatus(holiday.status);
            const dateBits = formatHolidayDate(holiday.date);
            return (
              <motion.article
                key={holiday.id}
                initial={{ opacity: 0, y: 6 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ delay: Math.min(i * 0.02, 0.2) }}
                className="rounded-xl border border-border bg-card p-4 shadow-card transition-shadow hover:shadow-card-hover"
              >
                <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
                  <div className="flex min-w-0 items-start gap-3">
                    <div className="flex h-12 w-12 shrink-0 flex-col items-center justify-center rounded-xl bg-muted/80 border border-border">
                      <span className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground leading-none">
                        {dateBits.month}
                      </span>
                      <span className="font-display text-lg font-bold text-foreground leading-tight">
                        {dateBits.day}
                      </span>
                    </div>
                    <div className="min-w-0 pt-0.5">
                      <div className="flex flex-wrap items-center gap-2">
                        <h3 className="font-display text-sm font-semibold text-foreground truncate">
                          {holiday.label}
                        </h3>
                        <StatusBadge status={status} />
                      </div>
                      <p className="mt-1 text-xs text-muted-foreground">
                        {dateBits.weekday}
                        {dateBits.weekday ? ' · ' : ''}
                        {dateBits.full}
                        {' · '}
                        {STATUS_META[status].hint}
                      </p>
                    </div>
                  </div>

                  <div className="flex flex-wrap items-center gap-1.5 lg:justify-end">
                    {status !== 'approved' && (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        disabled={saving}
                        onClick={() => setStatus(holiday, 'approved')}
                        className="h-8 gap-1 border-success/30 text-success hover:bg-success/10 hover:text-success"
                      >
                        <Check className="w-3.5 h-3.5" />
                        Approve
                      </Button>
                    )}
                    {status !== 'rejected' && (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        disabled={saving}
                        onClick={() => setStatus(holiday, 'rejected')}
                        className="h-8 gap-1 border-destructive/30 text-destructive hover:bg-destructive/10 hover:text-destructive"
                      >
                        <X className="w-3.5 h-3.5" />
                        Reject
                      </Button>
                    )}
                    {status !== 'pending' && (
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        disabled={saving}
                        onClick={() => setStatus(holiday, 'pending')}
                        className="h-8 text-muted-foreground"
                      >
                        Pending
                      </Button>
                    )}
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8"
                      onClick={() => openEdit(holiday)}
                      aria-label={`Edit ${holiday.label}`}
                    >
                      <Edit2 className="w-3.5 h-3.5 text-muted-foreground" />
                    </Button>
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8 hover:bg-destructive/10"
                      onClick={() => handleDelete(holiday)}
                      aria-label={`Delete ${holiday.label}`}
                    >
                      <Trash2 className="w-3.5 h-3.5 text-destructive" />
                    </Button>
                  </div>
                </div>
              </motion.article>
            );
          })}
        </div>
      )}

      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent className="bg-card sm:max-w-md gap-0 p-0 overflow-hidden">
          <div className="border-b border-border px-6 py-5">
            <DialogHeader className="space-y-2">
              <div className="flex items-center gap-3">
                <div className="flex h-9 w-9 items-center justify-center rounded-lg gradient-primary">
                  <CalendarDays className="h-4 w-4 text-primary-foreground" />
                </div>
                <div>
                  <DialogTitle className="font-display text-left">
                    {editing ? 'Edit Holiday' : 'New Holiday'}
                  </DialogTitle>
                  <DialogDescription className="text-left text-xs">
                    {editing
                      ? 'Update the date, label, or approval status.'
                      : 'Create a company holiday. New entries start as pending.'}
                  </DialogDescription>
                </div>
              </div>
            </DialogHeader>
          </div>

          <div className="space-y-4 px-6 py-5">
            <div className="space-y-1.5">
              <label htmlFor="holiday-date" className="text-sm font-semibold text-foreground">
                Date
              </label>
              <Input
                id="holiday-date"
                type="date"
                value={form.date}
                onChange={(e) => setForm({ ...form, date: e.target.value })}
                className="h-10"
              />
            </div>

            <div className="space-y-1.5">
              <label htmlFor="holiday-label" className="text-sm font-semibold text-foreground">
                Label
              </label>
              <Input
                id="holiday-label"
                value={form.label}
                onChange={(e) => setForm({ ...form, label: e.target.value })}
                placeholder="e.g. Independence Day"
                className="h-10"
              />
            </div>

            <div className="space-y-2">
              <label className="text-sm font-semibold text-foreground">Status</label>
              <div className="grid grid-cols-3 gap-2">
                {(Object.keys(STATUS_META) as HolidayStatus[]).map((key) => {
                  const meta = STATUS_META[key];
                  const Icon = meta.icon;
                  const active = form.status === key;
                  return (
                    <button
                      key={key}
                      type="button"
                      onClick={() => setForm({ ...form, status: key })}
                      className={cn(
                        'flex flex-col items-center gap-1.5 rounded-xl border px-2 py-3 text-center transition-all',
                        active
                          ? cn(meta.className, 'ring-2 ring-offset-1 ring-offset-background ring-current/20')
                          : 'border-border bg-background text-muted-foreground hover:bg-muted/50 hover:text-foreground',
                      )}
                    >
                      <Icon className="h-4 w-4" />
                      <span className="text-xs font-semibold">{meta.label}</span>
                    </button>
                  );
                })}
              </div>
              <p className="text-[11px] leading-relaxed text-muted-foreground">
                {STATUS_META[form.status].hint}
              </p>
            </div>
          </div>

          <div className="flex items-center justify-end gap-2 border-t border-border bg-muted/30 px-6 py-4">
            <Button
              type="button"
              variant="outline"
              onClick={() => setShowDialog(false)}
              disabled={saving}
            >
              Cancel
            </Button>
            <Button
              onClick={save}
              disabled={saving}
              className="min-w-[140px] gradient-primary text-primary-foreground"
            >
              {saving ? (
                <>
                  <Loader2 className="w-4 h-4 animate-spin mr-2" />
                  Saving…
                </>
              ) : editing ? (
                'Save Changes'
              ) : (
                'Create Holiday'
              )}
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}

export default function HolidaysPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[400px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <HolidaysInner />
    </Suspense>
  );
}
