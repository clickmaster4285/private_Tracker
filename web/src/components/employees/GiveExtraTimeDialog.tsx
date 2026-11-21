'use client';

import { useEffect, useMemo, useState } from 'react';
import { Clock, Loader2, Trash2 } from 'lucide-react';
import { toast } from 'sonner';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { shiftExtensionsApi, type Employee, type ShiftExtension } from '@/lib/api';

type Where = 'after' | 'before' | 'both';

const QUICK_MINUTES = [10, 15, 30] as const;

/** Local calendar date as YYYY-MM-DD (the admin's own day). */
function todayIso(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function describe(x: ShiftExtension): string {
  const parts: string[] = [];
  if (x.beforeMinutes > 0) parts.push(`${x.beforeMinutes} min before start`);
  if (x.afterMinutes > 0) parts.push(`${x.afterMinutes} min after end`);
  const dates = x.startDate === x.endDate ? x.startDate : `${x.startDate} → ${x.endDate}`;
  return `${parts.join(' + ')} · ${dates}`;
}

/**
 * "Give extra time": extra minutes before the shift start and/or after the shift end for one
 * employee, for one day or a date range. The shift itself is unchanged. The PC gets it within
 * seconds and opens the Windows account earlier / signs the employee out later.
 */
export function GiveExtraTimeDialog({
  employee,
  open,
  onOpenChange,
}: {
  employee: Employee | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const queryClient = useQueryClient();
  const employeeId = employee?.employeeId ?? '';

  const [minutes, setMinutes] = useState<number>(15);
  const [custom, setCustom] = useState('');
  const [where, setWhere] = useState<Where>('after');
  const [startDate, setStartDate] = useState(todayIso());
  const [endDate, setEndDate] = useState(todayIso());
  const [reason, setReason] = useState('');

  useEffect(() => {
    if (!open) return;
    setMinutes(15);
    setCustom('');
    setWhere('after');
    setStartDate(todayIso());
    setEndDate(todayIso());
    setReason('');
  }, [open, employeeId]);

  const listKey = ['shift-extensions', employeeId];
  const { data, isLoading } = useQuery({
    queryKey: listKey,
    queryFn: () => shiftExtensionsApi.list(employeeId),
    enabled: open && !!employeeId,
  });
  const grants = data?.data ?? [];

  const effectiveMinutes = useMemo(() => {
    if (custom.trim() !== '') {
      const n = Number.parseInt(custom, 10);
      return Number.isFinite(n) ? n : 0;
    }
    return minutes;
  }, [custom, minutes]);

  const minutesValid = effectiveMinutes >= 1 && effectiveMinutes <= 720;
  const datesValid = !!startDate && !!endDate && endDate >= startDate;

  const createMutation = useMutation({
    mutationFn: () =>
      shiftExtensionsApi.create({
        employeeId,
        startDate,
        endDate,
        beforeMinutes: where === 'before' || where === 'both' ? effectiveMinutes : 0,
        afterMinutes: where === 'after' || where === 'both' ? effectiveMinutes : 0,
        reason: reason.trim() || undefined,
      }),
    onSuccess: () => {
      toast.success(`Extra time given to ${employee?.name ?? employeeId}`);
      queryClient.invalidateQueries({ queryKey: listKey });
      queryClient.invalidateQueries({ queryKey: ['employees'] }); // refresh the list badge
      setReason('');
    },
    onError: (err: Error) => toast.error(err.message || 'Failed to give extra time'),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => shiftExtensionsApi.delete(id),
    onSuccess: () => {
      toast.success('Extra time removed');
      queryClient.invalidateQueries({ queryKey: listKey });
      queryClient.invalidateQueries({ queryKey: ['employees'] }); // refresh the list badge
    },
    onError: (err: Error) => toast.error(err.message || 'Failed to remove extra time'),
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Clock className="w-4 h-4" />
            Give extra time{employee ? ` — ${employee.name}` : ''}
          </DialogTitle>
        </DialogHeader>

        <p className="text-sm text-muted-foreground">
          Lets this employee use the PC a few minutes before their shift starts and/or after it
          ends. Their shift is not changed, and other employees are not affected.
        </p>

        <div className="space-y-4">
          <div className="space-y-2">
            <Label>How much extra time</Label>
            <div className="flex flex-wrap items-center gap-2">
              {QUICK_MINUTES.map((m) => (
                <Button
                  key={m}
                  type="button"
                  size="sm"
                  variant={custom === '' && minutes === m ? 'default' : 'outline'}
                  onClick={() => {
                    setMinutes(m);
                    setCustom('');
                  }}
                >
                  +{m} min
                </Button>
              ))}
              <Input
                type="number"
                min={1}
                max={720}
                placeholder="Custom min"
                className="w-28 h-8"
                value={custom}
                onChange={(e) => setCustom(e.target.value)}
              />
            </div>
            {!minutesValid && <p className="text-xs text-destructive">Enter 1 to 720 minutes.</p>}
          </div>

          <div className="space-y-2">
            <Label>When</Label>
            <div className="flex flex-wrap gap-2">
              {(
                [
                  ['after', 'After shift end'],
                  ['before', 'Before shift start'],
                  ['both', 'Both'],
                ] as const
              ).map(([value, label]) => (
                <Button
                  key={value}
                  type="button"
                  size="sm"
                  variant={where === value ? 'default' : 'outline'}
                  onClick={() => setWhere(value)}
                >
                  {label}
                </Button>
              ))}
            </div>
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label htmlFor="ext-from">From</Label>
              <Input
                id="ext-from"
                type="date"
                value={startDate}
                onChange={(e) => {
                  setStartDate(e.target.value);
                  if (e.target.value > endDate) setEndDate(e.target.value);
                }}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="ext-to">To</Label>
              <Input
                id="ext-to"
                type="date"
                value={endDate}
                min={startDate}
                onChange={(e) => setEndDate(e.target.value)}
              />
            </div>
          </div>
          {!datesValid && <p className="text-xs text-destructive">&quot;To&quot; must be on or after &quot;From&quot;.</p>}

          <div className="space-y-1.5">
            <Label htmlFor="ext-reason">Reason (optional)</Label>
            <Textarea
              id="ext-reason"
              rows={2}
              placeholder="e.g. finishing month-end report"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
            />
          </div>

          <Button
            className="w-full"
            disabled={!employeeId || !minutesValid || !datesValid || createMutation.isPending}
            onClick={() => createMutation.mutate()}
          >
            {createMutation.isPending && <Loader2 className="w-4 h-4 mr-2 animate-spin" />}
            Give extra time
          </Button>
        </div>

        <div className="border-t pt-3 space-y-2">
          <p className="text-sm font-medium">Current and upcoming extra time</p>
          {isLoading ? (
            <div className="flex justify-center py-3">
              <Loader2 className="w-4 h-4 animate-spin text-muted-foreground" />
            </div>
          ) : grants.length === 0 ? (
            <p className="text-sm text-muted-foreground">None.</p>
          ) : (
            <ul className="space-y-1.5 max-h-48 overflow-y-auto">
              {grants.map((x) => (
                <li key={x.id} className="flex items-start justify-between gap-2 rounded-md border px-3 py-2 text-sm">
                  <div className="min-w-0">
                    <p>{describe(x)}</p>
                    {x.reason && <p className="text-xs text-muted-foreground break-words">{x.reason}</p>}
                  </div>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    className="h-7 w-7 shrink-0"
                    aria-label="Remove extra time"
                    disabled={deleteMutation.isPending}
                    onClick={() => deleteMutation.mutate(x.id)}
                  >
                    <Trash2 className="w-4 h-4 text-muted-foreground" />
                  </Button>
                </li>
              ))}
            </ul>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}
