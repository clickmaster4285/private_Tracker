'use client';

import { Suspense, useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import {
  Usb, HardDrive, Wifi, Loader2, ShieldOff, ShieldAlert, ShieldCheck, Ban, Search,
  CircleCheck, CircleSlash, Info, Monitor, Keyboard, Mouse, Camera, Printer, Speaker,
  Bluetooth, Network, Smartphone, Cpu, Disc, Fingerprint, CreditCard, Cable, Clock,
  User, Building2, ChevronDown, AlertTriangle, Plug, Eye, ExternalLink,
} from 'lucide-react';
import EmployeePage from '@/components/employees/EmployeePage';
import { portControlApi, dlpApi, type Employee, type MachinePort, type DeviceControlRule } from '@/lib/api';
import { formatRelative } from '@/lib/format';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Switch } from '@/components/ui/switch';
import { Tabs, TabsList, TabsTrigger, TabsContent } from '@/components/ui/tabs';
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select';
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent,
  AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from '@/components/ui/alert-dialog';

// ─── Static reference data ──────────────────────────────────────────────────

const CATEGORIES: { key: number; tab: string; label: string; icon: React.ElementType; desc: string }[] = [
  {
    key: 1, tab: 'external', label: 'External ports', icon: Usb,
    desc: 'Devices plugged into the outside of the computer: USB, displays, Ethernet, audio, card readers, serial and parallel ports.',
  },
  {
    key: 2, tab: 'internal', label: 'Internal hardware', icon: HardDrive,
    desc: 'Hardware inside the computer: disks, storage controllers and PCIe devices. The system disk is shown for visibility and is never blocked.',
  },
  {
    key: 3, tab: 'wireless', label: 'Wireless & virtual', icon: Wifi,
    desc: 'Radios and software adapters: Wi-Fi, Bluetooth, mobile broadband and virtual network adapters (VPN, Hyper-V).',
  },
];

/** Human name + description + icon for each Windows device class. */
interface ClassInfo { label: string; desc: string; icon: React.ElementType; risky?: string }

const CLASS_INFO: Record<string, ClassInfo> = {
  USB: { label: 'USB controllers & hubs', desc: 'USB ports, hubs and composite devices.', icon: Usb, risky: 'Blocking a USB hub or controller can also disconnect the keyboard and mouse plugged into it.' },
  USBDevice: { label: 'USB devices', desc: 'Generic devices connected to a USB port.', icon: Usb },
  Keyboard: { label: 'Keyboards', desc: 'Blocking stops typing on the keyboard.', icon: Keyboard, risky: 'The employee will not be able to type with this keyboard.' },
  Mouse: { label: 'Mice & touchpads', desc: 'Blocking stops this pointing device.', icon: Mouse, risky: 'The employee will not be able to use this mouse or touchpad.' },
  HIDClass: { label: 'Input devices (HID)', desc: 'Keyboards, mice, game controllers and some readers.', icon: Keyboard, risky: 'HID devices include keyboards and mice. Blocking them can leave the computer without input.' },
  Camera: { label: 'Cameras', desc: 'Webcams. Blocking stops video capture.', icon: Camera },
  Image: { label: 'Imaging devices', desc: 'Scanners and cameras.', icon: Camera },
  Media: { label: 'Audio devices', desc: 'Speakers, microphones and headsets.', icon: Speaker },
  AudioEndpoint: { label: 'Audio endpoints', desc: 'Individual playback and recording devices.', icon: Speaker },
  Monitor: { label: 'Displays', desc: 'Monitors on HDMI, DisplayPort, VGA or DVI. Detect and alert only.', icon: Monitor },
  Display: { label: 'Graphics adapters', desc: 'The GPU that drives the screens. Cannot be safely disabled.', icon: Cpu },
  Printer: { label: 'Printers', desc: 'Blocking stops printing to this device.', icon: Printer },
  WPD: { label: 'Phones & portable devices', desc: 'Phones, tablets and MTP storage. Blocking stops file transfer.', icon: Smartphone },
  PortableDevice: { label: 'Portable devices', desc: 'Phones, media players and MTP storage.', icon: Smartphone },
  SmartCardReader: { label: 'Smart-card readers', desc: 'ID, access and payment cards.', icon: CreditCard },
  Ports: { label: 'Serial & parallel ports', desc: 'COM and LPT ports for legacy and industrial hardware.', icon: Cable },
  Biometric: { label: 'Biometric devices', desc: 'Fingerprint readers.', icon: Fingerprint },
  SCMDisk: { label: 'SD / memory cards', desc: 'Storage-class memory and SD card devices.', icon: HardDrive },
  DiskDrive: { label: 'Disk drives', desc: 'External drives can be blocked; the internal system disk is alert only.', icon: HardDrive },
  SCSIAdapter: { label: 'Storage controllers', desc: 'NVMe, SCSI and RAID controllers.', icon: HardDrive },
  HDC: { label: 'Disk controllers', desc: 'SATA / AHCI controllers.', icon: HardDrive },
  CDROM: { label: 'Optical drives', desc: 'CD, DVD and Blu-ray drives.', icon: Disc },
  Net: {
    label: 'Network adapters',
    desc: 'Ethernet, Wi-Fi and VPN adapters. Shown for visibility — never disabled here (that would take the PC offline).',
    icon: Network,
    risky: 'To block internet while keeping the management server reachable, use Allowlists → Web (Network Lockdown).',
  },
  Bluetooth: { label: 'Bluetooth', desc: 'Bluetooth radios and paired devices.', icon: Bluetooth, risky: 'Bluetooth keyboards, mice and headsets will stop working.' },
  Modem: { label: 'Mobile broadband', desc: 'Modems and 4G / 5G adapters.', icon: Network },
};

