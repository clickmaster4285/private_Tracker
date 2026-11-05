'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import {
  Eye,
  History,
  Loader2,
  Pencil,
  Plus,
  Shield,
  Trash2,
  Upload,
} from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Switch } from '@/components/ui/switch';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import {
  dlpApi,
  type DlpAction,
  type DlpRule,
  type DlpRuleRevision,
  type DlpSeverity,
  type DlpTrigger,
} from '@/lib/api';
import { formatDateTime } from '@/lib/format';

const severityColors: Record<string, string> = {
  critical: 'bg-destructive/15 text-destructive',
  high: 'bg-warning/15 text-warning',
  medium: 'bg-info/15 text-info',
  low: 'bg-muted text-muted-foreground',
};

const triggerLabels: Record<string, string> = {
  usb_storage: 'USB Storage',
  usb_file: 'USB File',
  software_install: 'Software Install',
  software_uninstall: 'Software Uninstall',
  cloud_sync_folder: 'Cloud Sync Folder',
  file_transfer: 'File Transfer',
  email: 'Email',
};

const actionLabels: Record<string, string> = {
  alert: 'Alert Only',
  block: 'Block',
  alert_block: 'Alert + Block',
};

const TRIGGER_OPTIONS: { value: DlpTrigger; label: string }[] = [
  { value: 'usb_storage', label: 'USB Storage' },
  { value: 'usb_file', label: 'USB File' },
  { value: 'software_install', label: 'Software Install' },
  { value: 'software_uninstall', label: 'Software Uninstall' },
  { value: 'cloud_sync_folder', label: 'Cloud Sync Folder' },
  { value: 'file_transfer', label: 'File Transfer' },
  { value: 'email', label: 'Email' },
];

type FormState = {
  name: string;
  trigger: DlpTrigger;
  action: DlpAction;
  severity: DlpSeverity;
  enabled: boolean;
};

const emptyForm = (): FormState => ({
  name: '',
  trigger: 'usb_storage',
  action: 'alert',
  severity: 'high',
  enabled: true,
});

function formFromRule(rule: DlpRule): FormState {
  return {
    name: rule.name,
    trigger: rule.trigger,
    action: rule.action,
    severity: rule.severity,
    enabled: rule.enabled,
  };
}

