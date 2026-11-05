'use client';

import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { AppWindow, Loader2, Plus, Trash2, Upload } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { dlpApi, type DlpLockdownAllowApp } from '@/lib/api';

function normalizeApp(a: DlpLockdownAllowApp): DlpLockdownAllowApp | null {
  const publisher = (a.publisher ?? '').trim();
  const product = (a.product ?? '').trim();
  let sha256 = (a.sha256 ?? '').trim().toLowerCase().replace(/^sha256:/, '');
  if (!publisher && sha256.length !== 64) return null;
  if (sha256 && sha256.length !== 64) return null;
  if (sha256 && !/^[0-9a-f]{64}$/.test(sha256)) return null;
  return { publisher: publisher || undefined, product: product || undefined, sha256: sha256 || undefined };
}

/**
 * App Allow List — RBAC `allowlists/apps`.
 * Draft publisher / SHA-256 entries publish into `allow.apps`. Windows enforcement
 * (WDAC/AppLocker) is the next phase; the list is already snapshotted for agents.
 */
export default function AppAllowListPage() {
  const qc = useQueryClient();
  const { data, isLoading, isError } = useQuery({
    queryKey: ['dlp-lockdown'],
    queryFn: () => dlpApi.lockdown.get(),
  });
  const [apps, setApps] = useState<DlpLockdownAllowApp[]>([]);
  const [dirty, setDirty] = useState(false);
  const [publisher, setPublisher] = useState('');
  const [product, setProduct] = useState('');
  const [sha256, setSha256] = useState('');

  useEffect(() => {
    if (data && !dirty) setApps(data.allowApps ?? []);
  }, [data, dirty]);

  const max = data?.maxAllowApps ?? 0;

  const saveMutation = useMutation({
    mutationFn: () => dlpApi.lockdown.update({ allowApps: apps }),
    onSuccess: (res) => {
      qc.setQueryData(['dlp-lockdown'], res);
      setApps(res.allowApps ?? []);
      setDirty(false);
      toast.success('App allow list saved (draft)', {
        description: 'Click Publish to include in the next policy bundle.',
      });
    },
    onError: (err: Error) => toast.error('Failed to save', { description: err.message }),
  });

  const publishMutation = useMutation({
    mutationFn: () => dlpApi.rules.publish(),
    onSuccess: (res) => {
      toast.success(`Published policy v${res.policyVersion}`);
    },
    onError: (err: Error) => toast.error('Publish failed', { description: err.message }),
  });

  const addRow = () => {
    const row = normalizeApp({ publisher, product, sha256 });
    if (!row) {
      toast.error('Need a publisher (e.g. CN=…) and/or a 64-hex SHA-256');
      return;
    }
    if (apps.length >= max && max > 0) {
      toast.error(`Maximum of ${max} app entries`);
      return;
    }
    setApps((prev) => [...prev, row]);
    setDirty(true);
    setPublisher('');
    setProduct('');
    setSha256('');
  };

  return (
    <div className="space-y-4 max-w-3xl">
      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div>
          <h1 className="text-xl font-display font-bold text-foreground flex items-center gap-2">
            <AppWindow className="w-5 h-5 text-primary" />
            App Allow List
          </h1>
          <p className="text-sm text-muted-foreground mt-1">
            Allow programs by code-signing publisher and/or file SHA-256 — never by display name alone.
            Entries publish into the policy bundle; Windows enforce mode ships in a later phase.
          </p>
        </div>
        <Button onClick={() => publishMutation.mutate()} disabled={publishMutation.isPending}>
          {publishMutation.isPending ? (
            <Loader2 className="w-4 h-4 animate-spin mr-1" />
          ) : (
            <Upload className="w-4 h-4 mr-1" />
          )}
          Publish
        </Button>
      </div>

      <div className="bg-card rounded-xl border border-border p-5 shadow-card space-y-4">
        {isLoading && <Loader2 className="w-4 h-4 animate-spin text-primary" />}
        {isError && <p className="text-xs text-destructive">Failed to load app allow list</p>}

        {data && (
          <>
            <div className="flex items-center justify-between text-xs text-muted-foreground">
              <span>{apps.length}/{max} entries</span>
              {!data.lockdown && (
                <span className="text-warning">
                  Web default-deny is off — turn it on under Web Allow Lists for network lockdown.
                </span>
              )}
            </div>

            <ul className="divide-y divide-border rounded-lg border border-border">
              {apps.length === 0 && (
                <li className="px-3 py-6 text-center text-sm text-muted-foreground">
                  No allowed apps yet.
                </li>
              )}
              {apps.map((a, i) => (
                <li key={`${a.publisher}-${a.sha256}-${i}`} className="flex items-start gap-2 px-3 py-2">
                  <div className="min-w-0 flex-1 text-xs font-mono break-all">
                    {a.publisher && <div>publisher: {a.publisher}</div>}
                    {a.product && <div className="text-muted-foreground">product: {a.product}</div>}
                    {a.sha256 && <div>sha256: {a.sha256}</div>}
                  </div>
                  <Button
                    size="icon"
                    variant="ghost"
                    className="shrink-0 h-8 w-8"
                    onClick={() => {
                      setApps((prev) => prev.filter((_, j) => j !== i));
                      setDirty(true);
                    }}
                    aria-label="Remove"
                  >
                    <Trash2 className="w-4 h-4" />
                  </Button>
                </li>
              ))}
            </ul>

            <div className="grid gap-2 sm:grid-cols-2">
              <div className="sm:col-span-2">
                <label className="text-xs font-medium text-muted-foreground">Publisher (subject)</label>
                <Input
                  value={publisher}
                  onChange={(e) => setPublisher(e.target.value)}
                  placeholder="CN=Microsoft Corporation, …"
                  className="mt-1 font-mono text-xs"
                />
              </div>
              <div>
                <label className="text-xs font-medium text-muted-foreground">Product (optional)</label>
                <Input
                  value={product}
                  onChange={(e) => setProduct(e.target.value)}
                  placeholder="Microsoft Office"
                  className="mt-1"
                />
              </div>
              <div>
                <label className="text-xs font-medium text-muted-foreground">SHA-256 (64 hex)</label>
                <Input
                  value={sha256}
                  onChange={(e) => setSha256(e.target.value)}
                  placeholder="abcdef…"
                  className="mt-1 font-mono text-xs"
                />
              </div>
            </div>

            <div className="flex flex-wrap gap-2">
              <Button size="sm" variant="secondary" onClick={addRow}>
                <Plus className="w-4 h-4 mr-1" /> Add
              </Button>
              <Button
                size="sm"
                variant="outline"
                disabled={!dirty || saveMutation.isPending}
                onClick={() => saveMutation.mutate()}
              >
                {saveMutation.isPending && <Loader2 className="w-4 h-4 animate-spin mr-1" />}
                Save draft
              </Button>
              {dirty && (
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => {
                    setApps(data.allowApps ?? []);
                    setDirty(false);
                  }}
                >
                  Discard
                </Button>
              )}
            </div>
          </>
        )}
      </div>
    </div>
  );
}
