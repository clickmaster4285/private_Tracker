'use client';

import { Suspense, useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import {
  Search, Loader2, ShieldCheck, ShieldAlert, ShieldOff, ShieldQuestion, Bug, Clock,
  ScanLine, ScanSearch, RefreshCw, Trash2, Check, FolderSearch, History, Info, ChevronDown,
} from 'lucide-react';
import { keepPreviousData, useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { CustomScanDialog } from '@/components/antivirus/CustomScanDialog';
import { CommandHistoryDialog } from '@/components/antivirus/CommandHistoryDialog';
import { COMMAND_INFO, HEALTH_HELP } from '@/components/antivirus/labels';
import {
  antivirusApi,
  type AntivirusCommandType,
  type AntivirusHealth,
  type AntivirusHealthCounts,
  type AntivirusStatusRow,
} from '@/lib/api';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import { formatDateTime, formatRelative } from '@/lib/format';

const PER_PAGE = 30;

const HEALTH: Record<AntivirusHealth, { label: string; cls: string }> = {
  protected: { label: 'Protected', cls: 'bg-success/15 text-success' },
  outdated: { label: 'Signatures outdated', cls: 'bg-warning/15 text-warning' },
  realtime_off: { label: 'Real-time OFF', cls: 'bg-destructive/15 text-destructive' },
  disabled: { label: 'Antivirus disabled', cls: 'bg-destructive/15 text-destructive' },
  third_party: { label: 'Third-party AV', cls: 'bg-primary/15 text-primary' },
  unavailable: { label: 'Unavailable', cls: 'bg-muted text-muted-foreground' },
  unknown: { label: 'Unknown', cls: 'bg-muted text-muted-foreground' },
};

/** Filter chips — `stale` is server-computed (no report for 30 min). */
const CHIPS: { key: string; label: string; count: (c: AntivirusHealthCounts) => number }[] = [
  { key: '', label: 'All', count: (c) => c.total },
  { key: 'protected', label: 'Protected', count: (c) => c.protected },
  { key: 'realtime_off', label: 'Real-time OFF', count: (c) => c.realtimeOff },
  { key: 'disabled', label: 'Disabled', count: (c) => c.disabled },
  { key: 'outdated', label: 'Outdated', count: (c) => c.outdated },
  { key: 'third_party', label: 'Third-party', count: (c) => c.thirdParty },
  { key: 'stale', label: 'Not reporting', count: (c) => c.stale },
];

function Toggle({ on, label }: { on: boolean; label: string }) {
  return (
    <span
      title={`${label}: ${on ? 'on' : 'off'}`}
      className={`inline-block px-1.5 py-0.5 rounded text-[10px] font-medium mr-1 mb-1 ${
        on ? 'bg-success/15 text-success' : 'bg-destructive/15 text-destructive'
      }`}
    >
      {label}
    </span>
  );
}

/** off | audit | block mode chip (ASR / CFA / network protection). */
function ModeBadge({ label, mode }: { label: string; mode: 'off' | 'audit' | 'block' }) {
  const cls =
    mode === 'block'
      ? 'bg-success/15 text-success'
      : mode === 'audit'
        ? 'bg-warning/15 text-warning'
        : 'bg-destructive/15 text-destructive';
  return (
    <span title={`${label}: ${mode}`} className={`inline-block px-1.5 py-0.5 rounded text-[10px] font-medium mr-1 mb-1 ${cls}`}>
      {label}: {mode}
    </span>
  );
}

/** Advanced hardening chips for one machine. */
function HardeningChips({ r }: { r: AntivirusStatusRow }) {
  const asrMode: 'off' | 'audit' | 'block' =
    r.asrBlock > 0 ? 'block' : r.asrConfigured > 0 ? 'audit' : 'off';
  return (
    <div className="max-w-[260px]">
      <span
        title={`${r.asrBlock} of ${r.asrConfigured} ASR rules in block mode`}
        className={`inline-block px-1.5 py-0.5 rounded text-[10px] font-medium mr-1 mb-1 ${
          asrMode === 'block' ? 'bg-success/15 text-success' : asrMode === 'audit' ? 'bg-warning/15 text-warning' : 'bg-destructive/15 text-destructive'
        }`}
      >
        ASR {r.asrBlock}/{r.asrConfigured || 16}
      </span>
      <ModeBadge label="Folder" mode={r.controlledFolderAccess} />
      <ModeBadge label="Network" mode={r.networkProtectionMode} />
      <Toggle on={r.exploitProtectionEnabled} label="Exploit prot." />
      <Toggle on={r.memoryIntegrityEnabled} label="Memory integrity" />
      <Toggle on={r.credentialGuardEnabled} label="Cred Guard" />
      <Toggle on={r.smartScreenEnabled} label="SmartScreen" />
      <Toggle on={r.emailScanEnabled} label="Email scan" />
      {r.quarantinedThreatCount > 0 && (
        <span
          title="Items currently in quarantine"
          className="inline-block px-1.5 py-0.5 rounded text-[10px] font-medium mr-1 mb-1 bg-destructive/15 text-destructive"
        >
          Quarantine: {r.quarantinedThreatCount}
        </span>
      )}
    </div>
  );
}

const REMOVE_CONFIRM =
  'Delete every threat Microsoft Defender has found on this PC, including quarantined items? This cannot be undone.';

/**
 * Compact per-row actions: one "Actions" button that opens a dropdown, so each record row
 * stays a single line tall. A short-lived green pill confirms the last queued command.
 */
function ActionsCell({ r }: { r: AntivirusStatusRow }) {
  const qc = useQueryClient();
  const [feedback, setFeedback] = useState<string | null>(null);
  const [customOpen, setCustomOpen] = useState(false);
  const [historyOpen, setHistoryOpen] = useState(false);
  const mutation = useMutation({
    mutationFn: (type: AntivirusCommandType) =>
      antivirusApi.queueCommand({ employeeId: r.employeeId, machineId: r.machineId, type }),
    onSuccess: (data, type) => {
      setFeedback(`${COMMAND_INFO[type].label}: ${data.reused ? 'already queued' : 'queued'}`);
      setTimeout(() => setFeedback(null), 3000);
      qc.invalidateQueries({ queryKey: ['antivirus-commands', r.employeeId, r.machineId] });
    },
  });

  const run = (type: AntivirusCommandType, confirmMsg?: string) => {
    if (confirmMsg && !window.confirm(confirmMsg)) return;
    mutation.mutate(type);
  };
  const busy = mutation.isPending || r.isStale;
  const staleHint = 'This PC is not reporting (off or offline) — it would not pick the command up';

  const item = (type: AntivirusCommandType, Icon: typeof ScanLine) => (
    <DropdownMenuItem
      disabled={busy}
      onSelect={() => run(type, type === 'remove_threats' ? REMOVE_CONFIRM : undefined)}
      className={`gap-2 ${type === 'remove_threats' ? 'text-destructive focus:text-destructive' : ''}`}
    >
      <Icon className="w-3.5 h-3.5 shrink-0" />
      <div className="flex flex-col">
        <span>{COMMAND_INFO[type].label}</span>
        <span className="text-[10px] text-muted-foreground">{COMMAND_INFO[type].help}</span>
      </div>
    </DropdownMenuItem>
  );

  return (
    <div className="flex flex-col items-start gap-1">
      <DropdownMenu>
        <DropdownMenuTrigger
          title={r.isStale ? staleHint : 'Scan, update or clean this PC'}
          className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg border border-border bg-card text-xs font-medium text-foreground hover:bg-muted/50 transition-colors data-[state=open]:bg-muted/60"
        >
          {mutation.isPending ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <ShieldCheck className="w-3.5 h-3.5 text-primary" />}
          Actions
          <ChevronDown className="w-3.5 h-3.5 text-muted-foreground" />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-60">
          <DropdownMenuLabel className="text-[11px] text-muted-foreground">Scans & maintenance</DropdownMenuLabel>
          {item('scan_quick', ScanLine)}
          {item('scan_full', ScanSearch)}
          <DropdownMenuItem disabled={r.isStale} onSelect={() => setCustomOpen(true)} className="gap-2">
            <FolderSearch className="w-3.5 h-3.5 shrink-0" />
            <div className="flex flex-col">
              <span>Custom scan…</span>
              <span className="text-[10px] text-muted-foreground">{COMMAND_INFO.scan_custom.help}</span>
            </div>
          </DropdownMenuItem>
          {item('update_signatures', RefreshCw)}
          <DropdownMenuSeparator />
          <DropdownMenuItem onSelect={() => setHistoryOpen(true)} className="gap-2 text-primary focus:text-primary">
            <History className="w-3.5 h-3.5 shrink-0" />
            <span>Command history</span>
          </DropdownMenuItem>
          <DropdownMenuSeparator />
          {item('remove_threats', Trash2)}
          {r.isStale && (
            <p className="px-2 py-1.5 text-[10px] text-warning">{staleHint}</p>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      {feedback && (
        <span className="inline-flex items-center gap-1 text-[10px] text-success">
          <Check className="w-3 h-3" /> {feedback}
        </span>
      )}
      {mutation.isError && <span className="text-[10px] text-destructive">Could not queue — try again</span>}

      <CustomScanDialog row={r} open={customOpen} onOpenChange={setCustomOpen} />
      <CommandHistoryDialog row={r} open={historyOpen} onOpenChange={setHistoryOpen} />
    </div>
  );
}

/** Collapsible plain-language explanation of the page. */
function HowItWorks() {
  const [open, setOpen] = useState(false);
  return (
    <div className="bg-card rounded-xl border border-border">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="w-full flex items-center gap-2 px-4 py-3 text-sm font-medium text-foreground"
      >
        <Info className="w-4 h-4 text-primary" /> How to read this page
        <ChevronDown className={`w-4 h-4 ml-auto transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>
      {open && (
        <div className="px-4 pb-4 grid gap-4 md:grid-cols-3 text-xs text-muted-foreground">
          <div>
            <p className="font-semibold text-foreground mb-1">Status</p>
            <ul className="space-y-1">
              {(['protected', 'outdated', 'realtime_off', 'disabled', 'third_party'] as AntivirusHealth[]).map((k) => (
                <li key={k}>
                  <span className={`px-1.5 py-0.5 rounded text-[10px] font-medium mr-1 ${HEALTH[k].cls}`}>{HEALTH[k].label}</span>
                  {HEALTH_HELP[k]}
                </li>
              ))}
              <li><span className="text-warning font-medium">Not reporting</span> — no update from the PC for 30 minutes (switched off, offline or the tracker stopped).</li>
            </ul>
          </div>
          <div>
            <p className="font-semibold text-foreground mb-1">Protection & hardening chips</p>
            <p className="mb-1"><span className="text-success font-medium">Green</span> = on, <span className="text-warning font-medium">amber</span> = audit (logs but does not block), <span className="text-destructive font-medium">red</span> = off.</p>
            <ul className="space-y-1">
              <li><b>Real-time</b> — every file is checked when it is opened, run or downloaded.</li>
              <li><b>Behaviour</b> — watches programs for suspicious actions.</li>
              <li><b>Cloud / PUA</b> — Microsoft cloud lookups / blocks unwanted adware-style apps.</li>
              <li><b>Tamper prot.</b> — Windows stops other programs switching Defender off.</li>
              <li><b>ASR x/16</b> — Attack Surface Reduction rules blocking risky actions (e.g. Office macros starting programs).</li>
              <li><b>Folder</b> — ransomware protection for Documents/Desktop.</li>
            </ul>
          </div>
          <div>
            <p className="font-semibold text-foreground mb-1">Actions</p>
            <ul className="space-y-1">
              {(['scan_quick', 'scan_full', 'scan_custom', 'update_signatures', 'remove_threats'] as AntivirusCommandType[]).map((k) => (
                <li key={k}><b>{COMMAND_INFO[k].label}</b> — {COMMAND_INFO[k].help}</li>
              ))}
              <li><b>History</b> — shows each command, who sent it, and whether it finished or failed.</li>
            </ul>
            <p className="mt-2">Commands wait until the PC is on, then run within about 30 seconds. Buttons are greyed out while a PC is not reporting.</p>
          </div>
        </div>
      )}
    </div>
  );
}

function AntivirusInner() {
  const [filters, setFilters] = useUrlQueryState(
    { search: {}, health: {} },
    { search: '', health: '' },
    { debounceMs: 300 },
  );

  const query = useInfiniteQuery({
    queryKey: ['antivirus-status', filters],
    queryFn: ({ pageParam }) =>
      antivirusApi.listStatus({
        search: filters.search || undefined,
        health: filters.health || undefined,
        page: pageParam,
        perPage: PER_PAGE,
      }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
    placeholderData: keepPreviousData,
    refetchInterval: 60_000,
  });

  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const obs = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && query.hasNextPage && !query.isFetchingNextPage) {
          query.fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    obs.observe(el);
    return () => obs.disconnect();
  }, [query.hasNextPage, query.isFetchingNextPage, query.fetchNextPage]);

  const pages = query.data?.pages;
  const rows: AntivirusStatusRow[] = pages?.flatMap((p) => p.data) ?? [];
  const counts = pages?.[0]?.counts;
  const atRisk = (counts?.realtimeOff ?? 0) + (counts?.disabled ?? 0);

  return (
    <div className="space-y-4 animate-fade-in">
      <div>
        <h1 className="text-xl font-display font-semibold text-foreground">Antivirus</h1>
        <p className="text-sm text-muted-foreground mt-1">
          24/7 real-time scanning on every Windows PC. The tracker keeps Microsoft Defender on at
          full strength, scans every file before it opens, quarantines anything malicious and
          reverts any attempt to switch protection off.
        </p>
      </div>

      <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
        <div className="bg-card rounded-xl border border-border p-4">
          <div className="flex items-center gap-2 text-success"><ShieldCheck className="w-4 h-4" /><span className="text-xs">Protected</span></div>
          <p className="text-2xl font-display font-bold text-foreground mt-1">
            {counts?.protected ?? 0}<span className="text-sm text-muted-foreground font-normal"> / {counts?.total ?? 0}</span>
          </p>
        </div>
        <div className="bg-card rounded-xl border border-border p-4">
          <div className="flex items-center gap-2 text-destructive"><ShieldOff className="w-4 h-4" /><span className="text-xs">Protection off</span></div>
          <p className="text-2xl font-display font-bold text-foreground mt-1">{atRisk}</p>
        </div>
        <Link href="/dlp-alerts?type=malware" className="bg-card rounded-xl border border-border p-4 hover:bg-muted/30">
          <div className="flex items-center gap-2 text-destructive"><Bug className="w-4 h-4" /><span className="text-xs">Threats blocked · 24h</span></div>
          <p className="text-2xl font-display font-bold text-foreground mt-1">{counts?.threats24h ?? 0}</p>
        </Link>
        <Link href="/dlp-alerts?type=av_tamper" className="bg-card rounded-xl border border-border p-4 hover:bg-muted/30">
          <div className="flex items-center gap-2 text-warning"><ShieldAlert className="w-4 h-4" /><span className="text-xs">Tampering reverted · 24h</span></div>
          <p className="text-2xl font-display font-bold text-foreground mt-1">{counts?.tampered24h ?? 0}</p>
        </Link>
      </div>

      <HowItWorks />

      <div className="flex flex-wrap items-center gap-2">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 w-full sm:max-w-xs">
          <Search className="w-4 h-4 text-muted-foreground" />
          <input
            value={filters.search}
            onChange={(e) => setFilters({ search: e.target.value })}
            placeholder="Search employee or PC..."
            className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
          />
        </div>
        {CHIPS.map((c) => (
          <button
            key={c.key || 'all'}
            type="button"
            onClick={() => setFilters({ health: c.key })}
            className={`px-3 py-1.5 rounded-full text-xs font-medium border ${
              filters.health === c.key
                ? 'bg-primary text-primary-foreground border-primary'
                : 'bg-card text-muted-foreground border-border hover:bg-muted/40'
            }`}
          >
            {c.label} {counts ? `(${c.count(counts)})` : ''}
          </button>
        ))}
      </div>

      {query.isLoading && (
        <div className="flex justify-center py-12"><Loader2 className="w-6 h-6 animate-spin text-primary" /></div>
      )}
      {query.isError && <p className="text-sm text-destructive">Failed to load antivirus status</p>}

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[960px]">
          <thead>
            <tr className="border-b border-border">
              {['Employee / PC', 'Status', 'Protection', 'Hardening', 'Signatures', 'Last quick scan', 'Threats 24h', 'Tampering', 'Last report', 'Actions'].map((h) => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => {
              const h = HEALTH[r.health] ?? HEALTH.unknown;
              return (
                <tr key={r.id} className="border-b border-border last:border-0 hover:bg-muted/30 align-top">
                  <td className="px-4 py-3 text-sm">
                    <div className="font-medium text-foreground">{r.employeeName || r.employeeId}</div>
                    <div className="text-xs text-muted-foreground">
                      {r.employeeName || r.employeeId} ({r.hostname || r.machineId || 'unknown machine'})
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    <span title={HEALTH_HELP[r.health] ?? ''} className={`px-2.5 py-1 rounded-full text-xs font-medium cursor-help ${h.cls}`}>{h.label}</span>
                    {r.error && (
                      <div className="text-[11px] text-destructive mt-1 max-w-[180px] break-words" title={r.error}>
                        Problem reading status: {r.error.length > 80 ? r.error.slice(0, 80) + '…' : r.error}
                      </div>
                    )}
                    {r.health === 'third_party' && r.thirdPartyProducts && (
                      <div className="text-xs text-muted-foreground mt-1">{r.thirdPartyProducts}</div>
                    )}
                    {r.isStale && (
                      <div className="flex items-center gap-1 text-xs text-warning mt-1">
                        <Clock className="w-3 h-3" /> Not reporting
                      </div>
                    )}
                  </td>
                  <td className="px-4 py-3 max-w-[220px]">
                    <Toggle on={r.realTimeEnabled} label="Real-time" />
                    <Toggle on={r.behaviorMonitorEnabled} label="Behaviour" />
                    <Toggle on={r.ioavEnabled} label="Downloads" />
                    <Toggle on={r.cloudEnabled} label="Cloud" />
                    <Toggle on={r.puaEnabled} label="PUA" />
                    <Toggle on={r.networkProtectionEnabled} label="Network" />
                    <Toggle on={r.tamperProtected} label="Tamper prot." />
                  </td>
                  <td className="px-4 py-3">
                    <HardeningChips r={r} />
                  </td>
                  <td className="px-4 py-3 text-sm">
                    <div className="text-foreground">{r.signatureVersion || '—'}</div>
                    <div className={`text-xs ${r.signatureAgeDays > 3 ? 'text-warning' : 'text-muted-foreground'}`}>
                      {r.signatureAgeDays === 0 ? 'Updated today' : `${r.signatureAgeDays} day(s) old`}
                    </div>
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{formatDateTime(r.quickScanAt)}</td>
                  <td className="px-4 py-3 text-sm">
                    {r.threats24h > 0 ? (
                      <Link
                        href={`/dlp-alerts?type=malware&employeeId=${encodeURIComponent(r.employeeId)}`}
                        className="text-destructive font-semibold hover:underline"
                      >
                        {r.threats24h}
                      </Link>
                    ) : (
                      <span className="text-muted-foreground">0</span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-sm">
                    {r.lastTamperAt ? (
                      <span className="text-warning" title={formatDateTime(r.lastTamperAt)}>
                        {formatRelative(r.lastTamperAt)}
                      </span>
                    ) : (
                      <span className="text-muted-foreground">None</span>
                    )}
                    {r.exclusionCount > 0 && (
                      <div className="text-xs text-muted-foreground">{r.exclusionCount} approved exclusion(s)</div>
                    )}
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground" title={formatDateTime(r.syncedAt)}>
                    {formatRelative(r.syncedAt)}
                    {r.agentVersion && <div className="text-xs">v{r.agentVersion}</div>}
                  </td>
                  <td className="px-4 py-3">
                    <ActionsCell r={r} />
                  </td>
                </tr>
              );
            })}
            {!query.isLoading && rows.length === 0 && (
              <tr>
                <td colSpan={10} className="px-4 py-10 text-center text-sm text-muted-foreground">
                  <ShieldQuestion className="w-6 h-6 mx-auto mb-2" />
                  No Windows PC has reported antivirus status yet — it appears within minutes of an
                  installed tracker (with the antivirus guard) logging in.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      <div ref={sentinelRef} className="h-8 flex items-center justify-center text-xs text-muted-foreground">
        {query.isFetchingNextPage && <Loader2 className="w-4 h-4 animate-spin" />}
        {!query.hasNextPage && rows.length > 0 && `Showing all ${pages?.[0]?.total ?? rows.length}`}
      </div>
    </div>
  );
}

export default function AntivirusPage() {
  return (
    <Suspense fallback={<div className="flex justify-center py-16"><Loader2 className="w-6 h-6 animate-spin text-primary" /></div>}>
      <AntivirusInner />
    </Suspense>
  );
}