/** Classes Windows cannot safely hard-block (detect / alert only). */
const ALERT_ONLY_CLASSES = new Set(['Display', 'Monitor', 'Net']);

function classInfo(pnpClass: string): ClassInfo {
  return CLASS_INFO[pnpClass] ?? { label: pnpClass || 'Other devices', desc: 'Hardware detected on this computer.', icon: Plug };
}

function scopeRank(scope: string): number {
  return ({ machine: 3, employee: 2, department: 1, global: 0 } as Record<string, number>)[scope] ?? 0;
}

function scopeLabel(scope: string): string {
  return scope === 'global' ? 'Everyone' : scope === 'employee' ? 'This employee' : scope === 'machine' ? 'This computer' : scope === 'department' ? 'Department' : scope;
}

type ScopeChoice = 'employee' | 'machine' | 'global';
type StatusFilter = 'all' | 'connected' | 'blocked' | 'alert' | 'none';

/** What the PC last reported, combined with the rule we sent it. */
type Enforcement = 'enforced' | 'pending' | 'on-connect' | 'monitoring' | 'none';

function enforcementFor(port: MachinePort, rule?: DeviceControlRule): Enforcement {
  if (!rule) return 'none';
  if (rule.action === 'alert') return 'monitoring';
  if (rule.action !== 'block') return 'none';
  if (!port.present) return 'on-connect';
  // Windows reports a disabled device as a non-OK status (problem code 22).
  return port.status && port.status !== 'OK' ? 'enforced' : 'pending';
}

// ─── Page ───────────────────────────────────────────────────────────────────

export default function DeviceSpecsPorts() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="Ports Control"
        subtitle="Every port and device on the employee's computer. Block, monitor or allow each one."
        icon={Usb}
        bodySchema={{ tab: {}, q: {}, status: {}, disconnected: {} }}
        bodyInitial={{ tab: 'external', q: '', status: 'all', disconnected: '' }}
      >
        {({ employee, body, setBody, deviceId }) => (
          <PortsBody employee={employee} body={body} setBody={setBody} deviceId={deviceId} />
        )}
      </EmployeePage>
    </Suspense>
  );
}

interface PendingAction {
  kind: 'instance' | 'class';
  port: MachinePort;
  title: string;
  count: number;
}