function RuleDetailBody({
  rule,
  revisions,
  historyLoading,
}: {
  rule: DlpRule;
  revisions: DlpRuleRevision[];
  historyLoading: boolean;
}) {
  return (
    <div className="space-y-4 mt-2">
      <dl className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-sm">
        <div>
          <dt className="text-xs text-muted-foreground">Status</dt>
          <dd className="font-medium">{rule.enabled ? 'Active' : 'Inactive'}</dd>
        </div>
        <div>
          <dt className="text-xs text-muted-foreground">Trigger</dt>
          <dd className="font-medium">{triggerLabels[rule.trigger] ?? rule.trigger}</dd>
        </div>
        <div>
          <dt className="text-xs text-muted-foreground">Action</dt>
          <dd className="font-medium">{actionLabels[rule.action] ?? rule.action}</dd>
        </div>
        <div>
          <dt className="text-xs text-muted-foreground">Severity</dt>
          <dd className="font-medium capitalize">{rule.severity}</dd>
        </div>
        <div>
          <dt className="text-xs text-muted-foreground">Created</dt>
          <dd className="text-xs">{formatDateTime(rule.createdAt)}</dd>
        </div>
        <div>
          <dt className="text-xs text-muted-foreground">Updated</dt>
          <dd className="text-xs">{formatDateTime(rule.updatedAt)}</dd>
        </div>
      </dl>

      <section>
        <h4 className="text-sm font-semibold mb-2">Revision history</h4>
        {historyLoading && (
          <div className="flex justify-center py-4">
            <Loader2 className="w-5 h-5 animate-spin text-primary" />
          </div>
        )}
        {!historyLoading && revisions.length === 0 && (
          <p className="text-xs text-muted-foreground">No prior revisions yet.</p>
        )}
        {!historyLoading && revisions.length > 0 && (
          <ul className="space-y-2 max-h-56 overflow-y-auto">
            {revisions.map((rev) => (
              <li
                key={rev.id}
                className="rounded-lg border border-border px-3 py-2 text-xs space-y-1"
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="font-medium">Rev {rev.revision}</span>
                  <span className="text-muted-foreground">{formatDateTime(rev.createdAt)}</span>
                </div>
                <p className="text-muted-foreground">
                  {rev.changedByName || 'Unknown'} · {rev.enabled ? 'Active' : 'Inactive'} ·{' '}
                  {triggerLabels[rev.trigger] ?? rev.trigger} ·{' '}
                  {actionLabels[rev.action] ?? rev.action} · {rev.severity}
                </p>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

export default function DLPRulesPage() {
  const qc = useQueryClient();
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editing, setEditing] = useState<DlpRule | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm());
  const [detailRule, setDetailRule] = useState<DlpRule | null>(null);
  const [historyRule, setHistoryRule] = useState<DlpRule | null>(null);
  const [publishMsg, setPublishMsg] = useState('');

  const { data, isLoading, isError } = useQuery({
    queryKey: ['dlp-rules'],
    queryFn: () => dlpApi.rules.list(),
  });

  const rules = data?.rules ?? [];

  const detailHistoryQuery = useQuery({
    queryKey: ['dlp-rules-history', detailRule?.id],
    queryFn: () => dlpApi.rules.history(detailRule!.id),
    enabled: !!detailRule,
  });

  const historyQuery = useQuery({
    queryKey: ['dlp-rules-history', historyRule?.id],
    queryFn: () => dlpApi.rules.history(historyRule!.id),
    enabled: !!historyRule,
  });

  const openCreate = () => {
    setEditing(null);
    setForm(emptyForm());
    setDialogOpen(true);
  };

  const openEdit = (rule: DlpRule) => {
    setEditing(rule);
    setForm(formFromRule(rule));
    setDialogOpen(true);
  };

  const saveMutation = useMutation({
    mutationFn: async () => {
      const payload = {
        name: form.name.trim(),
        trigger: form.trigger,
        action: form.action,
        severity: form.severity,
        extensions: [] as string[],
        patterns: [] as string[],
        scopeAll: true,
        enabled: form.enabled,
      };
      if (editing) {
        await dlpApi.rules.update(editing.id, payload);
      } else {
        await dlpApi.rules.create(payload);
      }
      return dlpApi.rules.publish();
    },
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ['dlp-rules'] });
      setDialogOpen(false);
      setEditing(null);
      setForm(emptyForm());
      setPublishMsg(`Published policy v${res.policyVersion} (${res.ruleCount} rules)`);
    },
  });

  const publishMutation = useMutation({
    mutationFn: () => dlpApi.rules.publish(),
    onSuccess: (res) => {
      setPublishMsg(`Published policy v${res.policyVersion} (${res.ruleCount} rules)`);
    },
  });

  const toggleMutation = useMutation({
    mutationFn: async ({ id, enabled }: { id: string; enabled: boolean }) => {
      await dlpApi.rules.update(id, { enabled });
      return dlpApi.rules.publish();
    },
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ['dlp-rules'] });
      setPublishMsg(
        `Published policy v${res.policyVersion} (${res.ruleCount} rules) — agents will refresh within ~30s`,
      );
    },
  });

  const deleteMutation = useMutation({
    mutationFn: async (id: string) => {
      await dlpApi.rules.delete(id);
      return dlpApi.rules.publish();
    },
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ['dlp-rules'] });
      setPublishMsg(`Published policy v${res.policyVersion} after delete`);
    },
  });

  const canSave = form.name.trim().length > 0;

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <div>
          <h3 className="font-display font-bold text-lg text-foreground">DLP Rules</h3>
          <p className="text-xs text-muted-foreground mt-0.5">
            USB and non-web triggers (relational). Domain / path / schedule rules live under Web Blocker.
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
            <Plus className="w-4 h-4" /> Add Rule
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
      {isError && <p className="text-sm text-destructive">Failed to load rules</p>}
      {!isLoading && !isError && rules.length === 0 && (
        <p className="text-sm text-muted-foreground py-8 text-center">
          No DLP rules yet. Create one and Publish so Windows agents can pull it.
        </p>
      )}

      <div className="space-y-3">
        {rules.map((rule, i) => (
          <motion.div
            key={rule.id}
            initial={{ opacity: 0, y: 10 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ delay: i * 0.03 }}
            className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all"
          >
            <div className="flex items-start justify-between gap-3">
              <button
                type="button"
                onClick={() => setDetailRule(rule)}
                className="flex items-center gap-3 min-w-0 text-left hover:opacity-90"
              >
                <Shield className="w-5 h-5 text-primary flex-shrink-0" />
                <div className="min-w-0">
                  <h4 className="font-display font-bold text-foreground truncate">{rule.name}</h4>
                  <p className="text-xs text-muted-foreground mt-0.5">
                    Trigger: {triggerLabels[rule.trigger] ?? rule.trigger}
                  </p>
                </div>
              </button>
              <div className="flex items-center gap-2 flex-shrink-0 flex-wrap justify-end">
                <label className="inline-flex items-center gap-2 cursor-pointer">
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
                  onClick={() => setDetailRule(rule)}
                  className="p-1.5 rounded hover:bg-muted"
                  aria-label="View rule detail"
                >
                  <Eye className="w-3.5 h-3.5 text-muted-foreground" />
                </button>
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
        ))}
      </div>

      {/* Create / Edit */}
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
              {editing ? 'Edit DLP rule' : 'New DLP rule'}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-3 mt-2">
            <Input
              placeholder="Rule Name *"
              value={form.name}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            />
            <Select
              value={form.trigger}
              onValueChange={(v) => setForm((f) => ({ ...f, trigger: v as DlpTrigger }))}
            >
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {TRIGGER_OPTIONS.map((opt) => (
                  <SelectItem key={opt.value} value={opt.value}>
                    {opt.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <div className="grid grid-cols-2 gap-3">
              <Select
                value={form.action}
                onValueChange={(v) => setForm((f) => ({ ...f, action: v as DlpAction }))}
              >
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
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
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
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
            <Button
              className="w-full gradient-primary text-primary-foreground"
              disabled={!canSave || saveMutation.isPending}
              onClick={() => saveMutation.mutate()}
            >
              {saveMutation.isPending ? (
                <Loader2 className="w-4 h-4 animate-spin" />
              ) : editing ? (
                'Save & Publish'
              ) : (
                'Create & Publish'
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

      {/* Detail */}
      <Dialog open={!!detailRule} onOpenChange={(open) => !open && setDetailRule(null)}>
        <DialogContent className="bg-card max-h-[90vh] overflow-y-auto sm:max-w-lg">
          <DialogHeader>
            <DialogTitle className="font-display">{detailRule?.name ?? 'Rule detail'}</DialogTitle>
          </DialogHeader>
          {detailRule && (
            <>
              <RuleDetailBody
                rule={detailRule}
                revisions={detailHistoryQuery.data?.revisions ?? []}
                historyLoading={detailHistoryQuery.isLoading}
              />
              <div className="flex gap-2 pt-2">
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1"
                  onClick={() => {
                    setDetailRule(null);
                    openEdit(detailRule);
                  }}
                >
                  <Pencil className="w-3.5 h-3.5" /> Edit
                </Button>
              </div>
            </>
          )}
        </DialogContent>
      </Dialog>

      {/* History-only */}
      <Dialog open={!!historyRule} onOpenChange={(open) => !open && setHistoryRule(null)}>
        <DialogContent className="bg-card max-h-[90vh] overflow-y-auto sm:max-w-lg">
          <DialogHeader>
            <DialogTitle className="font-display">
              History — {historyRule?.name ?? 'Rule'}
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
          {!historyQuery.isLoading && !historyQuery.isError && (
            <ul className="space-y-2 mt-2">
              {(historyQuery.data?.revisions ?? []).length === 0 && (
                <p className="text-xs text-muted-foreground">No prior revisions yet.</p>
              )}
              {(historyQuery.data?.revisions ?? []).map((rev) => (
                <li
                  key={rev.id}
                  className="rounded-lg border border-border px-3 py-2 text-xs space-y-1"
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="font-medium">Rev {rev.revision}</span>
                    <span className="text-muted-foreground">{formatDateTime(rev.createdAt)}</span>
                  </div>
                  <p className="text-muted-foreground">
                    {rev.changedByName || 'Unknown'} · {rev.enabled ? 'Active' : 'Inactive'} ·{' '}
                    {triggerLabels[rev.trigger] ?? rev.trigger} ·{' '}
                    {actionLabels[rev.action] ?? rev.action} · {rev.severity}
                  </p>
                </li>
              ))}
            </ul>
          )}
        </DialogContent>
      </Dialog>
    </div>
  );
}
