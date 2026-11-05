'use client';

import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Loader2, Laptop } from 'lucide-react';
import { toast } from 'sonner';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import {
  employeesApi,
  type Employee,
  type EmployeeDevice,
  type TrackingModeValue,
} from '@/lib/api';
import TrackingModeBadge, {
  deviceEffectiveMode,
  trackingModeHint,
} from '@/components/employees/TrackingModeBadge';
import { deviceLabel } from '@/components/employees/EmployeePage';

interface SetTrackingModeDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  employee: Employee | null;
  /** Prefill machine when opened from Device Specs. */
  initialMachineId?: string;
}

function choiceFromDevice(d: EmployeeDevice | undefined): TrackingModeValue {
  if (d?.trackingModeOverride === 'roaming') return 'roaming';
  return 'fixed';
}

export default function SetTrackingModeDialog({
  open,
  onOpenChange,
  employee,
  initialMachineId = '',
}: SetTrackingModeDialogProps) {
  const qc = useQueryClient();
  const [machineId, setMachineId] = useState(initialMachineId);
  const [choice, setChoice] = useState<TrackingModeValue>('fixed');

  const detailQuery = useQuery({
    queryKey: ['employee-detail', employee?.id, 'tracking-mode-dialog'],
    queryFn: () => employeesApi.detail(employee!.id),
    enabled: open && !!employee?.id,
  });

  const devices = useMemo(() => {
    const list = detailQuery.data?.devices ?? [];
    return list.filter((d) => d.status === 'active').length > 0
      ? list.filter((d) => d.status === 'active')
      : list;
  }, [detailQuery.data]);

  useEffect(() => {
    if (!open) return;
    const preferred =
      (initialMachineId && devices.find((d) => d.machineId === initialMachineId)?.machineId) ||
      devices.find((d) => d.status === 'active')?.machineId ||
      devices[0]?.machineId ||
      '';
    setMachineId(preferred);
  }, [open, devices, initialMachineId]);

  const selected = devices.find((d) => d.machineId === machineId);

  useEffect(() => {
    if (!open) return;
    setChoice(choiceFromDevice(selected));
  }, [open, selected?.machineId, selected?.trackingModeOverride]);

  const mutation = useMutation({
    mutationFn: async () => {
      if (!employee) throw new Error('No employee');
      if (!machineId) throw new Error('Select a device');
      return employeesApi.setMachineTrackingMode(employee.id, machineId, choice);
    },
    onSuccess: (res) => {
      toast.success(
        `Tracking mode → ${res.effectiveMode === 'roaming' ? 'Roaming' : 'Fixed'} (${res.effectiveModeSource})`,
      );
      qc.invalidateQueries({ queryKey: ['employee-detail'] });
      qc.invalidateQueries({ queryKey: ['employees'] });
      onOpenChange(false);
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Could not update tracking mode');
    },
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Set tracking mode</DialogTitle>
          <DialogDescription>
            Fixed devices always track. Roaming devices let the employee Start and Stop tracking
            (for example a home laptop). Organization DLP rules stay independent of this setting.
          </DialogDescription>
        </DialogHeader>

        {!employee ? (
          <p className="text-sm text-muted-foreground">No employee selected.</p>
        ) : detailQuery.isLoading ? (
          <div className="flex items-center justify-center py-8">
            <Loader2 className="w-6 h-6 animate-spin text-primary" />
          </div>
        ) : devices.length === 0 ? (
          <p className="text-sm text-muted-foreground py-4">
            No devices registered for {employee.name} yet.
          </p>
        ) : (
          <div className="space-y-4 py-1">
            <div className="space-y-2">
              <Label>Device</Label>
              <Select value={machineId} onValueChange={setMachineId}>
                <SelectTrigger className="w-full">
                  <Laptop className="w-4 h-4 text-muted-foreground shrink-0" />
                  <SelectValue placeholder="Select device" />
                </SelectTrigger>
                <SelectContent>
                  {devices.map((d) => (
                    <SelectItem key={d.machineId} value={d.machineId}>
                      {deviceLabel(d, employee)}
                      {d.status === 'old' ? ' · removed' : ''}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>

            {selected && (
              <div className="rounded-lg border border-border bg-muted/40 px-3 py-2 space-y-1">
                <p className="text-xs text-muted-foreground">Current effective mode</p>
                <TrackingModeBadge
                  mode={deviceEffectiveMode(selected)}
                  source={selected.effectiveModeSource}
                  showSource
                />
                <p className="text-[11px] text-muted-foreground">
                  {trackingModeHint(deviceEffectiveMode(selected))}
                </p>
              </div>
            )}

            <div className="space-y-2">
              <Label>Tracking mode for this device</Label>
              <Select value={choice} onValueChange={(v) => setChoice(v as TrackingModeValue)}>
                <SelectTrigger className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="fixed">Fixed — always on, not stoppable</SelectItem>
                  <SelectItem value="roaming">Roaming — employee Start/Stop</SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>
        )}

        <DialogFooter className="gap-2 sm:gap-0">
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            type="button"
            disabled={!employee || !machineId || mutation.isPending || devices.length === 0}
            onClick={() => mutation.mutate()}
          >
            {mutation.isPending ? (
              <>
                <Loader2 className="w-4 h-4 animate-spin mr-2" />
                Saving…
              </>
            ) : (
              'Save'
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
