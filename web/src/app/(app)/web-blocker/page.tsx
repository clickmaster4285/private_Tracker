'use client';

import { useEffect, useMemo, useState } from 'react';
import { motion } from 'framer-motion';
import { Globe, History, Loader2, Pencil, Plus, Trash2, Upload, X } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Switch } from '@/components/ui/switch';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { TimezoneCombobox, browserTimezone } from '@/components/TimezoneCombobox';
import {
  dlpApi,
  type DlpAction,
  type DlpSeverity,
  type WebBlockerRule,
  type WebBlockerRuleRevision,
} from '@/lib/api';

const severityColors: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive',
  high: 'bg-warning/15 text-warning',
  medium: 'bg-info/15 text-info',
  low: 'bg-muted text-muted-foreground',
};

const actionLabels: Record<string, string> = {
  alert: 'Alert Only',
  block: 'Block',
  alert_block: 'Alert + Block',
};

const DAY_OPTIONS = [
  { id: 'mon', label: 'Mon' },
  { id: 'tue', label: 'Tue' },
  { id: 'wed', label: 'Wed' },
  { id: 'thu', label: 'Thu' },
  { id: 'fri', label: 'Fri' },
  { id: 'sat', label: 'Sat' },
  { id: 'sun', label: 'Sun' },
] as const;

type DayId = (typeof DAY_OPTIONS)[number]['id'];

function normalizeDomainClient(raw: string): string {
  let s = raw.trim().toLowerCase();
  if (!s) return '';
  s = s.replace(/^https?:\/\//, '').replace(/^\/\//, '');
  const cut = s.search(/[/?#]/);
  if (cut >= 0) s = s.slice(0, cut);
  s = s.replace(/\.$/, '');
  return s;
}

function DomainChipInput({
  domains,
  onChange,
  placeholder,
}: {
  domains: string[];
  onChange: (next: string[]) => void;
  placeholder?: string;
}) {
  const [draft, setDraft] = useState('');

  const commit = (raw: string) => {
    const parts = raw
      .split(/[\s,]+/)
      .map(normalizeDomainClient)
      .filter(Boolean);
    if (parts.length === 0) return;
    const seen = new Set(domains);
    const next = [...domains];
    for (const p of parts) {
      if (seen.has(p)) continue;
      seen.add(p);
      next.push(p);
    }
    onChange(next);
    setDraft('');
  };

  return (
    <div className="mt-1 rounded-md border border-border bg-background px-2 py-1.5 focus-within:ring-1 focus-within:ring-ring">
      <div className="flex flex-wrap gap-1.5">
        {domains.map((d) => (
          <span
            key={d}
            className="inline-flex items-center gap-1 rounded-md bg-muted px-2 py-0.5 text-xs font-medium text-foreground"
          >
            {d}
            <button
              type="button"
              aria-label={`Remove ${d}`}
              className="rounded hover:bg-destructive/15 p-0.5"
              onClick={() => onChange(domains.filter((x) => x !== d))}
            >
              <X className="w-3 h-3 text-muted-foreground" />
            </button>
          </span>
        ))}
        <input
          className="min-w-[10rem] flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground py-0.5"
          placeholder={domains.length === 0 ? placeholder ?? 'Type a domain and press Enter' : 'Add another…'}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' || e.key === ',' || e.key === 'Tab') {
              e.preventDefault();
              commit(draft);
            } else if (e.key === 'Backspace' && !draft && domains.length > 0) {
              onChange(domains.slice(0, -1));
            }
          }}
          onBlur={() => {
            if (draft.trim()) commit(draft);
          }}
        />
      </div>
    </div>
  );
}

type FormState = {
  name: string;
  domains: string[];
  action: DlpAction;
  severity: DlpSeverity;
  enabled: boolean;
  expandRelated: boolean;
  scheduleOn: boolean;
  scheduleDays: DayId[];
  startTime: string;
  endTime: string;
  timezone: string;
};

