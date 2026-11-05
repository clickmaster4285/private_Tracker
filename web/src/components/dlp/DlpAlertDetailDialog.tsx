'use client';

import { useEffect, useMemo, useState } from 'react';
import { Check, Copy, Loader2, ShieldAlert } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import EmployeeSelector from '@/components/EmployeeSelector';
import {
  dlpApi,
  type DlpAlert,
  type DlpAlertHistoryEvent,
  type DlpAlertStatus,
} from '@/lib/api';
import {
  actionLabel,
  alertTypeLabel,
  evidenceDomains,
  severityLabel,
  statusLabel,
  titleize,
} from '@/lib/dlp-labels';
import { formatDateTime, formatRelative } from '@/lib/format';

const SEVERITY_BADGE: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive',
  high: 'bg-warning/15 text-warning',
  medium: 'bg-info/15 text-info',
  low: 'bg-muted text-muted-foreground',
};

/** Who-handled field label by triage status. Open has no assignee. */
const ASSIGNEE_LABEL: Partial<Record<DlpAlertStatus, string>> = {
  investigating: 'Investigated by',
  resolved: 'Resolved by',
  false_positive: 'False positive by',
};

interface Props {
  alert: DlpAlert | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

type TriageDraft = {
  status: DlpAlertStatus;
  assignedTo: string;
  notes: string;
};

function triageFromAlert(a: DlpAlert | null | undefined): TriageDraft {
  const status = (a?.status as DlpAlertStatus) ?? 'open';
  return {
    status,
    // Open has no assignee on the wire either.
    assignedTo: status === 'open' ? '' : (a?.assignedTo ?? ''),
    notes: a?.notes ?? '',
  };
}

/**
 * Full-detail popup for a single DLP alert.
 *
 * Triage edits (status / assignee / notes) stay local until "Save changes".
 * Every successful save appends audit rows on the server (who / what / when).
 */
export default function DlpAlertDetailDialog({ alert, open, onOpenChange }: Props) {
  const qc = useQueryClient();
  const [assignedTo, setAssignedTo] = useState('');
  const [notes, setNotes] = useState('');
  const [copied, setCopied] = useState(false);
  const [status, setStatus] = useState<DlpAlertStatus>(alert?.status ?? 'open');
  // Last successfully saved (or initially loaded) triage — dirty is vs this,
  // so the Save button clears immediately without waiting for the list refetch.
  const [baseline, setBaseline] = useState<TriageDraft>(() => triageFromAlert(alert));
  const [seededAlertId, setSeededAlertId] = useState<string | null>(alert?.id ?? null);

  // Re-seed only when a different alert is opened (not on list-refetch flicker).
  useEffect(() => {
    const id = alert?.id ?? null;
    if (id === seededAlertId) return;
    const next = triageFromAlert(alert);
    setAssignedTo(next.assignedTo);
    setNotes(next.notes);
    setStatus(next.status);
    setBaseline(next);
    setCopied(false);
    setSeededAlertId(id);
  }, [alert, seededAlertId]);

  const historyQuery = useQuery({
    queryKey: ['dlp-alert-history', alert?.id],
    queryFn: () => dlpApi.alerts.history(alert!.id),
    enabled: open && Boolean(alert?.id),
  });

  const saveMutation = useMutation({
    mutationFn: ({
      id,
      status: nextStatus,
      assignedTo: who,
      notes: note,
    }: {
      id: string;
      status: DlpAlertStatus;
      assignedTo: string;
      notes: string;
    }) =>
      dlpApi.alerts.update(id, {
        status: nextStatus,
        // Open has no assignee — always clear on that status.
        assignedTo: nextStatus === 'open' ? '' : who,
        notes: note,
      }),
    onSuccess: (saved) => {
      const next = triageFromAlert(saved);
      setAssignedTo(next.assignedTo);
      setNotes(next.notes);
      setStatus(next.status);
      setBaseline(next);
      qc.invalidateQueries({ queryKey: ['dlp-alerts'] });
      qc.invalidateQueries({ queryKey: ['dlp-alert-history', alert?.id] });
    },
  });

  const historyGroups = useMemo(
    () => groupHistory(historyQuery.data?.events ?? []),
    [historyQuery.data?.events],
  );

  const draftAssignee = status === 'open' ? '' : assignedTo;
  const dirty =
    status !== baseline.status ||
    draftAssignee !== baseline.assignedTo ||
    notes !== baseline.notes;

  if (!alert) return null;

  const domains = evidenceDomains(alert.evidence);
  const evidenceEntries = Object.entries(alert.evidence ?? {}).filter(
    ([key]) => key !== 'domains' && key !== 'domain',
  );
  const saving = saveMutation.isPending;
  const assigneeLabel = ASSIGNEE_LABEL[status];

  const copyEvidence = async () => {
    try {
      await navigator.clipboard.writeText(JSON.stringify(alert.evidence ?? {}, null, 2));
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard unavailable (insecure context) — ignore */
    }
  };

  const onStatusChange = (next: DlpAlertStatus) => {
    setStatus(next);
    if (next === 'open') setAssignedTo('');
    saveMutation.reset();
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-2xl max-h-[85vh] overflow-y-auto">
        <DialogHeader>
          <div className="flex items-center gap-2 pr-6">
            <ShieldAlert className="w-5 h-5 text-primary shrink-0" />
            <DialogTitle className="text-base">{alertTypeLabel(alert.type)}</DialogTitle>
            <span
              className={`px-2 py-0.5 rounded-full text-xs font-medium ${SEVERITY_BADGE[alert.severity] ?? ''}`}
            >
              {severityLabel(alert.severity)}
            </span>
          </div>
          <DialogDescription className="text-xs">
            {actionLabel(alert.actionTaken)} · {statusLabel(status)}
            {dirty ? ' (unsaved)' : ''} · {formatDateTime(alert.occurredAt)}
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-5">
          {/* ── Facts ─────────────────────────────────────────────── */}
          <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-3 text-sm">
            <Fact label="Employee" value={alert.employeeName || alert.employeeId} />
            <Fact label="Employee ID" value={alert.employeeId} mono />
            <Fact label="Machine" value={alert.machineId || '—'} mono />
            <Fact label="Occurred" value={formatDateTime(alert.occurredAt)} />
            <Fact label="Action taken" value={actionLabel(alert.actionTaken)} />
            <Fact label="Policy version" value={alert.policyVersion ? `v${alert.policyVersion}` : '—'} />
            <Fact label="Alert ID" value={alert.id} mono />
            <Fact label="Matched rule" value={alert.ruleId || '—'} mono />
            <Fact label="Synced at" value={formatDateTime(alert.syncedAt)} />
            <Fact label="Raw type" value={titleize(alert.type)} />
          </dl>

          {/* ── Domains (chips, not CSV) ──────────────────────────── */}
          {domains.length > 0 && (
            <section>
              <h3 className="text-sm font-semibold text-foreground mb-2">
                Domains ({domains.length})
              </h3>
              <div className="flex flex-wrap gap-1.5 max-h-48 overflow-y-auto">
                {domains.map((d) => (
                  <code
                    key={d}
                    className="bg-muted px-1.5 py-0.5 rounded text-[10px] text-muted-foreground break-all"
                  >
                    {d}
                  </code>
                ))}
              </div>
            </section>
          )}

          {/* ── Evidence ──────────────────────────────────────────── */}
          <section>
            <div className="flex items-center justify-between mb-2">
              <h3 className="text-sm font-semibold text-foreground">Evidence</h3>
              {/* {Object.keys(alert.evidence ?? {}).length > 0 && (
                <button
                  type="button"
                  onClick={copyEvidence}
                  className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground transition-colors"
                >
                  {copied ? <Check className="w-3.5 h-3.5 text-success" /> : <Copy className="w-3.5 h-3.5" />}
                  {copied ? 'Copied' : 'Copy JSON'}
                </button>
              )} */}
            </div>
            {evidenceEntries.length === 0 && domains.length === 0 ? (
              <p className="text-xs text-muted-foreground">No evidence was reported with this alert.</p>
            ) : evidenceEntries.length === 0 ? (
              <p className="text-xs text-muted-foreground">No additional evidence fields.</p>
            ) : (
              <div className="rounded-lg border border-border bg-muted/30 divide-y divide-border">
                {evidenceEntries.map(([key, value]) => (
                  <div key={key} className="flex items-start gap-3 px-3 py-2">
                    <span className="text-xs font-medium text-muted-foreground w-36 shrink-0 break-words">
                      {titleize(key)}
                    </span>
                    <span className="text-xs text-foreground font-mono break-all min-w-0">
                      {typeof value === 'object' && value !== null
                        ? JSON.stringify(value)
                        : String(value)}
                    </span>
                  </div>
                ))}
              </div>
            )}
          </section>

          {/* ── Triage ────────────────────────────────────────────── */}
          <section className="space-y-3">
            <h3 className="text-sm font-semibold text-foreground">Triage</h3>

            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <Label htmlFor="dlp-alert-status" className="text-xs text-muted-foreground">
                  Status
                </Label>
                <Select
                  value={status}
                  disabled={saving}
                  onValueChange={(v) => onStatusChange(v as DlpAlertStatus)}
                >
                  <SelectTrigger id="dlp-alert-status" className="h-9">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="open">Open</SelectItem>
                    <SelectItem value="investigating">Investigating</SelectItem>
                    <SelectItem value="resolved">Resolved</SelectItem>
                    <SelectItem value="false_positive">False Positive</SelectItem>
                  </SelectContent>
                </Select>
              </div>

              {assigneeLabel && (
                <div className="space-y-1.5">
                  <Label className="text-xs text-muted-foreground">{assigneeLabel}</Label>
                  <EmployeeSelector
                    value={assignedTo}
                    onChange={(emp) => {
                      setAssignedTo(emp?.employeeId ?? '');
                      saveMutation.reset();
                    }}
                    placeholder="Select employee…"
                    allowAll
                    allLabel="Unassigned"
                    className="w-full max-w-none"
                  />
                </div>
              )}
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="dlp-alert-notes" className="text-xs text-muted-foreground">
                Notes
              </Label>
              <Textarea
                id="dlp-alert-notes"
                value={notes}
                disabled={saving}
                onChange={(e) => {
                  setNotes(e.target.value);
                  saveMutation.reset();
                }}
                placeholder="Investigation notes…"
                rows={3}
              />
            </div>

            <div className="flex items-center justify-end gap-3">
              {saveMutation.isError && (
                <p className="text-xs text-destructive mr-auto">Could not save changes.</p>
              )}
              {saveMutation.isSuccess && !dirty && (
                <p className="text-xs text-success mr-auto">Saved.</p>
              )}
              <Button
                size="sm"
                variant="outline"
                disabled={!dirty || saving}
                onClick={() =>
                  saveMutation.mutate({
                    id: alert.id,
                    status,
                    assignedTo,
                    notes,
                  })
                }
              >
                {saving && <Loader2 className="w-4 h-4 animate-spin" />}
                Save changes
              </Button>
            </div>
          </section>

          {/* ── History ───────────────────────────────────────────── */}
          <section className="space-y-2">
            <h3 className="text-sm font-semibold text-foreground">History</h3>
            {historyQuery.isLoading ? (
              <div className="flex items-center gap-2 text-xs text-muted-foreground py-2">
                <Loader2 className="w-3.5 h-3.5 animate-spin" /> Loading history…
              </div>
            ) : historyQuery.isError ? (
              <p className="text-xs text-destructive">Could not load history.</p>
            ) : historyGroups.length === 0 ? (
              <p className="text-xs text-muted-foreground">No triage changes yet.</p>
            ) : (
              <ol className="rounded-lg border border-border divide-y divide-border overflow-hidden">
                {historyGroups.map((g) => (
                  <li key={g.key} className="px-3 py-2.5 bg-muted/20">
                    <div className="flex items-baseline justify-between gap-3 mb-1">
                      <span className="text-xs font-medium text-foreground">
                        {g.changedByName || 'Unknown user'}
                      </span>
                      <span
                        className="text-[11px] text-muted-foreground shrink-0"
                        title={formatDateTime(g.createdAt)}
                      >
                        {formatRelative(g.createdAt)}
                      </span>
                    </div>
                    <ul className="space-y-0.5">
                      {g.events.map((e) => (
                        <li key={e.id} className="text-xs text-muted-foreground">
                          <span className="text-foreground/80">{historyFieldLabel(e.field)}</span>
                          {': '}
                          <span className="font-mono">{formatHistoryValue(e.field, e.oldValue)}</span>
                          {' → '}
                          <span className="font-mono text-foreground">
                            {formatHistoryValue(e.field, e.newValue)}
                          </span>
                        </li>
                      ))}
                    </ul>
                  </li>
                ))}
              </ol>
            )}
          </section>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function Fact({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd
        className={`text-sm text-foreground break-words ${mono ? 'font-mono text-xs' : ''}`}
        title={value}
      >
        {value || '—'}
      </dd>
    </div>
  );
}

type HistoryGroup = {
  key: string;
  changedByName: string;
  createdAt: string;
  events: DlpAlertHistoryEvent[];
};

/** Collapse same-save field rows (identical createdAt + actor) into one card. */
function groupHistory(events: DlpAlertHistoryEvent[]): HistoryGroup[] {
  const groups: HistoryGroup[] = [];
  for (const e of events) {
    const key = `${e.createdAt}|${e.changedByName}|${e.changedBy ?? ''}`;
    const last = groups[groups.length - 1];
    if (last && last.key === key) {
      last.events.push(e);
    } else {
      groups.push({
        key,
        changedByName: e.changedByName,
        createdAt: e.createdAt,
        events: [e],
      });
    }
  }
  return groups;
}

function historyFieldLabel(field: string): string {
  switch (field) {
    case 'status':
      return 'Status';
    case 'assigned_to':
      return 'Assigned to';
    case 'notes':
      return 'Notes';
    default:
      return titleize(field);
  }
}

function formatHistoryValue(field: string, value: string): string {
  if (!value) return '—';
  if (field === 'status') return statusLabel(value);
  if (field === 'notes') {
    const trimmed = value.replace(/\s+/g, ' ').trim();
    return trimmed.length > 80 ? `${trimmed.slice(0, 80)}…` : trimmed;
  }
  return value;
}