function PortsBody({
  employee, body, setBody, deviceId,
}: {
  employee: Employee;
  body: Record<string, string>;
  setBody: (patch: Record<string, string>) => void;
  deviceId: string;
}) {
  const qc = useQueryClient();
  const [scope, setScope] = useState<ScopeChoice>('employee');
  const [confirm, setConfirm] = useState<PendingAction | null>(null);
  const [helpOpen, setHelpOpen] = useState(false);

  const tab = CATEGORIES.some((c) => c.tab === body.tab) ? body.tab : 'external';
  const statusFilter = (['all', 'connected', 'blocked', 'alert', 'none'].includes(body.status) ? body.status : 'all') as StatusFilter;
  const showDisconnected = body.disconnected === '1';

  // Local debounced mirror of the URL search value.
  const [searchInput, setSearchInput] = useState(body.q ?? '');
  useEffect(() => {
    const t = setTimeout(() => {
      if (searchInput !== (body.q ?? '')) setBody({ q: searchInput });
    }, 400);
    return () => clearTimeout(t);
  }, [searchInput, body.q, setBody]);

  const portsQ = useQuery({
    queryKey: ['machine-ports', employee.employeeId, deviceId],
    queryFn: () => portControlApi.listPorts(employee.employeeId, deviceId || undefined),
    refetchInterval: 30_000,
  });
  const rulesQ = useQuery({
    queryKey: ['device-control-rules'],
    queryFn: () => portControlApi.listRules(),
  });
  // Same Network Lockdown switch as Allowlists → Web (blocks internet, keeps server/LAN).
  const lockdownQ = useQuery({
    queryKey: ['dlp-lockdown'],
    queryFn: () => dlpApi.lockdown.get(),
  });
  const internetMutation = useMutation({
    mutationFn: async (lockdown: boolean) => {
      const res = await dlpApi.lockdown.update({ lockdown });
      const pub = await dlpApi.rules.publish();
      return { res, pub };
    },
    onSuccess: ({ res, pub }) => {
      qc.setQueryData(['dlp-lockdown'], res);
      toast.success(
        res.lockdown
          ? `Internet blocked · policy v${pub.policyVersion}`
          : `Internet allowed · policy v${pub.policyVersion}`,
        {
          description: res.lockdown
            ? 'Outbound web is default-deny. The management server and LAN stay reachable.'
            : 'Browsing is open again (unless Web Blocker rules still apply).',
        },
      );
    },
    onError: (err: Error) => toast.error('Could not update internet lock', { description: err.message }),
  });

  const rules = useMemo(() => rulesQ.data?.rules ?? [], [rulesQ.data]);

  const ruleAppliesHere = (r: DeviceControlRule, machineId: string) =>
    r.scope === 'global' ||
    (r.scope === 'employee' && r.scopeValue === employee.employeeId) ||
    (r.scope === 'machine' && r.scopeValue === machineId);

  const instanceRuleFor = (port: MachinePort): DeviceControlRule | undefined => {
    let best: DeviceControlRule | undefined;
    for (const r of rules) {
      if (r.targetKind !== 'instance') continue;
      if (r.targetValue.toLowerCase() !== port.instanceId.toLowerCase()) continue;
      if (!ruleAppliesHere(r, port.machineId)) continue;
      if (!best || scopeRank(r.scope) > scopeRank(best.scope)) best = r;
    }
    return best;
  };

  const classRuleFor = (port: MachinePort): DeviceControlRule | undefined => {
    const guid = (port.classGuid || '').toLowerCase();
    const cls = (port.pnpClass || '').toLowerCase();
    let best: DeviceControlRule | undefined;
    for (const r of rules) {
      if (r.targetKind !== 'class') continue;
      if (!ruleAppliesHere(r, port.machineId)) continue;
      const tv = r.targetValue.toLowerCase();
      const match = (guid && tv === guid) || (cls && (tv === cls || r.pnpClass?.toLowerCase() === cls));
      if (!match) continue;
      if (!best || scopeRank(r.scope) > scopeRank(best.scope)) best = r;
    }
    return best;
  };

  /** Effective rule for a row: a device rule wins over a class rule. */
  const ruleFor = (port: MachinePort) => instanceRuleFor(port) ?? classRuleFor(port);

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ['device-control-rules'] });
    qc.invalidateQueries({ queryKey: ['machine-ports', employee.employeeId] });
  };

  const scopeValueFor = (machineId: string) =>
    scope === 'employee' ? employee.employeeId : scope === 'machine' ? machineId : '';

  const createInstanceRule = useMutation({
    mutationFn: (v: { port: MachinePort; action: 'block' | 'alert' }) =>
      portControlApi.createRule({
        targetKind: 'instance',
        targetValue: v.port.instanceId,
        label: v.port.friendlyName,
        pnpClass: v.port.pnpClass,
        action: v.action,
        scope,
        scopeValue: scopeValueFor(v.port.machineId),
      }),
    onSuccess: (_d, v) => {
      invalidate();
      toast.success(v.action === 'block' ? 'Device blocked' : 'Monitoring enabled', {
        description: `${v.port.friendlyName || 'Device'} · ${scopeLabel(scope)}. The PC applies it within about a minute.`,
      });
    },
    onError: (err: Error) => toast.error('Could not save the rule', { description: err.message }),
  });

  const createClassRule = useMutation({
    mutationFn: (v: { port: MachinePort; action: 'block' | 'alert' }) => {
      const targetValue = v.port.classGuid || v.port.pnpClass;
      if (!targetValue) throw new Error('This device type has no Windows class ID');
      return portControlApi.createRule({
        targetKind: 'class',
        targetValue,
        label: `All ${classInfo(v.port.pnpClass).label}`,
        pnpClass: v.port.pnpClass,
        action: v.action,
        scope,
        scopeValue: scopeValueFor(v.port.machineId),
      });
    },
    onSuccess: (_d, v) => {
      invalidate();
      toast.success(`All ${classInfo(v.port.pnpClass).label.toLowerCase()} blocked`, {
        description: `${scopeLabel(scope)}. Current and future devices of this type are covered.`,
      });
    },
    onError: (err: Error) => toast.error('Could not save the rule', { description: err.message }),
  });

  const removeRule = useMutation({
    mutationFn: (id: number) => portControlApi.removeRule(id),
    onSuccess: () => {
      invalidate();
      toast.success('Rule removed', { description: 'The PC re-enables the device within about a minute.' });
    },
    onError: (err: Error) => toast.error('Could not remove the rule', { description: err.message }),
  });

  const busy = createInstanceRule.isPending || createClassRule.isPending || removeRule.isPending
    || internetMutation.isPending;
  const internetBlocked = lockdownQ.data?.lockdown === true;

  if (portsQ.isLoading || rulesQ.isLoading) {
    return <div className="flex items-center justify-center min-h-[300px]"><Loader2 className="w-7 h-7 animate-spin text-primary" /></div>;
  }

  if (portsQ.error) {
    return (
      <div className="rounded-xl border border-destructive/30 bg-destructive/5 p-6 text-sm text-destructive">
        Could not load the device inventory: {(portsQ.error as Error).message}
      </div>
    );
  }

  const ports = portsQ.data?.ports ?? [];
  const lastScan = ports.reduce<string | undefined>((max, p) => (!max || p.lastSeenAt > max ? p.lastSeenAt : max), undefined);

  const stats = {
    total: ports.length,
    connected: ports.filter((p) => p.present).length,
    blocked: ports.filter((p) => ruleFor(p)?.action === 'block').length,
    pending: ports.filter((p) => enforcementFor(p, ruleFor(p)) === 'pending').length,
    alert: ports.filter((p) => ruleFor(p)?.action === 'alert').length,
  };

  const q = (body.q ?? '').trim().toLowerCase();
  const matches = (p: MachinePort) => {
    if (!showDisconnected && !p.present && !ruleFor(p)) return false;
    if (q) {
      const hay = `${p.friendlyName} ${p.manufacturer} ${p.pnpClass} ${classInfo(p.pnpClass).label} ${p.instanceId}`.toLowerCase();
      if (!hay.includes(q)) return false;
    }
    const r = ruleFor(p);
    switch (statusFilter) {
      case 'connected': return p.present;
      case 'blocked': return r?.action === 'block';
      case 'alert': return r?.action === 'alert';
      case 'none': return !r;
      default: return true;
    }
  };

  const scopeOptions: { key: ScopeChoice; label: string; icon: React.ElementType; hint: string }[] = [
    { key: 'employee', label: employee.name, icon: User, hint: `Follows ${employee.name} on every computer they sign in to.` },
    { key: 'machine', label: 'This computer', icon: Monitor, hint: 'Only on the computer the device is attached to, whoever uses it.' },
    { key: 'global', label: 'Whole company', icon: Building2, hint: 'Every employee on every computer.' },
  ];

  const runConfirmed = () => {
    if (!confirm) return;
    if (confirm.kind === 'instance') createInstanceRule.mutate({ port: confirm.port, action: 'block' });
    else createClassRule.mutate({ port: confirm.port, action: 'block' });
    setConfirm(null);
  };

  const confirmRisk = confirm ? classInfo(confirm.port.pnpClass).risky : undefined;

  return (
    <div className="space-y-5">
      {/* ── Summary ─────────────────────────────────────────────── */}
      <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
        <Stat icon={Plug} label="Devices detected" value={stats.total} sub={lastScan ? `Last scan ${formatRelative(lastScan)}` : 'No scan yet'} />
        <Stat icon={CircleCheck} label="Connected now" value={stats.connected} sub={`${stats.total - stats.connected} not connected`} tone="success" />
        <Stat icon={ShieldOff} label="Blocked" value={stats.blocked} sub={stats.pending ? `${stats.pending} waiting for the PC` : 'All applied'} tone={stats.blocked ? 'danger' : 'muted'} />
        <Stat icon={Eye} label="Monitored" value={stats.alert} sub="Usage is logged, not blocked" tone={stats.alert ? 'warning' : 'muted'} />
      </div>

      {/* ── Rule scope + filters ───────────────────────────────── */}
      <div className="rounded-xl border border-border bg-card">
        <div className="p-4 border-b border-border">
          <div className="flex flex-col lg:flex-row lg:items-center gap-3">
            <div className="min-w-0">
              <p className="text-sm font-semibold">Who new rules apply to</p>
              <p className="text-xs text-muted-foreground mt-0.5">{scopeOptions.find((o) => o.key === scope)?.hint}</p>
            </div>
            <div className="lg:ml-auto inline-flex rounded-lg border border-border p-1 bg-muted/40" role="radiogroup" aria-label="Rule scope">
              {scopeOptions.map((o) => {
                const Icon = o.icon;
                const active = scope === o.key;
                return (
                  <button
                    key={o.key}
                    role="radio"
                    aria-checked={active}
                    onClick={() => setScope(o.key)}
                    className={`inline-flex items-center gap-1.5 text-xs font-medium px-3 py-1.5 rounded-md transition-colors max-w-[180px]
                      ${active ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}`}
                  >
                    <Icon className="w-3.5 h-3.5 flex-shrink-0" />
                    <span className="truncate">{o.label}</span>
                  </button>
                );
              })}
            </div>
          </div>
        </div>

        <div className="p-4 flex flex-col md:flex-row md:items-center gap-3">
          <div className="relative flex-1">
            <Search className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
              placeholder="Search by device name, manufacturer, type or instance ID"
              className="pl-9"
            />
          </div>
          <Select value={statusFilter} onValueChange={(v) => setBody({ status: v === 'all' ? '' : v })}>
            <SelectTrigger className="md:w-[180px]"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All devices</SelectItem>
              <SelectItem value="connected">Connected now</SelectItem>
              <SelectItem value="blocked">Blocked</SelectItem>
              <SelectItem value="alert">Monitored</SelectItem>
              <SelectItem value="none">No rule</SelectItem>
            </SelectContent>
          </Select>
          <label className="inline-flex items-center gap-2 text-xs text-muted-foreground whitespace-nowrap cursor-pointer">
            <Switch checked={showDisconnected} onCheckedChange={(c) => setBody({ disconnected: c ? '1' : '' })} />
            Show disconnected
          </label>
        </div>
      </div>

      {/* ── How it works ───────────────────────────────────────── */}
      <div className="rounded-xl border border-border bg-card">
        <button
          onClick={() => setHelpOpen((o) => !o)}
          className="w-full flex items-center gap-2 px-4 py-3 text-sm font-medium text-left"
          aria-expanded={helpOpen}
        >
          <Info className="w-4 h-4 text-primary" />
          How blocking works
          <ChevronDown className={`w-4 h-4 ml-auto text-muted-foreground transition-transform ${helpOpen ? 'rotate-180' : ''}`} />
        </button>
        {helpOpen && (
          <div className="px-4 pb-4 grid sm:grid-cols-2 gap-3 text-xs">
            <Legend icon={Ban} tone="danger" title="Block" text="Disables this one device. If it is plugged into a different USB port, Windows may see it as a new device." />
            <Legend icon={ShieldOff} tone="danger" title="Block all of this type" text="Blocks every current and future device of this Windows device type, on any port." />
            <Legend icon={ShieldAlert} tone="warning" title="Monitor" text="The device keeps working; its use is logged as an alert." />
            <Legend icon={CircleCheck} tone="success" title="Unblock" text="Removes the rule. The PC re-enables the device automatically." />
            <div className="sm:col-span-2 flex gap-2 rounded-lg bg-muted/50 p-3 text-muted-foreground">
              <AlertTriangle className="w-4 h-4 flex-shrink-0 text-amber-600" />
              <span>
                Displays (HDMI, DisplayPort, VGA, DVI) and the internal system disk can only be monitored. Disabling them would
                make the computer unusable. Windows may refuse to disable a keyboard or mouse that is in use until the PC restarts;
                those show as <b>Waiting for PC</b>.
              </span>
            </div>
          </div>
        )}
      </div>

      {/* ── Inventory ──────────────────────────────────────────── */}
      {ports.length === 0 ? (
        <div className="rounded-xl border border-dashed border-border bg-card p-10 text-center">
          <Plug className="w-8 h-8 mx-auto text-muted-foreground/60" />
          <p className="mt-3 text-sm font-medium">No device scan yet</p>
          <p className="mt-1 text-xs text-muted-foreground max-w-md mx-auto">
            The desktop agent uploads the device list shortly after it starts. If this stays empty, the agent on the
            employee&rsquo;s PC may be offline or out of date.
          </p>
        </div>
      ) : (
        <Tabs value={tab} onValueChange={(v) => setBody({ tab: v === 'external' ? '' : v })}>
          <TabsList className="h-auto flex-wrap">
            {CATEGORIES.map((c) => {
              const Icon = c.icon;
              const n = ports.filter((p) => p.category === c.key && matches(p)).length;
              return (
                <TabsTrigger key={c.tab} value={c.tab} className="gap-1.5">
                  <Icon className="w-3.5 h-3.5" />
                  {c.label}
                  <span className="ml-1 rounded-full bg-muted px-1.5 text-[10px] font-semibold text-muted-foreground">{n}</span>
                </TabsTrigger>
              );
            })}
          </TabsList>

          {CATEGORIES.map((cat) => {
            const rows = ports.filter((p) => p.category === cat.key && matches(p));
            const groups = new Map<string, MachinePort[]>();
            for (const p of rows) {
              const k = (p.classGuid || p.pnpClass || `id:${p.instanceId}`).toLowerCase();
              groups.set(k, [...(groups.get(k) ?? []), p]);
            }
            const ordered = [...groups.entries()].sort((a, b) =>
              classInfo(a[1][0].pnpClass).label.localeCompare(classInfo(b[1][0].pnpClass).label));

            return (
              <TabsContent key={cat.tab} value={cat.tab} className="space-y-4 mt-4">
                <p className="text-xs text-muted-foreground">{cat.desc}</p>
                {ordered.length === 0 ? (
                  <div className="rounded-xl border border-dashed border-border bg-card p-8 text-center text-xs text-muted-foreground">
                    No devices match the current filters.
                  </div>
                ) : (
                  ordered.map(([key, groupPorts]) => (
                    <ClassGroup
                      key={key}
                      ports={groupPorts}
                      classRule={classRuleFor(groupPorts[0])}
                      ruleFor={ruleFor}
                      busy={busy}
                      internetBlocked={internetBlocked}
                      onToggleInternet={(on) => internetMutation.mutate(on)}
                      onBlockAll={(p) => setConfirm({ kind: 'class', port: p, title: classInfo(p.pnpClass).label, count: groupPorts.length })}
                      onBlock={(p) => setConfirm({ kind: 'instance', port: p, title: p.friendlyName || p.instanceId, count: 1 })}
                      onAlert={(p) => createInstanceRule.mutate({ port: p, action: 'alert' })}
                      onRemove={(id) => removeRule.mutate(id)}
                    />
                  ))
                )}
              </TabsContent>
            );
          })}
        </Tabs>
      )}

      {/* ── Block confirmation ─────────────────────────────────── */}
      <AlertDialog open={!!confirm} onOpenChange={(o) => !o && setConfirm(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              {confirm?.kind === 'class' ? `Block all ${confirm.title.toLowerCase()}?` : `Block ${confirm?.title}?`}
            </AlertDialogTitle>
            <AlertDialogDescription asChild>
              <div className="space-y-3 text-sm">
                <p>
                  {confirm?.kind === 'class'
                    ? 'Every current and future device of this type will be disabled, on any port.'
                    : 'This device will be disabled on the computer.'}
                </p>
                <div className="rounded-lg border border-border bg-muted/40 p-3 text-xs space-y-1">
                  <div className="flex justify-between gap-3"><span className="text-muted-foreground">Applies to</span><span className="font-medium text-foreground">{scopeLabel(scope)}{scope === 'employee' ? ` (${employee.name})` : ''}</span></div>
                  <div className="flex justify-between gap-3"><span className="text-muted-foreground">Takes effect</span><span className="font-medium text-foreground">Within about a minute</span></div>
                  {confirm?.kind === 'class' && (
                    <div className="flex justify-between gap-3"><span className="text-muted-foreground">Devices on this PC</span><span className="font-medium text-foreground">{confirm.count}</span></div>
                  )}
                </div>
                {confirmRisk && (
                  <div className="flex gap-2 rounded-lg border border-amber-500/30 bg-amber-500/10 p-3 text-xs text-amber-700 dark:text-amber-400">
                    <AlertTriangle className="w-4 h-4 flex-shrink-0" />
                    <span>{confirmRisk}</span>
                  </div>
                )}
              </div>
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={runConfirmed} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">
              Block
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}

// ─── Building blocks ────────────────────────────────────────────────────────

const TONES = {
  muted: { text: 'text-foreground', icon: 'text-muted-foreground bg-muted' },
  success: { text: 'text-foreground', icon: 'text-emerald-600 bg-emerald-500/10' },
  danger: { text: 'text-red-600', icon: 'text-red-600 bg-red-500/10' },
  warning: { text: 'text-amber-600', icon: 'text-amber-600 bg-amber-500/10' },
} as const;

function Stat({
  icon: Icon, label, value, sub, tone = 'muted',
}: { icon: React.ElementType; label: string; value: number; sub?: string; tone?: keyof typeof TONES }) {
  return (
    <div className="rounded-xl border border-border bg-card p-4 flex items-start gap-3">
      <div className={`w-9 h-9 rounded-lg flex items-center justify-center flex-shrink-0 ${TONES[tone].icon}`}>
        <Icon className="w-4 h-4" />
      </div>
      <div className="min-w-0">
        <div className={`text-2xl font-semibold leading-none ${TONES[tone].text}`}>{value}</div>
        <div className="text-xs font-medium text-foreground mt-1.5">{label}</div>
        {sub && <div className="text-[11px] text-muted-foreground mt-0.5 truncate">{sub}</div>}
      </div>
    </div>
  );
}

function Legend({ icon: Icon, tone, title, text }: { icon: React.ElementType; tone: keyof typeof TONES; title: string; text: string }) {
  return (
    <div className="flex gap-2.5 rounded-lg border border-border p-3">
      <div className={`w-7 h-7 rounded-md flex items-center justify-center flex-shrink-0 ${TONES[tone].icon}`}>
        <Icon className="w-3.5 h-3.5" />
      </div>
      <div>
        <div className="font-medium text-foreground">{title}</div>
        <div className="text-muted-foreground mt-0.5">{text}</div>
      </div>
    </div>
  );
}

function ClassGroup({
  ports, classRule, ruleFor, busy, internetBlocked, onToggleInternet, onBlockAll, onBlock, onAlert, onRemove,
}: {
  ports: MachinePort[];
  classRule?: DeviceControlRule;
  ruleFor: (p: MachinePort) => DeviceControlRule | undefined;
  busy: boolean;
  internetBlocked?: boolean;
  onToggleInternet?: (on: boolean) => void;
  onBlockAll: (p: MachinePort) => void;
  onBlock: (p: MachinePort) => void;
  onAlert: (p: MachinePort) => void;
  onRemove: (id: number) => void;
}) {
  const sample = ports[0];
  const info = classInfo(sample.pnpClass);
  const Icon = info.icon;
  const isNet = sample.pnpClass === 'Net';
  const alertOnly = ALERT_ONLY_CLASSES.has(sample.pnpClass) || !sample.blockable;
  const canClassBlock = !alertOnly && !!(sample.classGuid || sample.pnpClass);

  return (
    <section className="rounded-xl border border-border bg-card overflow-hidden">
      <header className="flex flex-wrap items-center gap-3 px-4 py-3 border-b border-border bg-muted/30">
        <div className="w-9 h-9 rounded-lg bg-primary/10 text-primary flex items-center justify-center flex-shrink-0">
          <Icon className="w-4 h-4" />
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2 flex-wrap">
            <h3 className="text-sm font-semibold">{info.label}</h3>
            <span className="text-xs text-muted-foreground">{ports.length} device{ports.length === 1 ? '' : 's'}</span>
            {classRule && <PolicyBadge rule={classRule} />}
            {isNet && internetBlocked ? (
              <span className="inline-flex items-center gap-1 text-[11px] font-medium px-2 py-0.5 rounded-full bg-red-500/10 text-red-600">
                <ShieldOff className="w-3 h-3" /> Internet blocked
              </span>
            ) : alertOnly ? (
              <span className="inline-flex items-center gap-1 text-[11px] font-medium px-2 py-0.5 rounded-full bg-muted text-muted-foreground">
                <Eye className="w-3 h-3" /> Monitor only
              </span>
            ) : null}
          </div>
          <p className="text-xs text-muted-foreground mt-0.5">{info.desc}</p>
          {isNet && (
            <p className="text-xs text-amber-700 dark:text-amber-400 mt-1.5 flex flex-wrap items-center gap-x-1 gap-y-0.5">
              <AlertTriangle className="w-3.5 h-3.5 flex-shrink-0" />
              Adapters stay enabled. Internet lock uses Network Lockdown (same as{' '}
              <Link href="/allowlists/web" className="underline font-medium inline-flex items-center gap-0.5">
                Allowlists → Web
                <ExternalLink className="w-3 h-3" />
              </Link>
              ) — company-wide; server and LAN stay reachable.
            </p>
          )}
        </div>
        {classRule ? (
          <Button variant="outline" size="sm" disabled={busy} onClick={() => onRemove(classRule.id)}>
            <CircleCheck className="w-3.5 h-3.5 mr-1.5" /> Unblock all
          </Button>
        ) : canClassBlock ? (
          <Button
            variant="outline"
            size="sm"
            disabled={busy}
            onClick={() => onBlockAll(sample)}
            className="border-red-500/40 text-red-600 hover:bg-red-500/10 hover:text-red-700"
          >
            <ShieldOff className="w-3.5 h-3.5 mr-1.5" /> Block all of this type
          </Button>
        ) : isNet && onToggleInternet ? (
          internetBlocked ? (
            <Button variant="outline" size="sm" disabled={busy} onClick={() => onToggleInternet(false)}>
              <CircleCheck className="w-3.5 h-3.5 mr-1.5" /> Allow internet
            </Button>
          ) : (
            <Button
              size="sm"
              disabled={busy}
              onClick={() => onToggleInternet(true)}
              className="bg-red-600 text-white hover:bg-red-700"
            >
              <ShieldOff className="w-3.5 h-3.5 mr-1.5" /> Block internet
            </Button>
          )
        ) : null}
      </header>

      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left text-[11px] font-medium uppercase tracking-wide text-muted-foreground border-b border-border">
              <th className="px-4 py-2 font-medium">Device</th>
              <th className="px-4 py-2 font-medium hidden md:table-cell">Connection</th>
              <th className="px-4 py-2 font-medium">Status</th>
              <th className="px-4 py-2 font-medium">Rule</th>
              <th className="px-4 py-2 font-medium">On the PC</th>
              <th className="px-4 py-2 font-medium text-right">Actions</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-border">
            {ports.map((p) => (
              <PortRow
                key={p.id}
                port={p}
                rule={ruleFor(p)}
                busy={busy}
                onBlock={() => onBlock(p)}
                onAlert={() => onAlert(p)}
                onRemove={onRemove}
              />
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function PolicyBadge({ rule }: { rule: DeviceControlRule }) {
  const isClass = rule.targetKind === 'class';
  const style =
    rule.action === 'block' ? 'bg-red-500/10 text-red-600'
      : rule.action === 'alert' ? 'bg-amber-500/10 text-amber-600'
        : 'bg-emerald-500/10 text-emerald-600';
  const Icon = rule.action === 'block' ? ShieldOff : rule.action === 'alert' ? ShieldAlert : ShieldCheck;
  const verb = rule.action === 'block' ? 'Blocked' : rule.action === 'alert' ? 'Monitored' : 'Allowed';
  return (
    <span
      className={`inline-flex items-center gap-1 text-[11px] font-medium px-2 py-0.5 rounded-full whitespace-nowrap ${style}`}
      title={`${verb} ${isClass ? 'for the whole device type' : 'for this device'} · ${scopeLabel(rule.scope)}`}
    >
      <Icon className="w-3 h-3" />
      {verb}{isClass ? ' (type)' : ''}
      <span className="opacity-70 font-normal">· {scopeLabel(rule.scope)}</span>
    </span>
  );
}

const ENFORCEMENT_UI: Record<Enforcement, { label: string; cls: string; icon: React.ElementType; hint: string }> = {
  enforced: { label: 'Disabled', cls: 'text-red-600', icon: ShieldOff, hint: 'The PC reports this device as disabled.' },
  pending: { label: 'Waiting for PC', cls: 'text-amber-600', icon: Clock, hint: 'The rule was sent but the PC still reports the device as working. Windows may need a restart for in-use keyboards and mice.' },
  'on-connect': { label: 'Applies when plugged in', cls: 'text-muted-foreground', icon: Plug, hint: 'The device is not connected. The block applies when it is plugged in again.' },
  monitoring: { label: 'Logging use', cls: 'text-amber-600', icon: Eye, hint: 'The device works normally; its use is recorded as an alert.' },
  none: { label: '—', cls: 'text-muted-foreground', icon: CircleSlash, hint: '' },
};

function PortRow({
  port, rule, busy, onBlock, onAlert, onRemove,
}: {
  port: MachinePort;
  rule?: DeviceControlRule;
  busy: boolean;
  onBlock: () => void;
  onAlert: () => void;
  onRemove: (id: number) => void;
}) {
  const enf = ENFORCEMENT_UI[enforcementFor(port, rule)];
  const EnfIcon = enf.icon;
  const blockable = port.blockable && !ALERT_ONLY_CLASSES.has(port.pnpClass);
  const coveredByClass = rule?.targetKind === 'class';

  return (
    <tr className="hover:bg-muted/30 transition-colors align-top">
      <td className="px-4 py-3 min-w-[220px]">
        <div className="font-medium">{port.friendlyName || 'Unnamed device'}</div>
        <div className="text-[11px] text-muted-foreground mt-0.5">{port.manufacturer || 'Unknown manufacturer'}</div>
        <div className="text-[10px] text-muted-foreground/70 font-mono mt-0.5 truncate max-w-[320px]" title={port.instanceId}>
          {port.instanceId}
        </div>
      </td>
      <td className="px-4 py-3 text-xs text-muted-foreground hidden md:table-cell whitespace-nowrap">
        {port.busType || '—'}
      </td>
      <td className="px-4 py-3 whitespace-nowrap">
        {port.present ? (
          <span className="inline-flex items-center gap-1.5 text-xs text-emerald-600">
            <span className="w-1.5 h-1.5 rounded-full bg-emerald-500" /> Connected
          </span>
        ) : (
          <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground">
            <span className="w-1.5 h-1.5 rounded-full bg-muted-foreground/40" /> Not connected
          </span>
        )}
        <div className="text-[10px] text-muted-foreground mt-0.5">Seen {formatRelative(port.lastSeenAt)}</div>
      </td>
      <td className="px-4 py-3">
        {rule ? <PolicyBadge rule={rule} /> : <span className="text-xs text-muted-foreground">No rule</span>}
      </td>
      <td className="px-4 py-3 whitespace-nowrap">
        <span className={`inline-flex items-center gap-1 text-xs ${enf.cls}`} title={enf.hint}>
          {enforcementFor(port, rule) !== 'none' && <EnfIcon className="w-3.5 h-3.5" />}
          {enf.label}
        </span>
      </td>
      <td className="px-4 py-3">
        <div className="flex items-center justify-end gap-1.5">
          {rule && !coveredByClass ? (
            <Button variant="outline" size="sm" className="h-8" disabled={busy} onClick={() => onRemove(rule.id)}>
              <CircleCheck className="w-3.5 h-3.5 mr-1.5" />
              {rule.action === 'alert' ? 'Stop monitoring' : 'Unblock'}
            </Button>
          ) : coveredByClass ? (
            <span className="text-[11px] text-muted-foreground text-right">Covered by the type rule above</span>
          ) : (
            <>
              {blockable && (
                <Button size="sm" className="h-8 bg-red-600 text-white hover:bg-red-700" disabled={busy} onClick={onBlock}>
                  <Ban className="w-3.5 h-3.5 mr-1.5" /> Block
                </Button>
              )}
              <Button variant="outline" size="sm" className="h-8" disabled={busy} onClick={onAlert}
                title={blockable ? 'Keep the device working and log its use' : 'This device type cannot be safely disabled'}>
                <Eye className="w-3.5 h-3.5 mr-1.5" /> Monitor
              </Button>
            </>
          )}
        </div>
      </td>
    </tr>
  );
}