const emptyForm = (): FormState => ({
  name: '',
  domains: [],
  action: 'alert_block',
  severity: 'high',
  enabled: true,
  expandRelated: true,
  scheduleOn: false,
  scheduleDays: ['mon', 'tue', 'wed', 'thu', 'fri'],
  startTime: '09:00',
  endTime: '18:00',
  timezone: browserTimezone(),
});

function formFromRule(rule: WebBlockerRule): FormState {
  return {
    name: rule.name,
    domains: uniqueDomains(rule.domains ?? []),
    action: rule.action,
    severity: rule.severity,
    enabled: rule.enabled,
    expandRelated: false,
    scheduleOn: rule.scheduleEnabled,
    scheduleDays: (rule.scheduleDays ?? []) as DayId[],
    startTime: rule.scheduleStart || '09:00',
    endTime: rule.scheduleEnd || '18:00',
    timezone: rule.scheduleTimezone || 'UTC',
  };
}

function uniqueDomains(list: string[]): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const raw of list) {
    const d = normalizeDomainClient(raw);
    if (!d || seen.has(d)) continue;
    seen.add(d);
    out.push(d);
  }
  return out;
}

function notAlreadySelected(candidates: string[], selected: string[]): string[] {
  const have = new Set(selected.map(normalizeDomainClient));
  return uniqueDomains(candidates).filter((d) => !have.has(d));
}

