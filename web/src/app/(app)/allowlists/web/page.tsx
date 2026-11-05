'use client';

import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Globe2, Loader2, Lock, Upload } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Switch } from '@/components/ui/switch';
import { dlpApi } from '@/lib/api';

function uniqueDomains(list: string[]): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const raw of list) {
    const d = raw.trim().toLowerCase();
    if (!d || seen.has(d)) continue;
    seen.add(d);
    out.push(d);
  }
  return out;
}

function DomainChipInput({
  domains,
  onChange,
  placeholder,
  max,
}: {
  domains: string[];
  onChange: (next: string[]) => void;
  placeholder?: string;
  max?: number;
}) {
  const [draft, setDraft] = useState('');
  const atCap = typeof max === 'number' && max > 0 && domains.length >= max;

  const commit = (raw: string) => {
    const parts = raw
      .split(/[,;\s]+/)
      .map((s) => s.trim().toLowerCase())
      .filter(Boolean);
    if (parts.length === 0) return;
    let next = uniqueDomains([...domains, ...parts]);
    if (typeof max === 'number' && max > 0) next = next.slice(0, max);
    onChange(next);
    setDraft('');
  };

  return (
    <div className="mt-1 rounded-md border border-border bg-background px-2 py-1.5 focus-within:ring-1 focus-within:ring-ring">
      <div className="flex flex-wrap gap-1">
        {domains.map((d) => (
          <span
            key={d}
            className="inline-flex items-center gap-1 rounded bg-muted px-1.5 py-0.5 text-[11px] font-mono"
          >
            {d}
            <button
              type="button"
              className="text-muted-foreground hover:text-foreground"
              onClick={() => onChange(domains.filter((x) => x !== d))}
              aria-label={`Remove ${d}`}
            >
              ×
            </button>
          </span>
        ))}
        <input
          className="min-w-[10rem] flex-1 bg-transparent text-sm outline-none disabled:opacity-50"
          value={draft}
          disabled={atCap}
          placeholder={placeholder}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' || e.key === ',') {
              e.preventDefault();
              commit(draft);
            } else if (e.key === 'Backspace' && draft === '' && domains.length > 0) {
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

/**
 * Web Allow Lists — RBAC `allowlists/web`.
 * Draft hosts + lockdown switch; Publish compiles into the DLP policy bundle.
 * After employee login the Windows service flushes DNS/CDN pins, pulls this
 * allow list, and default-denies everything else (bootstrap/API still open).
 */
export default function WebAllowListsPage() {
  const qc = useQueryClient();
  const { data, isLoading, isError } = useQuery({
    queryKey: ['dlp-lockdown'],
    queryFn: () => dlpApi.lockdown.get(),
  });
  const [hosts, setHosts] = useState<string[]>([]);
  const [dirty, setDirty] = useState(false);

  useEffect(() => {
    if (data && !dirty) setHosts(data.allowHosts ?? []);
  }, [data, dirty]);

  const max = data?.maxAllowHosts ?? 0;

  const switchMutation = useMutation({
    mutationFn: (lockdown: boolean) => dlpApi.lockdown.update({ lockdown }),
    onSuccess: (res) => {
      qc.setQueryData(['dlp-lockdown'], res);
      toast.success(res.lockdown ? 'Default-deny kept on (draft)' : 'Open browsing (draft)', {
        description: res.lockdown
          ? 'PCs already block by default after login. Publish only to update the allow list.'
          : 'Click Publish to open general browsing on employee PCs (opt-out).',
      });
    },
    onError: (err: Error) => toast.error('Failed to update', { description: err.message }),
  });

  const hostsMutation = useMutation({
    mutationFn: () => dlpApi.lockdown.update({ allowHosts: uniqueDomains(hosts) }),
    onSuccess: (res) => {
      qc.setQueryData(['dlp-lockdown'], res);
      setHosts(res.allowHosts ?? []);
      setDirty(false);
      toast.success('Web allow list saved (draft)', {
        description: 'Click Publish so employee PCs can reach these hosts.',
      });
    },
    onError: (err: Error) => toast.error('Failed to save allow list', { description: err.message }),
  });

  const publishMutation = useMutation({
    mutationFn: () => dlpApi.rules.publish(),
    onSuccess: (res) => {
      toast.success(`Published policy v${res.policyVersion}`, {
        description: 'Employee PCs will pull the new allow list on the next policy poll.',
      });
    },
    onError: (err: Error) => toast.error('Publish failed', { description: err.message }),
  });

  return (
    <div className="space-y-4 max-w-3xl">
      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div>
          <h1 className="text-xl font-display font-bold text-foreground flex items-center gap-2">
            <Globe2 className="w-5 h-5 text-primary" />
            Web Allow Lists
          </h1>
          <p className="text-sm text-muted-foreground mt-1">
            After install and login, Windows PCs block every website by default (no Publish needed).
            Add hosts here, then Publish — only those sites (plus platform destinations) open.
          </p>
        </div>
        <Button
          onClick={() => publishMutation.mutate()}
          disabled={publishMutation.isPending}
        >
          {publishMutation.isPending ? (
            <Loader2 className="w-4 h-4 animate-spin mr-1" />
          ) : (
            <Upload className="w-4 h-4 mr-1" />
          )}
          Publish
        </Button>
      </div>

      <div className="bg-card rounded-xl border border-border p-5 shadow-card space-y-4">
        <div className="flex items-start justify-between gap-3 flex-wrap">
          <div className="min-w-0">
            <h2 className="font-display font-bold text-foreground flex items-center gap-2">
              <Lock className="w-4 h-4 text-primary" /> Default-deny web
            </h2>
            <p className="text-xs text-muted-foreground mt-0.5">
              Leave On (recommended). Employee PCs already block all sites after login.
              Turn Off + Publish only to temporarily allow general browsing. The allow list
              below still needs Publish to open specific hosts.
            </p>
          </div>
          <label className="inline-flex items-center gap-2 cursor-pointer">
            <Switch
              checked={data?.lockdown ?? false}
              disabled={isLoading || isError || switchMutation.isPending}
              onCheckedChange={(checked) => switchMutation.mutate(checked)}
              aria-label="Toggle default-deny web"
            />
            <span
              className={`text-xs font-medium ${data?.lockdown ? 'text-success' : 'text-muted-foreground'}`}
            >
              {data?.lockdown ? 'On' : 'Off'}
            </span>
          </label>
        </div>

        {isLoading && <Loader2 className="w-4 h-4 animate-spin text-primary" />}
        {isError && <p className="text-xs text-destructive">Failed to load allow list settings</p>}

        {data && (
          <>
            <div>
              <div className="flex items-center justify-between">
                <label className="text-xs font-medium text-muted-foreground">Allowed websites</label>
                <span className="text-[10px] text-muted-foreground">
                  {hosts.length}/{max}
                </span>
              </div>
              <DomainChipInput
                domains={hosts}
                max={max}
                onChange={(next) => {
                  setHosts(uniqueDomains(next));
                  setDirty(true);
                }}
                placeholder="contoso.sharepoint.com — Enter to add"
              />
              {hosts.length >= max && max > 0 && (
                <p className="text-[10px] text-warning mt-1">Maximum of {max} hosts reached.</p>
              )}
              <div className="mt-2 flex items-center gap-2">
                <Button
                  size="sm"
                  variant="outline"
                  disabled={!dirty || hostsMutation.isPending}
                  onClick={() => hostsMutation.mutate()}
                >
                  {hostsMutation.isPending && <Loader2 className="w-4 h-4 animate-spin mr-1" />}
                  Save allow list
                </Button>
                {dirty && (
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => {
                      setHosts(data.allowHosts ?? []);
                      setDirty(false);
                    }}
                  >
                    Discard
                  </Button>
                )}
              </div>
            </div>

            <div>
              <label className="text-xs font-medium text-muted-foreground">
                Platform allow (always permitted, read-only)
              </label>
              <div className="flex flex-wrap gap-1 mt-1">
                {(data.platformAllow ?? []).length === 0 && (
                  <span className="text-[10px] text-muted-foreground">
                    None compiled yet — set INSTANCE_PUBLIC_URL / TURN on the API host.
                  </span>
                )}
                {(data.platformAllow ?? []).map((d) => (
                  <code
                    key={d}
                    className="bg-muted px-1.5 py-0.5 rounded text-[10px] text-muted-foreground"
                  >
                    {d}
                  </code>
                ))}
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
