'use client';

import { Suspense } from 'react';
import { Cpu, MemoryStick, Monitor, HardDrive, Wifi, Globe, Fingerprint, Network, Clock, Activity, Loader2 } from 'lucide-react';
import EmployeePage, { type EmployeePageContext } from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import { formatMb, formatDateTime } from '@/lib/format';

export default function DeviceSpecsHardware() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="Hardware Overview"
        subtitle="Processor, memory, storage, network and status of the selected employee's machine."
        icon={Monitor}
        fetchDetail
      >
        {(ctx) => <HardwareOverviewBody {...ctx} />}
      </EmployeePage>
    </Suspense>
  );
}

function HardwareOverviewBody({ detail, devices, deviceId }: EmployeePageContext) {
  const { deviceHardware, storageDevices, networkInfo, appStatus } = detail!;

  const selectedDevice = deviceId
    ? devices.find((d) => d.machineId === deviceId)
    : (devices.length === 1 ? devices[0] : undefined);
  const deviceRemoved = !!selectedDevice && selectedDevice.status === 'old';

  const specs = [
    { icon: Cpu, label: 'Processor', value: deviceHardware?.cpuModel ? `${deviceHardware.cpuModel} · ${deviceHardware.cpuCores} cores` : '—' },
    { icon: MemoryStick, label: 'Memory', value: formatMb(deviceHardware?.ramTotalMb) },
    { icon: Monitor, label: 'Graphics', value: deviceHardware?.gpuModel ? (deviceHardware.gpuVramMb ? `${deviceHardware.gpuModel} · ${formatMb(deviceHardware.gpuVramMb)}` : deviceHardware.gpuModel) : '—' },
    { icon: Globe, label: 'Operating System', value: deviceHardware?.osName ? `${deviceHardware.osName} ${deviceHardware.osVersion}`.trim() : '—' },
    { icon: Fingerprint, label: 'Hostname', value: deviceHardware?.hostname || '—' },
    { icon: Network, label: 'MAC Address', value: deviceHardware?.macAddress || '—' },
    { icon: Clock, label: 'Snapshot Time', value: deviceHardware?.collectedAt ? formatDateTime(deviceHardware.collectedAt) : '—' },
  ];

  return (
    <div className="space-y-4">
      {!deviceHardware && (
        <EmptyState
          icon={Monitor}
          text={
            deviceRemoved
              ? 'No hardware snapshot was recorded for this device before it was removed'
              : 'No hardware snapshot synced yet — the desktop client will upload one after login'
          }
        />
      )}

      {deviceHardware && (
        <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-3">
          {specs.map(({ icon: Icon, label, value }) => (
            <div key={label} className="bg-card rounded-xl border border-border p-4 shadow-card hover:shadow-card-hover transition-shadow">
              <div className="flex items-center gap-2 text-muted-foreground mb-2">
                <Icon className="w-4 h-4 text-primary" />
                <span className="text-xs font-semibold uppercase tracking-wide">{label}</span>
              </div>
              <p className="text-sm font-medium text-foreground leading-snug break-words">{value}</p>
            </div>
          ))}
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <div className="bg-card rounded-xl border border-border shadow-card overflow-hidden">
          <div className="px-4 py-3 border-b border-border flex items-center justify-between">
            <div className="flex items-center gap-2">
              <HardDrive className="w-4 h-4 text-primary" />
              <h3 className="text-sm font-semibold text-foreground">Storage Devices</h3>
            </div>
            <span className="text-xs text-muted-foreground">{storageDevices.length} drive{storageDevices.length === 1 ? '' : 's'}</span>
          </div>
          {storageDevices.length === 0 ? (
            <p className="text-sm text-muted-foreground text-center py-8">No storage devices recorded</p>
          ) : (
            <table className="w-full">
              <tbody>
                {storageDevices.map((d) => (
                  <tr key={d.id} className="border-b border-border last:border-0">
                    <td className="px-4 py-3">
                      <p className="text-sm font-medium text-foreground truncate">{d.model || 'Unknown drive'}</p>
                      <p className="text-xs text-muted-foreground capitalize">{d.deviceType || 'storage'}</p>
                    </td>
                    <td className="px-4 py-3 text-right text-sm font-semibold text-foreground whitespace-nowrap">{formatMb(d.capacityMb)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div className="space-y-4">
          <div className="bg-card rounded-xl border border-border shadow-card p-4">
            <div className="flex items-center gap-2 mb-3">
              <Wifi className="w-4 h-4 text-primary" />
              <h3 className="text-sm font-semibold text-foreground">Network</h3>
            </div>
            {!networkInfo ? (
              <p className="text-sm text-muted-foreground">No network snapshot synced yet</p>
            ) : (
              <div className="grid grid-cols-2 gap-3">
                <NetworkField label="Public IP" value={networkInfo.publicIp || '—'} mono />
                <NetworkField label="Private IP" value={networkInfo.privateIp || '—'} mono />
                <NetworkField label="Interface" value={networkInfo.networkInterfaceName || '—'} />
                <NetworkField label="MAC Address" value={networkInfo.macAddress || '—'} mono />
              </div>
            )}
          </div>

          {Object.keys(appStatus).length > 0 && (
            <div className="bg-card rounded-xl border border-border shadow-card p-4">
              <div className="flex items-center gap-2 mb-3">
                <Activity className="w-4 h-4 text-primary" />
                <h3 className="text-sm font-semibold text-foreground">Device Status</h3>
              </div>
              <div className="flex flex-wrap gap-2">
                {Object.entries(appStatus).map(([key, value]) => (
                  <span key={key} className="px-2 py-1 rounded-lg bg-muted text-xs">
                    <span className="text-muted-foreground">{key.replace(/_/g, ' ')}:</span>{' '}
                    <span className="font-semibold text-foreground">{value || '—'}</span>
                  </span>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function NetworkField({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div>
      <p className="text-xs text-muted-foreground">{label}</p>
      <p className={`text-sm font-medium text-foreground truncate ${mono ? 'font-mono' : ''}`}>{value}</p>
    </div>
  );
}