export default function WebBlockerPage() {
  const qc = useQueryClient();
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editing, setEditing] = useState<WebBlockerRule | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm);
  const [suggested, setSuggested] = useState<string[]>([]);
  const [publishMsg, setPublishMsg] = useState('');
  const [historyRule, setHistoryRule] = useState<WebBlockerRule | null>(null);

  const { data, isLoading, isError } = useQuery({
    queryKey: ['web-blocker-rules'],
    queryFn: () => dlpApi.webBlocker.list(),
  });

  const rules = data?.rules ?? [];

  const historyQuery = useQuery({
    queryKey: ['web-blocker-history', historyRule?.id],
    queryFn: () => dlpApi.webBlocker.history(historyRule!.id),
    enabled: !!historyRule,
  });

  useEffect(() => {
    if (!form.expandRelated || form.domains.length === 0) {
      setSuggested([]);
      return;
    }
    const domainsSnapshot = form.domains;
    let cancelled = false;
    const t = setTimeout(() => {
      dlpApi.webBlocker
        .expandDomains(domainsSnapshot)
        .then((res) => {
          if (cancelled) return;
          // Always strip anything already in Domains (avoids stale/race duplicates).
          setSuggested(notAlreadySelected(res.suggested ?? [], domainsSnapshot));
        })
        .catch(() => {
          if (!cancelled) setSuggested([]);
        });
    }, 300);
    return () => {
      cancelled = true;
      clearTimeout(t);
    };
  }, [form.domains, form.expandRelated]);

  // Defense in depth: never show a chip that is already selected.
  const visibleSuggested = useMemo(
    () => notAlreadySelected(suggested, form.domains),
    [suggested, form.domains],
  );

  const openCreate = () => {
    setEditing(null);
    setForm(emptyForm());
    setSuggested([]);
    setDialogOpen(true);
  };

  const openEdit = (rule: WebBlockerRule) => {
    setEditing(rule);
    setForm(formFromRule(rule));
    setSuggested([]);
    setDialogOpen(true);
  };

  const saveMutation = useMutation({
    mutationFn: async () => {
      const domains = uniqueDomains(form.domains);
      if (domains.length === 0) {
        throw new Error('At least one domain is required');
      }
      const payload = {
        name: form.name.trim(),
        action: form.action,
        severity: form.severity,
        domains,
        paths: [] as string[],
        scheduleEnabled: form.scheduleOn,
        scheduleStart: form.scheduleOn ? form.startTime : '',
        scheduleEnd: form.scheduleOn ? form.endTime : '',
        scheduleTimezone: form.scheduleOn ? form.timezone.trim() || 'UTC' : 'UTC',
        scheduleDays: form.scheduleOn ? form.scheduleDays : [],
        scopeAll: true,
        enabled: form.enabled,
        expandRelated: form.expandRelated,
      };
      if (editing) {
        await dlpApi.webBlocker.update(editing.id, payload);
      } else {
        await dlpApi.webBlocker.create(payload);
      }
      // Push to agents so Active rules re-appear in hosts without a separate Publish click.
      return dlpApi.rules.publish();
    },
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ['web-blocker-rules'] });
      setDialogOpen(false);
      setEditing(null);
      setForm(emptyForm());
      setSuggested([]);
      setPublishMsg(`Published policy v${res.policyVersion} (${res.ruleCount} rules)`);
    },
  });

  const publishMutation = useMutation({
    mutationFn: () => dlpApi.rules.publish(),
    onSuccess: (res) => {
      setPublishMsg(`Published policy v${res.policyVersion} (${res.ruleCount} rules)`);
    },
  });

  // Active/Inactive only updates the draft rule — Windows hosts follow the *published*
  // policy. Auto-publish after toggle so re-Activate re-applies the DNS sink.
  const toggleMutation = useMutation({
    mutationFn: async ({ id, enabled }: { id: string; enabled: boolean }) => {
      await dlpApi.webBlocker.update(id, { enabled });
      return dlpApi.rules.publish();
    },
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ['web-blocker-rules'] });
      setPublishMsg(
        `Published policy v${res.policyVersion} (${res.ruleCount} rules) — agents will refresh within ~${30}s`,
      );
    },
  });

  const deleteMutation = useMutation({
    mutationFn: async (id: string) => {
      await dlpApi.webBlocker.delete(id);
      return dlpApi.rules.publish();
    },
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ['web-blocker-rules'] });
      setPublishMsg(`Published policy v${res.policyVersion} after delete`);
    },
  });

  const toggleDay = (id: DayId) => {
    setForm((prev) => ({
      ...prev,
      scheduleDays: prev.scheduleDays.includes(id)
        ? prev.scheduleDays.filter((d) => d !== id)
        : [...prev.scheduleDays, id],
    }));
  };

  const addSuggested = (d: string) => {
    const normalized = normalizeDomainClient(d);
    if (!normalized) return;
    setSuggested((prev) => prev.filter((x) => normalizeDomainClient(x) !== normalized));
    setForm((prev) => {
      if (prev.domains.some((x) => normalizeDomainClient(x) === normalized)) return prev;
      return { ...prev, domains: [...prev.domains, normalized] };
    });
  };

  const addAllSuggested = () => {
    const toAdd = visibleSuggested;
    setSuggested([]);
    setForm((prev) => ({
      ...prev,
      domains: uniqueDomains([...prev.domains, ...toAdd]),
    }));
  };

  const canSave =
    form.name.trim().length > 0 &&
    form.domains.length > 0 &&
    (!form.scheduleOn || form.scheduleDays.length > 0);

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <div>
          <h3 className="font-display font-bold text-lg text-foreground">Web Blocker</h3>
          <p className="text-xs text-muted-foreground mt-0.5">
            Domain block rules with optional schedule. Domains hard-block on Windows after Publish
            (hosts + firewall). Edits keep revision history.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            className="gap-1"
            disabled={publishMutation.isPending}
            onClick={() => publishMutation.mutate()}
          >
            {publishMutation.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : <Upload className="w-4 h-4" />}
            Publish
          </Button>
          <Button
            onClick={openCreate}
            size="sm"
            className="gap-1 gradient-primary text-primary-foreground"
          >
            <Plus className="w-4 h-4" /> Add rule
          </Button>
        </div>
      </div>

      {publishMsg && <p className="text-xs text-success">{publishMsg}</p>}
      {publishMutation.isError && <p className="text-xs text-destructive">Publish failed</p>}

      {isLoading && (
        <div className="flex justify-center py-12">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      )}
      {isError && <p className="text-sm text-destructive">Failed to load web blocker rules</p>}
      {!isLoading && !isError && rules.length === 0 && (
        <p className="text-sm text-muted-foreground py-8 text-center">
          No web block rules yet. Add domains, then Publish so Windows agents can pull them.
        </p>
      )}

      <div className="space-y-3">
        {rules.map((rule, i) => {
          const sched = rule.scheduleEnabled
            ? `${(rule.scheduleDays ?? []).join(', ') || 'all days'} · ${rule.scheduleStart || '?'}–${rule.scheduleEnd || '?'} (${rule.scheduleTimezone || 'UTC'})`
            : null;
          return (
            <motion.div
              key={rule.id}
              initial={{ opacity: 0, y: 10 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ delay: i * 0.03 }}
              className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all"
            >
              <div className="flex items-start justify-between gap-3">
                <div className="flex items-center gap-3 min-w-0">
                  <Globe className="w-5 h-5 text-primary flex-shrink-0" />
                  <div className="min-w-0 space-y-1">
                    <h4 className="font-display font-bold text-foreground truncate">{rule.name}</h4>
                    {(rule.domains?.length ?? 0) > 0 && (
                      <div className="flex flex-wrap gap-1">
                        {rule.domains.map((d) => (
                          <code
                            key={d}
                            className="bg-muted px-1.5 py-0.5 rounded text-[10px] text-muted-foreground"
                          >
                            {d}
                          </code>
                        ))}
                      </div>
                    )}
                    {sched && <p className="text-xs text-muted-foreground">Schedule: {sched}</p>}
                  </div>
                </div>
                <div className="flex items-center gap-2 flex-shrink-0 flex-wrap justify-end">
                  <label
                    className="inline-flex items-center gap-2 cursor-pointer"
                    title={
                      rule.enabled
                        ? 'Deactivate and publish (removes from agent hosts)'
                        : 'Activate and publish (re-applies agent hosts)'
                    }
                  >
                    <Switch
                      checked={rule.enabled}
                      disabled={toggleMutation.isPending}
                      onCheckedChange={(checked) =>
                        toggleMutation.mutate({ id: rule.id, enabled: checked })
                      }
                      className="h-4 w-7 [&>span]:h-3 [&>span]:w-3 [&>span]:data-[state=checked]:translate-x-3"
                      aria-label={rule.enabled ? 'Deactivate rule' : 'Activate rule'}
                    />
                    <span
                      className={`text-xs font-medium ${
                        rule.enabled ? 'text-success' : 'text-muted-foreground'
                      }`}
                    >
                      {rule.enabled ? 'Active' : 'Inactive'}
                    </span>
                  </label>
                  <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${severityColors[rule.severity] ?? ''}`}>
                    {rule.severity}
                  </span>
                  <span className="px-2.5 py-1 rounded-full text-xs font-medium bg-accent text-accent-foreground">
                    {actionLabels[rule.action] ?? rule.action}
                  </span>
                  <button
                    type="button"
                    onClick={() => openEdit(rule)}
                    className="p-1.5 rounded hover:bg-muted"
                    aria-label="Edit rule"
                  >
                    <Pencil className="w-3.5 h-3.5 text-muted-foreground" />
                  </button>
                  <button
                    type="button"
                    onClick={() => setHistoryRule(rule)}
                    className="p-1.5 rounded hover:bg-muted"
                    aria-label="Revision history"
                  >
                    <History className="w-3.5 h-3.5 text-muted-foreground" />
                  </button>
                  <button
                    type="button"
                    onClick={() => deleteMutation.mutate(rule.id)}
                    className="p-1.5 rounded hover:bg-destructive/10"
                    aria-label="Delete rule"
                  >
                    <Trash2 className="w-3.5 h-3.5 text-destructive" />
                  </button>
                </div>
              </div>
            </motion.div>
          );
        })}
      </div>

      <Dialog
        open={dialogOpen}
        onOpenChange={(open) => {
          setDialogOpen(open);
          if (!open) {
            setEditing(null);
            setForm(emptyForm());
          }
        }}
      >
        <DialogContent className="bg-card max-h-[90vh] overflow-y-auto sm:max-w-lg">
          <DialogHeader>
            <DialogTitle className="font-display">
              {editing ? 'Edit web block rule' : 'New web block rule'}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-3 mt-2">
            <Input
              placeholder="Rule name *"
              value={form.name}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            />

            <div>
              <label className="text-xs font-medium text-muted-foreground">Domains</label>
              <DomainChipInput
                domains={form.domains}
                onChange={(domains) => setForm((f) => ({ ...f, domains: uniqueDomains(domains) }))}
                placeholder="example.com — Enter to add"
              />
              <p className="text-[10px] text-muted-foreground mt-1">
                Add one domain at a time (Enter). Duplicates are ignored.
              </p>
            </div>

            <label className="flex items-start gap-2 text-sm cursor-pointer">
              <input
                type="checkbox"
                checked={form.expandRelated}
                onChange={(e) => setForm((f) => ({ ...f, expandRelated: e.target.checked }))}
                className="rounded border-border mt-0.5"
              />
              <span>
                Include related domains
                <span className="block text-[10px] text-muted-foreground font-normal">
                  Always adds www./m. for every site. Known sites (YouTube, Facebook, …) also get
                  CDNs and alternate apexes. Unknown sites cannot invent arbitrary CDNs.
                </span>
              </span>
            </label>

            {form.expandRelated && visibleSuggested.length > 0 && (
              <div className="rounded-lg border border-border bg-muted/30 p-2.5 space-y-2">
                <div className="flex items-center justify-between gap-2">
                  <p className="text-[11px] font-medium text-muted-foreground">
                    Suggested related ({visibleSuggested.length})
                  </p>
                  <Button type="button" variant="outline" size="sm" className="h-7 text-xs" onClick={addAllSuggested}>
                    Add all
                  </Button>
                </div>
                <div className="flex flex-wrap gap-1">
                  {visibleSuggested.map((d) => (
                    <button
                      key={d}
                      type="button"
                      onClick={() => addSuggested(d)}
                      className="text-[10px] px-1.5 py-0.5 rounded border border-dashed border-border hover:border-primary hover:text-primary"
                    >
                      + {d}
                    </button>
                  ))}
                </div>
              </div>
            )}

            <div className="grid grid-cols-2 gap-3">
              <Select
                value={form.action}
                onValueChange={(v) => setForm((f) => ({ ...f, action: v as DlpAction }))}
              >
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="alert">Alert Only</SelectItem>
                  <SelectItem value="block">Block</SelectItem>
                  <SelectItem value="alert_block">Alert + Block</SelectItem>
                </SelectContent>
              </Select>
              <Select
                value={form.severity}
                onValueChange={(v) => setForm((f) => ({ ...f, severity: v as DlpSeverity }))}
              >
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="critical">Critical</SelectItem>
                  <SelectItem value="high">High</SelectItem>
                  <SelectItem value="medium">Medium</SelectItem>
                  <SelectItem value="low">Low</SelectItem>
                </SelectContent>
              </Select>
            </div>

            <label className="flex items-center justify-between gap-3 rounded-lg border border-border px-3 py-2">
              <span className="text-sm">
                Status:{' '}
                <span className={form.enabled ? 'text-success font-medium' : 'text-muted-foreground'}>
                  {form.enabled ? 'Active' : 'Inactive'}
                </span>
              </span>
              <Switch
                checked={form.enabled}
                onCheckedChange={(checked) => setForm((f) => ({ ...f, enabled: checked }))}
              />
            </label>

            <label className="flex items-center gap-2 text-sm cursor-pointer">
              <input
                type="checkbox"
                checked={form.scheduleOn}
                onChange={(e) => setForm((f) => ({ ...f, scheduleOn: e.target.checked }))}
                className="rounded border-border"
              />
              Timer-based restriction
            </label>

            {form.scheduleOn && (
              <div className="space-y-2 rounded-lg border border-border p-3 bg-muted/30">
                <div className="flex flex-wrap gap-1.5">
                  {DAY_OPTIONS.map((d) => {
                    const on = form.scheduleDays.includes(d.id);
                    return (
                      <button
                        key={d.id}
                        type="button"
                        onClick={() => toggleDay(d.id)}
                        className={`px-2 py-1 rounded text-xs font-medium border ${
                          on
                            ? 'bg-primary text-primary-foreground border-primary'
                            : 'bg-card text-muted-foreground border-border'
                        }`}
                      >
                        {d.label}
                      </button>
                    );
                  })}
                </div>
                <div className="grid grid-cols-2 gap-2">
                  <div>
                    <label className="text-[10px] text-muted-foreground">Start</label>
                    <Input
                      type="time"
                      value={form.startTime}
                      onChange={(e) => setForm((f) => ({ ...f, startTime: e.target.value }))}
                    />
                  </div>
                  <div>
                    <label className="text-[10px] text-muted-foreground">End</label>
                    <Input
                      type="time"
                      value={form.endTime}
                      onChange={(e) => setForm((f) => ({ ...f, endTime: e.target.value }))}
                    />
                  </div>
                </div>
                <div>
                  <label className="text-[10px] text-muted-foreground">Timezone</label>
                  <TimezoneCombobox
                    value={form.timezone.trim() || 'UTC'}
                    onChange={(tz) => setForm((f) => ({ ...f, timezone: tz }))}
                  />
                  <p className="text-[10px] text-muted-foreground mt-1">
                    Search and select an IANA zone (e.g. Asia/Karachi). Schedule times use this zone on the employee PC.
                  </p>
                </div>
              </div>
            )}

            <Button
              className="w-full gradient-primary text-primary-foreground"
              disabled={!canSave || saveMutation.isPending}
              onClick={() => saveMutation.mutate()}
            >
              {saveMutation.isPending ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : editing ? (
                'Save changes'
              ) : (
                'Create rule'
              )}
            </Button>
            {saveMutation.isError && (
              <p className="text-xs text-destructive">
                {(saveMutation.error as Error)?.message || 'Save failed'}
              </p>
            )}
          </div>
        </DialogContent>
      </Dialog>

      <Dialog open={!!historyRule} onOpenChange={(open) => !open && setHistoryRule(null)}>
        <DialogContent className="bg-card max-h-[90vh] overflow-y-auto sm:max-w-lg">
          <DialogHeader>
            <DialogTitle className="font-display">
              History{historyRule ? ` — ${historyRule.name}` : ''}
            </DialogTitle>
          </DialogHeader>
          {historyQuery.isLoading && (
            <div className="flex justify-center py-8">
              <Loader2 className="w-5 h-5 animate-spin text-primary" />
            </div>
          )}
          {historyQuery.isError && (
            <p className="text-sm text-destructive">Failed to load revision history</p>
          )}
          {!historyQuery.isLoading && !historyQuery.isError && (historyQuery.data?.revisions?.length ?? 0) === 0 && (
            <p className="text-sm text-muted-foreground py-6 text-center">
              No previous versions yet. History is saved when you edit a rule.
            </p>
          )}
          <div className="space-y-3">
            {(historyQuery.data?.revisions ?? []).map((rev: WebBlockerRuleRevision) => (
              <div key={rev.id} className="rounded-lg border border-border p-3 space-y-1">
                <div className="flex items-center justify-between gap-2">
                  <span className="text-xs font-semibold text-foreground">
                    Revision #{rev.revision}
                  </span>
                  <span className="text-[10px] text-muted-foreground">
                    {new Date(rev.createdAt).toLocaleString()}
                  </span>
                </div>
                <p className="text-[11px] text-muted-foreground">
                  {rev.changedByName || 'Unknown'} · {rev.enabled ? 'Active' : 'Inactive'} ·{' '}
                  {actionLabels[rev.action] ?? rev.action} · {rev.severity}
                </p>
                <p className="text-xs font-medium text-foreground">{rev.name}</p>
                {(rev.domains?.length ?? 0) > 0 && (
                  <div className="flex flex-wrap gap-1 pt-0.5">
                    {rev.domains.map((d) => (
                      <code
                        key={d}
                        className="bg-muted px-1.5 py-0.5 rounded text-[10px] text-muted-foreground"
                      >
                        {d}
                      </code>
                    ))}
                  </div>
                )}
              </div>
            ))}
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
