'use client';

import { Suspense, useState } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { Usb, Loader2, ChevronDown, ChevronRight } from 'lucide-react';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import DeviceClassIcon from '@/components/employees/DeviceClassIcon';
import { formatDateShort } from '@/lib/format';

type HardwareDevice = {
  id: string;
  product: string | null;
  vendor: string | null;
  deviceClass: string | null;
  serial: string | null;
  pluggedAt: string | null;
  unpluggedAt?: string | null;
};

function groupDevices(devices: HardwareDevice[]): Map<string, HardwareDevice[]> {
  const groups = new Map<string, HardwareDevice[]>();
  for (const device of devices) {
    const hasProduct = device.product && device.product.trim() !== '';
    // Use serial as identity when the product name is generic/empty —
    // two "USB Composite Device" entries with different serials are different hardware.
    const key = hasProduct
      ? [device.product.trim(), device.deviceClass?.trim() || '', device.vendor?.trim() || ''].join('|')
      : `serial:${device.serial}`;
    const existing = groups.get(key) || [];
    existing.push(device);
    groups.set(key, existing);
  }
  // Sort each group by pluggedAt DESC so newest is first
  for (const list of groups.values()) {
    list.sort((a, b) => {
      const ta = a.pluggedAt ? new Date(a.pluggedAt).getTime() : 0;
      const tb = b.pluggedAt ? new Date(b.pluggedAt).getTime() : 0;
      return tb - ta;
    });
  }
  return groups;
}

function DeviceCard({ device, history }: { device: HardwareDevice; history: HardwareDevice[] }) {
  const [expanded, setExpanded] = useState(false);
  const hasHistory = history.length > 1;

  // A history row is effectively disconnected if a newer row exists in the group
  // (the device was replugged). The newest row's own unpluggedAt determines the card status.
  const isNewestInGroup = (h: HardwareDevice) => h.id === device.id;
  const effectiveDisconnected = (h: HardwareDevice) => {
    if (h.unpluggedAt) return true;
    // No unpluggedAt on this row — but if a newer row exists, the device was replugged,
    // so this older session effectively ended when the next one began.
    return !isNewestInGroup(h);
  };

  return (
    <motion.div
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      className="bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-shadow"
    >
      <div className="flex items-start gap-3">
        <div className="w-10 h-10 rounded-xl bg-info/10 flex items-center justify-center flex-shrink-0">
          <DeviceClassIcon deviceClass={device.deviceClass || ''} />
        </div>
        <div className="flex-1 min-w-0">
          <p className="text-sm font-semibold text-foreground truncate">{device.product || device.deviceClass || 'Device'}</p>
          {device.vendor && <p className="text-xs text-muted-foreground truncate">{device.vendor}</p>}
        </div>
        {hasHistory && (
          <button
            type="button"
            onClick={() => setExpanded(v => !v)}
            className="flex items-center justify-center w-6 h-6 rounded-md text-muted-foreground hover:text-foreground hover:bg-muted -mr-2 -mt-1 flex-shrink-0 transition-colors"
            aria-label={expanded ? 'Collapse history' : 'View history'}
            aria-expanded={expanded}
          >
            {expanded ? <ChevronDown className="w-4 h-4" /> : <ChevronRight className="w-4 h-4" />}
          </button>
        )}
        <span className={`flex items-center gap-1.5 text-xs font-medium whitespace-nowrap mt-0.5 ${
          device.unpluggedAt ? 'text-muted-foreground' : 'text-success'
        }`}>
          <span className={`w-2 h-2 rounded-full ${device.unpluggedAt ? 'bg-muted-foreground' : 'bg-success animate-pulse-soft'}`} />
          {device.unpluggedAt ? 'Disconnected' : 'Connected'}
        </span>
      </div>
      <div className="mt-3 pt-3 border-t border-border space-y-1">
        <div className="flex justify-between text-xs">
          <span className="text-muted-foreground capitalize">{device.deviceClass || 'other'}</span>
          <span className="text-muted-foreground">Plugged {formatDateShort(device.pluggedAt)}</span>
        </div>
        {device.serial && (
          <div className="flex justify-between text-xs">
            <span className="text-muted-foreground">Serial</span>
            <span className="font-mono text-foreground truncate ml-3">{device.serial}</span>
          </div>
        )}
        {device.unpluggedAt && (
          <div className="flex justify-between text-xs">
            <span className="text-muted-foreground">Unplugged</span>
            <span className="text-foreground">{formatDateShort(device.unpluggedAt)}</span>
          </div>
        )}
      </div>
      <AnimatePresence>
        {expanded && hasHistory && (
          <motion.div
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: 'auto', opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ duration: 0.15 }}
            className="overflow-hidden"
          >
            <div className="mt-3 pt-3 border-t border-border space-y-2 pl-2">
              {history.map((h) => (
                <div key={h.id} className="flex items-start gap-2 text-xs">
                  <span className={`flex items-center gap-1.5 font-medium whitespace-nowrap mt-0.5 ${
                    effectiveDisconnected(h) ? 'text-muted-foreground' : 'text-success'
                  }`}>
                    <span className={`w-1.5 h-1.5 rounded-full ${effectiveDisconnected(h) ? 'bg-muted-foreground' : 'bg-success'}`} />
                    {effectiveDisconnected(h) ? 'Disconnected' : 'Connected'}
                  </span>
                  <span className="text-muted-foreground">Plugged {formatDateShort(h.pluggedAt)}</span>
                  {h.unpluggedAt && (
                    <span className="text-muted-foreground">Unplugged {formatDateShort(h.unpluggedAt)}</span>
                  )}
                </div>
              ))}
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </motion.div>
  );
}

export default function DeviceSpecsPeripherals() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="Peripherals"
        subtitle="USB and hot-plugged devices connected to the employee's machine."
        icon={Usb}
        fetchDetail
      >
        {({ detail }) => {
          const { hardwareDevices } = detail!;

          if (hardwareDevices.length === 0) {
            return <EmptyState icon={Usb} text="No peripheral devices recorded yet" />;
          }

          const groups = groupDevices(hardwareDevices);

          return (
            <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-3">
              {Array.from(groups.entries()).map(([_, devices]) => (
                <DeviceCard key={devices[0].id} device={devices[0]} history={devices} />
              ))}
            </div>
          );
        }}
      </EmployeePage>
    </Suspense>
  );
}
